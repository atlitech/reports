using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Atli.Reports.Client.Http;
using Atli.Reports.Engine;
using Atli.Reports.Hosting.Renderers;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// Gateway mode's <see cref="IHtmlToPdfConverter"/>: forwards the conversion that
/// <c>/convert</c> already validated to the tenant's own renderer and relays its PDF.
/// </summary>
/// <remarks>
/// <para>
/// The tenant comes from <see cref="GatewayTenantFeature"/>, which the gateway middleware sets from
/// the authenticated caller's membership before the body is read. The renderer request is built
/// from scratch: the renderer's own credential, the conversion as JSON in the client's wire format,
/// and nothing of the caller's request (no headers, credentials, cookies, or trace context).
/// </para>
/// <para>
/// The renderer may be compromised, so its answer is untrusted input. A <c>200</c> counts only as
/// <c>application/pdf</c> starting with <c>%PDF-</c>, checked before the caller's response starts;
/// the PDF then streams through with a byte count against
/// <see cref="GatewayOptions.MaxPdfBytes"/>. A failure after the response started returns an
/// error, and <c>/convert</c> aborts the connection, so the caller never gets a truncated
/// <c>200</c>. Error bodies are read only up to a cap; see <see cref="RendererFailures"/>.
/// </para>
/// <para>
/// A conversion is sent again only while its renderer wakes (see <see cref="SandboxWaker"/>) or is
/// busy, a few times with a short random pause, so a burst does not fail for a renderer that frees
/// up within moments.
/// </para>
/// </remarks>
internal sealed partial class RendererGateway(
  IHttpContextAccessor httpContextAccessor,
  RendererDirectory directory,
  IHttpClientFactory httpClientFactory,
  GatewayOptions settings,
  TimeProvider timeProvider,
  ILogger<RendererGateway> logger,
  SandboxWaker? waker
) : IHtmlToPdfConverter
{
  /// <summary>The named <see cref="HttpClient"/> renderer requests go through.</summary>
  public const string HttpClientName = "Atli.Reports.Gateway.Renderers";

  /// <summary>The header the renderer authenticates the gateway with: its own API key.</summary>
  private const string ApiKeyHeader = "X-Reports-Api-Key";

  private const int CopyBufferSize = 81920;

  /// <summary>
  /// The caller's message for a renderer's <c>413</c>. The gateway sends the HTML in the client's
  /// wire format, which escapes characters outside the Basic Multilingual Plane (an emoji's 4 UTF-8
  /// bytes become a 12-byte <c>\uD83D\uDE00</c>) and a few others, so a body under the gateway's
  /// limit can exceed the renderer's.
  /// </summary>
  private const string TooLargeForRenderer =
    "The document is larger than the tenant's renderer accepts once encoded for it. Emoji and other characters outside the Basic Multilingual Plane count up to three times their size; send a smaller document.";

  private static readonly TimeSpan FirstWakeBackoff = TimeSpan.FromMilliseconds(250);

  private static readonly TimeSpan MaxWakeBackoff = TimeSpan.FromSeconds(1);

  /// <summary>How many times one conversion is resent to a busy renderer.</summary>
  private const int MaxBusyRetries = 8;

  private static readonly TimeSpan MinBusyBackoff = TimeSpan.FromMilliseconds(100);

  private static readonly TimeSpan MaxBusyBackoff = TimeSpan.FromMilliseconds(500);

  /// <inheritdoc />
  /// <remarks>The server only streams; nothing calls this overload.</remarks>
  public ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  ) => throw new NotSupportedException("The gateway streams the PDF to its destination.");

  /// <inheritdoc />
  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(destination);
    var tenant =
      httpContextAccessor.HttpContext?.Features.Get<GatewayTenantFeature>()
      ?? throw new InvalidOperationException(
        "Gateway conversions need the tenant that the gateway middleware resolves."
      );
    var tenantId = tenant.TenantId;

    using var deadline = new CancellationTokenSource(settings.RendererTimeout, timeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken,
      deadline.Token
    );
    Attempt attempt = new(tenantId, html, options, destination, linked.Token);
    try
    {
      RendererRecord? record;
      try
      {
        // The middleware already read it for the tenant's limit, unless that read failed.
        record = tenant.RecordLoaded
          ? tenant.Record
          : await directory.GetAsync(tenantId, linked.Token);
      }
      catch (Exception exception) when (!linked.IsCancellationRequested)
      {
        LogDirectoryFailed(logger, tenantId, exception);
        return Unavailable("The renderer directory is unavailable.");
      }

      if (record is null)
      {
        LogNoRenderer(logger, tenantId);
        return Unavailable("The tenant has no renderer.");
      }

      if (
        record.TenantId != tenantId
        || !GatewayOptions.IsRendererUrl(record.Url, settings.AllowHttpRenderers)
        || !GatewayOptions.IsApiKey(record.ApiKey)
      )
      {
        LogInvalidRecord(logger, tenantId);
        return Unavailable("The tenant's renderer is unavailable.");
      }

      return await ForwardAsync(attempt, record);
    }
    catch (DestinationWriteException exception)
    {
      // The caller's response failed. Its own cancellation (or the deadline) is a conversion
      // outcome; anything else propagates, as the IHtmlToPdfConverter contract asks.
      if (exception.InnerException is OperationCanceledException && linked.IsCancellationRequested)
      {
        return Canceled(tenantId, cancellationToken);
      }

      ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
      throw;
    }
    catch (OperationCanceledException) when (linked.IsCancellationRequested)
    {
      return Canceled(tenantId, cancellationToken);
    }
  }

  /// <summary>
  /// Sends the conversion, resending it while a suspended sandbox wakes or while the renderer is
  /// busy, and relays the answer.
  /// </summary>
  private async Task<OneOf<Success, ConversionError>> ForwardAsync(
    Attempt attempt,
    RendererRecord record
  )
  {
    var client = httpClientFactory.CreateClient(HttpClientName);
    var convertUri = new Uri(record.Url.AbsoluteUri.TrimEnd('/') + "/convert", UriKind.Absolute);
    var canWake = waker is not null && record.SandboxId is not null;
    long? wakeStarted = null;
    var backoff = FirstWakeBackoff;
    var busyRetries = 0;

    while (true)
    {
      HttpResponseMessage response;
      var sentAt = timeProvider.GetTimestamp();
      // A fresh request each time: a sent HttpRequestMessage cannot be sent again.
      using (var request = CreateRequest(convertUri, record.ApiKey, attempt))
      {
        try
        {
          response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            attempt.CancellationToken
          );
        }
        // A refused or reset connection, or one that did not open within the handler's connect
        // timeout (a stalled TCP or TLS handshake), which surfaces as a cancellation that the
        // conversion's own token did not cause.
        catch (Exception exception)
          when (exception is HttpRequestException or OperationCanceledException
            && !attempt.CancellationToken.IsCancellationRequested
          )
        {
          if (wakeStarted is not null && await BackOffAsync())
          {
            continue;
          }

          LogUnreachable(
            logger,
            attempt.TenantId,
            record.SandboxId,
            exception is HttpRequestException { HttpRequestError: var error }
              ? error.ToString()
              : "ConnectTimeout"
          );
          // The record may be stale (a renderer recreated elsewhere): read it again next time.
          directory.Evict(attempt.TenantId, record);
          return wakeStarted is null
            ? Unavailable("The tenant's renderer could not be reached.")
            : WakeTimedOut(attempt, record);
        }
      }

      using (response)
      {
        if (response.StatusCode == HttpStatusCode.OK)
        {
          return await RelayAsync(attempt, record, response);
        }

        var failure = await RendererFailures.ReadAsync(response, attempt.CancellationToken);
        if (failure.SandboxNotRunning && canWake)
        {
          wakeStarted ??= timeProvider.GetTimestamp();
          if (WakeWindowLeft() <= TimeSpan.Zero)
          {
            return WakeTimedOut(attempt, record);
          }

          switch (
            await waker!.WakeAsync(
              attempt.TenantId,
              record.SandboxId!,
              sentAt,
              attempt.CancellationToken
            )
          )
          {
            case WakeResult.Resumed:
              // Straight to a resend.
              continue;
            case WakeResult.Running:
              LogNotRunningWhileRunning(logger, attempt.TenantId, record.SandboxId);
              return Unavailable("The tenant's renderer is unavailable.");
            case WakeResult.Missing:
              LogNoSandbox(logger, attempt.TenantId, record.SandboxId);
              directory.Evict(attempt.TenantId, record);
              return Unavailable("The tenant's renderer is not running.");
            case WakeResult.Disabled:
              LogDisabled(logger, attempt.TenantId, record.SandboxId);
              return Unavailable("The tenant's renderer is not running.");
            default:
              if (await BackOffAsync())
              {
                continue;
              }

              return WakeTimedOut(attempt, record);
          }
        }

        if (wakeStarted is not null && failure.Status is 502 or 503 && !failure.IsBusy)
        {
          if (await BackOffAsync())
          {
            continue;
          }

          return WakeTimedOut(attempt, record);
        }

        // A renderer at capacity (its caller limit, or its queue) frees up within moments, and
        // after a wake every waiting request resends at once: spread the resends out.
        if (failure.IsBusy && busyRetries < MaxBusyRetries)
        {
          busyRetries++;
          var delay = TimeSpan.FromMilliseconds(
            RandomNumberGenerator.GetInt32(
              (int)MinBusyBackoff.TotalMilliseconds,
              (int)MaxBusyBackoff.TotalMilliseconds + 1
            )
          );
          LogBusyRetry(
            logger,
            attempt.TenantId,
            record.SandboxId,
            busyRetries,
            delay.TotalMilliseconds
          );
          await Task.Delay(delay, timeProvider, attempt.CancellationToken);
          continue;
        }

        return Map(attempt.TenantId, record, failure);
      }
    }

    TimeSpan WakeWindowLeft() =>
      wakeStarted is null
        ? settings.Wake.Timeout
        : settings.Wake.Timeout - timeProvider.GetElapsedTime(wakeStarted.Value);

    // Waits before the next resend, 250 ms growing to 1 s, at most until the wake window closes;
    // false once it has. A wait that ends with the window still gets its resend.
    async Task<bool> BackOffAsync()
    {
      var left = WakeWindowLeft();
      if (left <= TimeSpan.Zero)
      {
        return false;
      }

      await Task.Delay(backoff < left ? backoff : left, timeProvider, attempt.CancellationToken);
      backoff = backoff * 2 < MaxWakeBackoff ? backoff * 2 : MaxWakeBackoff;
      return true;
    }
  }

  private static HttpRequestMessage CreateRequest(Uri convertUri, string apiKey, Attempt attempt)
  {
    HttpRequestMessage request = new(HttpMethod.Post, convertUri)
    {
      // Serialized as it is sent, so the HTML is not copied into another buffer; every attempt
      // serializes it again. The options spell out every value, so the renderer's defaults never
      // decide the outcome.
      Content = JsonContent.Create(
        ConvertRequestBody.Create(attempt.Html, attempt.Options),
        RequestJson.TypeInfo
      ),
    };
    request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
    return request;
  }

  /// <summary>
  /// Relays a <c>200</c>: checks the type and the <c>%PDF-</c> prefix before writing anything, then
  /// streams the rest while counting it.
  /// </summary>
  private async Task<OneOf<Success, ConversionError>> RelayAsync(
    Attempt attempt,
    RendererRecord record,
    HttpResponseMessage response
  )
  {
    if (
      !string.Equals(
        response.Content.Headers.ContentType?.MediaType,
        "application/pdf",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      LogNotAPdf(
        logger,
        attempt.TenantId,
        record.SandboxId,
        "its content type is not application/pdf"
      );
      return NotAPdf();
    }

    // Without a length or chunked framing, a body cut short would look complete.
    if (
      response.Version.Major == 1
      && response.Content.Headers.ContentLength is null
      && response.Headers.TransferEncodingChunked != true
    )
    {
      LogNotAPdf(logger, attempt.TenantId, record.SandboxId, "its length is not framed");
      return NotAPdf();
    }

    if (response.Content.Headers.ContentLength > settings.MaxPdfBytes)
    {
      LogTooLarge(logger, attempt.TenantId, record.SandboxId, settings.MaxPdfBytes);
      return TooLarge();
    }

    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
    try
    {
      await using var body = await ReadRendererAsync(() =>
        response.Content.ReadAsStreamAsync(attempt.CancellationToken)
      );

      // Read until the prefix is complete; nothing reaches the caller before it is checked.
      var count = 0;
      while (count < 5)
      {
        var read = await ReadRendererAsync(() =>
          body.ReadAsync(buffer.AsMemory(count), attempt.CancellationToken).AsTask()
        );
        if (read == 0)
        {
          break;
        }

        count += read;
      }

      if (count < 5 || !buffer.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
      {
        LogNotAPdf(logger, attempt.TenantId, record.SandboxId, "it does not start with %PDF-");
        return NotAPdf();
      }

      long total = 0;
      while (count > 0)
      {
        total += count;
        if (total > settings.MaxPdfBytes)
        {
          LogTooLarge(logger, attempt.TenantId, record.SandboxId, settings.MaxPdfBytes);
          return TooLarge();
        }

        await WriteDestinationAsync(attempt, buffer.AsMemory(0, count));
        count = await ReadRendererAsync(() =>
          body.ReadAsync(buffer, attempt.CancellationToken).AsTask()
        );
      }

      return new Success();
    }
    catch (RendererReadException exception)
    {
      LogBrokenOff(
        logger,
        attempt.TenantId,
        record.SandboxId,
        exception.InnerException!.GetType().Name
      );
      return Unavailable("The tenant's renderer broke off the PDF.");
    }
    finally
    {
      // The PDF is the tenant's document; do not leave it in a pooled buffer.
      ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
  }

  /// <summary>Maps a renderer's error response onto the caller's error.</summary>
  private ConversionError Map(string tenantId, RendererRecord record, RendererFailure failure)
  {
    var kind = failure.Kind;

    // The renderer refusing the gateway is the gateway's credential problem, never the caller's:
    // telling the caller Unauthorized would send it after its own valid credentials.
    if (
      failure.Status == StatusCodes.Status401Unauthorized
      || (failure.IsProblem && failure.Status == StatusCodes.Status403Forbidden)
      || kind is ConversionErrorKind.Unauthorized or ConversionErrorKind.Forbidden
    )
    {
      LogCredentialRejected(logger, tenantId, record.SandboxId, failure.Status);
      // A rotated credential or a replaced renderer reaches the store first: read it again.
      directory.Evict(tenantId, record);
      return Unavailable("The tenant's renderer is unavailable.");
    }

    if (failure.SandboxNotRunning)
    {
      LogNotRunning(logger, tenantId, record.SandboxId);
      return Unavailable("The tenant's renderer is not running.");
    }

    // The platform has no renderer at the record's URL: it was deleted, or replaced since the
    // record was read. Not the document's failure; the next conversion reads the record again.
    if (failure.Proxy == ProxyAnswer.SandboxNotFound)
    {
      LogNotFound(logger, tenantId, record.SandboxId);
      directory.Evict(tenantId, record);
      return Unavailable("The tenant's renderer is unavailable.");
    }

    // The renderer's port does not admit the gateway's address: the gateway's configuration
    // problem, never the caller's or the document's.
    if (failure.Proxy == ProxyAnswer.AddressDenied)
    {
      LogAddressDenied(logger, tenantId, record.SandboxId);
      return Unavailable("The tenant's renderer is unavailable.");
    }

    // The renderer's body limit. The gateway accepted the caller's body, but re-encodes it for the
    // renderer, which can make it larger (see TooLargeForRenderer); the renderer's own words about
    // its request body would only confuse the caller.
    if (failure.Status == StatusCodes.Status413PayloadTooLarge)
    {
      LogRendererFailed(
        logger,
        tenantId,
        record.SandboxId,
        failure.Status,
        ConversionErrorKind.InvalidRequest
      );
      return new ConversionError(ConversionErrorKind.InvalidRequest, TooLargeForRenderer);
    }

    kind ??= failure.Status switch
    {
      StatusCodes.Status429TooManyRequests => ConversionErrorKind.Busy,
      >= 500 => ConversionErrorKind.BrowserUnavailable,
      _ => ConversionErrorKind.RenderFailed,
    };
    LogRendererFailed(logger, tenantId, record.SandboxId, failure.Status, kind.Value);

    // Only the request's own problems carry the renderer's words to the caller, sanitized and cut
    // short; anything else gets the gateway's fixed message.
    var detail = kind
      is ConversionErrorKind.InvalidRequest
        or ConversionErrorKind.SignalTimeout
        or ConversionErrorKind.PolicyDenied
      ? RendererFailures.Sanitize(failure.Detail)
      : null;
    return new ConversionError(kind.Value, detail ?? FixedMessage(kind.Value));
  }

  private static string FixedMessage(ConversionErrorKind kind) =>
    kind switch
    {
      ConversionErrorKind.InvalidRequest => "The tenant's renderer rejected the request.",
      ConversionErrorKind.SignalTimeout => "The document did not signal that it was ready.",
      ConversionErrorKind.PolicyDenied => "The document violates the rendering policy.",
      ConversionErrorKind.Busy => "The tenant's renderer is busy. Retry later.",
      ConversionErrorKind.BrowserUnavailable => "The tenant's renderer is unavailable.",
      ConversionErrorKind.Timeout => "The tenant's renderer did not finish the conversion in time.",
      _ => "The tenant's renderer could not convert the document.",
    };

  private ConversionError WakeTimedOut(Attempt attempt, RendererRecord record)
  {
    LogWakeTimedOut(logger, attempt.TenantId, record.SandboxId, settings.Wake.Timeout.TotalSeconds);
    return Unavailable("The tenant's renderer did not wake in time.");
  }

  private ConversionError Canceled(string tenantId, CancellationToken cancellationToken)
  {
    if (cancellationToken.IsCancellationRequested)
    {
      return new ConversionError(ConversionErrorKind.Canceled, "The conversion was canceled.");
    }

    LogTimedOut(logger, tenantId, settings.RendererTimeout.TotalSeconds);
    return new ConversionError(
      ConversionErrorKind.Timeout,
      "The tenant's renderer did not finish the conversion in time."
    );
  }

  private static ConversionError Unavailable(string message) =>
    new(ConversionErrorKind.BrowserUnavailable, message);

  private static ConversionError NotAPdf() =>
    new(ConversionErrorKind.RenderFailed, "The tenant's renderer did not answer with a PDF.");

  private static ConversionError TooLarge() =>
    new(ConversionErrorKind.RenderFailed, "The PDF is larger than the gateway relays.");

  /// <summary>Runs a read from the renderer, telling its failures apart from the caller's.</summary>
  private static async Task<T> ReadRendererAsync<T>(Func<Task<T>> read)
  {
    try
    {
      return await read();
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException)
    {
      throw new RendererReadException(exception);
    }
  }

  private static async Task WriteDestinationAsync(Attempt attempt, ReadOnlyMemory<byte> bytes)
  {
    try
    {
      await attempt.Destination.WriteAsync(bytes, attempt.CancellationToken);
    }
    catch (Exception exception)
    {
      throw new DestinationWriteException(exception);
    }
  }

  [LoggerMessage(
    EventId = 40,
    Level = LogLevel.Warning,
    Message = "Tenant {TenantId} has no renderer record."
  )]
  private static partial void LogNoRenderer(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 41,
    Level = LogLevel.Error,
    Message = "The renderer record store failed for tenant {TenantId}."
  )]
  private static partial void LogDirectoryFailed(
    ILogger logger,
    string tenantId,
    Exception exception
  );

  [LoggerMessage(
    EventId = 42,
    Level = LogLevel.Error,
    Message = "The renderer record of tenant {TenantId} is invalid: a different tenant, a URL that is not allowed, or an unusable API key."
  )]
  private static partial void LogInvalidRecord(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 43,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) could not be reached ({Reason})."
  )]
  private static partial void LogUnreachable(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    string reason
  );

  [LoggerMessage(
    EventId = 44,
    Level = LogLevel.Error,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) rejected the gateway's credential with {StatusCode}. Check the tenant's renderer record."
  )]
  private static partial void LogCredentialRejected(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    int statusCode
  );

  [LoggerMessage(
    EventId = 45,
    Level = LogLevel.Information,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) answered {StatusCode} ({Kind})."
  )]
  private static partial void LogRendererFailed(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    int statusCode,
    ConversionErrorKind kind
  );

  [LoggerMessage(
    EventId = 46,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) answered 200 with no PDF: {Reason}."
  )]
  private static partial void LogNotAPdf(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    string reason
  );

  [LoggerMessage(
    EventId = 47,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) sent a PDF over the {MaxPdfBytes}-byte limit."
  )]
  private static partial void LogTooLarge(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    long maxPdfBytes
  );

  [LoggerMessage(
    EventId = 48,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) broke off the PDF ({ErrorType})."
  )]
  private static partial void LogBrokenOff(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    string errorType
  );

  [LoggerMessage(
    EventId = 49,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} did not finish within {RendererTimeoutSeconds} s."
  )]
  private static partial void LogTimedOut(
    ILogger logger,
    string tenantId,
    double rendererTimeoutSeconds
  );

  [LoggerMessage(
    EventId = 50,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) is not running, and the gateway cannot wake it."
  )]
  private static partial void LogNotRunning(ILogger logger, string tenantId, string? sandboxId);

  [LoggerMessage(
    EventId = 51,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) did not wake within {WakeTimeoutSeconds} s."
  )]
  private static partial void LogWakeTimedOut(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    double wakeTimeoutSeconds
  );

  [LoggerMessage(
    EventId = 54,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} answered that sandbox {SandboxId} is not running, but the platform reports it running: the answer did not come from the platform's proxy."
  )]
  private static partial void LogNotRunningWhileRunning(
    ILogger logger,
    string tenantId,
    string? sandboxId
  );

  [LoggerMessage(
    EventId = 55,
    Level = LogLevel.Error,
    Message = "The renderer record of tenant {TenantId} names sandbox {SandboxId}, which does not exist."
  )]
  private static partial void LogNoSandbox(ILogger logger, string tenantId, string? sandboxId);

  [LoggerMessage(
    EventId = 57,
    Level = LogLevel.Warning,
    Message = "The platform found no renderer at the record of tenant {TenantId} (sandbox {SandboxId}): it was deleted or replaced. The record is read again."
  )]
  private static partial void LogNotFound(ILogger logger, string tenantId, string? sandboxId);

  [LoggerMessage(
    EventId = 58,
    Level = LogLevel.Error,
    Message = "The port of tenant {TenantId}'s renderer (sandbox {SandboxId}) refused the gateway's address (IpAccessDenied). Its allowed source ranges must include the gateway's outbound addresses."
  )]
  private static partial void LogAddressDenied(ILogger logger, string tenantId, string? sandboxId);

  [LoggerMessage(
    EventId = 59,
    Level = LogLevel.Warning,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) is disabled; its conversions fail until it is enabled."
  )]
  private static partial void LogDisabled(ILogger logger, string tenantId, string? sandboxId);

  [LoggerMessage(
    EventId = 56,
    Level = LogLevel.Debug,
    Message = "The renderer of tenant {TenantId} (sandbox {SandboxId}) was busy; resend {Retry} in {DelayMilliseconds} ms."
  )]
  private static partial void LogBusyRetry(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    int retry,
    double delayMilliseconds
  );

  /// <summary>
  /// One conversion's inputs, shared by its attempts. A class rather than a record, so no generated
  /// <c>ToString</c> can put the HTML in a log.
  /// </summary>
  private sealed class Attempt(
    string tenantId,
    string html,
    PdfOptions? options,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    public string TenantId => tenantId;

    public string Html => html;

    public PdfOptions? Options => options;

    public Stream Destination => destination;

    public CancellationToken CancellationToken => cancellationToken;
  }

  private sealed class RendererReadException(Exception inner)
    : Exception("Reading the renderer's response failed.", inner);

  private sealed class DestinationWriteException(Exception inner)
    : Exception("The PDF destination rejected a write.", inner);
}
