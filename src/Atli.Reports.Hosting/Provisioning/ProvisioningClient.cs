using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Atli.Reports.Hosting.Provisioning;

/// <summary>
/// <see cref="IProvisioningClient"/> over the <see cref="ProvisioningApi"/>: each request carries the
/// gateway's credential in <see cref="ProvisioningApi.ApiKeyHeader"/> and nothing else of the
/// gateway's.
/// </summary>
/// <remarks>
/// <para>
/// Send through a handler from <see cref="CreateHandler"/>: it follows no redirect, which would carry
/// the credential to another address, and sends no trace context or baggage, which ASP.NET Core
/// takes from the gateway's callers.
/// </para>
/// <para>
/// An answer other than success throws <see cref="ProvisioningApiException"/> with its status, its
/// <c>Retry-After</c>, and the problem's <c>kind</c> when the answer is RFC 9457 problem details
/// naming one of <see cref="ProvisioningProblemKinds"/>. At most <see cref="MaxBodyBytes"/> of any
/// answer are read. A service that cannot be reached, or does not answer within the
/// <see cref="HttpClient.Timeout"/>, throws one with neither a status nor a kind. Messages name the
/// call, the status, and the service's <c>detail</c>, cut short and without control characters,
/// never the credential. Nothing is retried, and nothing is logged.
/// </para>
/// </remarks>
public sealed class ProvisioningClient : IProvisioningClient
{
  /// <summary>The most of an answer's body the client reads.</summary>
  public const int MaxBodyBytes = 16 * 1024;

  /// <summary>How much of the service's <c>detail</c> an exception message carries.</summary>
  internal const int MaxDetailLength = 300;

  private const string Redacted = "[redacted]";

  private static readonly string[] Kinds =
  [
    ProvisioningProblemKinds.InvalidRequest,
    ProvisioningProblemKinds.Unauthorized,
    ProvisioningProblemKinds.NotAllowed,
    ProvisioningProblemKinds.QuotaExceeded,
    ProvisioningProblemKinds.RateLimited,
    ProvisioningProblemKinds.Disabled,
    ProvisioningProblemKinds.Failed,
  ];

  private readonly HttpClient _httpClient;
  private readonly string _baseAddress;
  private readonly string _apiKey;
  private readonly TimeProvider _time;

  /// <summary>Creates the client.</summary>
  /// <param name="httpClient">
  /// The client to send with, over a handler from <see cref="CreateHandler"/>; its base address is
  /// ignored.
  /// </param>
  /// <param name="baseAddress">The service's address, to which the API's routes are appended.</param>
  /// <param name="apiKey">The gateway's credential for the service.</param>
  public ProvisioningClient(HttpClient httpClient, Uri baseAddress, string apiKey)
    : this(httpClient, baseAddress, apiKey, TimeProvider.System) { }

  /// <summary>Creates the client, reading a dated <c>Retry-After</c> against <paramref name="time"/>.</summary>
  internal ProvisioningClient(
    HttpClient httpClient,
    Uri baseAddress,
    string apiKey,
    TimeProvider time
  )
  {
    ArgumentNullException.ThrowIfNull(httpClient);
    ArgumentNullException.ThrowIfNull(baseAddress);
    ArgumentNullException.ThrowIfNull(time);
    if (
      !baseAddress.IsAbsoluteUri
      || baseAddress.Scheme is not ("https" or "http")
      || !string.IsNullOrEmpty(baseAddress.UserInfo)
      || !string.IsNullOrEmpty(baseAddress.Query)
      || !string.IsNullOrEmpty(baseAddress.Fragment)
    )
    {
      throw new ArgumentException(
        "The provisioning service's address must be absolute http or https, without credentials, query, or fragment.",
        nameof(baseAddress)
      );
    }

    // Never the key itself in the message.
    if (apiKey is not { Length: > 0 and <= 1024 } || !apiKey.All(c => c is > ' ' and < '\u007f'))
    {
      throw new ArgumentException(
        "The provisioning service's API key must be printable ASCII without spaces, at most 1024 characters.",
        nameof(apiKey)
      );
    }

    _httpClient = httpClient;
    _baseAddress = baseAddress.AbsoluteUri.TrimEnd('/');
    _apiKey = apiKey;
    _time = time;
  }

  /// <summary>
  /// A handler for the client's <see cref="HttpClient"/>: no redirects, cookies, decompression, or
  /// trace headers; connections that open within 10 seconds and are recycled every 5 minutes, so a
  /// long-lived client follows DNS changes.
  /// </summary>
  public static SocketsHttpHandler CreateHandler() =>
    new()
    {
      // The credential header must never follow a redirect to another address.
      AllowAutoRedirect = false,
      UseCookies = false,
      AutomaticDecompression = DecompressionMethods.None,
      ConnectTimeout = TimeSpan.FromSeconds(10),
      PooledConnectionLifetime = TimeSpan.FromMinutes(5),
      // No trace context or baggage: a call may serve many requests, and none of their callers'
      // context belongs to the service.
      ActivityHeadersPropagator = null,
    };

  /// <inheritdoc />
  public async Task<bool> EnsureRendererAsync(string tenantId, CancellationToken cancellationToken)
  {
    var call = $"PUT {ProvisioningApi.RendererPath(tenantId)}";
    using var response = await SendAsync(HttpMethod.Put, call, tenantId, cancellationToken);
    await EnsureSuccessAsync(call, response, cancellationToken);

    var body = await ReadBodyAsync(call, response, cancellationToken);
    EnsureRendererResponse? answer = null;
    if (body is not null)
    {
      try
      {
        answer = JsonSerializer.Deserialize(
          body.Value.Span,
          ProvisioningJsonContext.Default.EnsureRendererResponse
        );
      }
      catch (JsonException)
      {
        // Not an answer; reported below.
      }
    }

    if (answer is null || !string.Equals(answer.TenantId, tenantId, StringComparison.Ordinal))
    {
      throw new ProvisioningApiException(
        $"{call} answered {(int)response.StatusCode} without the tenant's ensure response."
      )
      {
        StatusCode = (int)response.StatusCode,
      };
    }

    return answer.Created;
  }

  /// <inheritdoc />
  public async Task DeleteRendererAsync(string tenantId, CancellationToken cancellationToken)
  {
    var call = $"DELETE {ProvisioningApi.RendererPath(tenantId)}";
    using var response = await SendAsync(HttpMethod.Delete, call, tenantId, cancellationToken);
    await EnsureSuccessAsync(call, response, cancellationToken);
  }

  private async Task<HttpResponseMessage> SendAsync(
    HttpMethod method,
    string call,
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    using HttpRequestMessage request = new(
      method,
      new Uri(_baseAddress + ProvisioningApi.RendererPath(tenantId), UriKind.Absolute)
    );
    request.Headers.TryAddWithoutValidation(ProvisioningApi.ApiKeyHeader, _apiKey);
    try
    {
      // Headers only: the body is read up to MaxBodyBytes, never buffered whole.
      return await _httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      );
    }
    catch (HttpRequestException exception)
    {
      throw new ProvisioningApiException(
        $"{call} could not reach the provisioning service: {exception.Message}",
        exception
      );
    }
    // HttpClient.Timeout, not the caller's cancellation.
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
      throw new ProvisioningApiException(
        $"{call} got no answer from the provisioning service in time.",
        exception
      );
    }
  }

  private async Task EnsureSuccessAsync(
    string call,
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    if (response.IsSuccessStatusCode)
    {
      return;
    }

    var status = (int)response.StatusCode;
    string? kind = null;
    string? detail = null;
    if (
      string.Equals(
        response.Content.Headers.ContentType?.MediaType,
        "application/problem+json",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      ReadOnlyMemory<byte>? body;
      try
      {
        body = await ReadBodyAsync(call, response, cancellationToken);
      }
      catch (ProvisioningApiException)
      {
        // The status alone decides.
        body = null;
      }

      (kind, detail) = ReadProblem(body);
    }

    var description = Describe(detail);
    throw new ProvisioningApiException(
      $"{call} failed with {status} ({response.StatusCode})"
        + (kind is null ? "" : $", kind {kind}")
        + (description is null ? "." : ": " + description)
    )
    {
      StatusCode = status,
      Kind = kind,
      RetryAfter = RetryAfter(response),
    };
  }

  /// <summary>
  /// The body, or <see langword="null"/> when it is longer than <see cref="MaxBodyBytes"/>. Throws
  /// <see cref="ProvisioningApiException"/> when it breaks off.
  /// </summary>
  private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(
    string call,
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    if (response.Content.Headers.ContentLength > MaxBodyBytes)
    {
      return null;
    }

    try
    {
      await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      var buffer = new byte[MaxBodyBytes + 1];
      var length = 0;
      while (length < buffer.Length)
      {
        var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
        if (read == 0)
        {
          return buffer.AsMemory(0, length);
        }

        length += read;
      }

      return null;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException)
    {
      throw new ProvisioningApiException(
        $"{call}: the provisioning service's answer broke off.",
        exception
      );
    }
  }

  /// <summary>
  /// The problem's <c>kind</c>, when it is one of <see cref="ProvisioningProblemKinds"/>, and its
  /// <c>detail</c>. Anything that is not a JSON object with string members counts as missing.
  /// </summary>
  private static (string? Kind, string? Detail) ReadProblem(ReadOnlyMemory<byte>? body)
  {
    if (body is null)
    {
      return (null, null);
    }

    try
    {
      using var document = JsonDocument.Parse(body.Value);
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return (null, null);
      }

      string? kind = null;
      if (root.TryGetProperty("kind", out var kindElement))
      {
        kind = Array.Find(
          Kinds,
          known => kindElement.ValueKind == JsonValueKind.String && kindElement.ValueEquals(known)
        );
      }

      var detail =
        root.TryGetProperty("detail", out var detailElement)
        && detailElement.ValueKind == JsonValueKind.String
          ? detailElement.GetString()
          : null;
      return (kind, detail);
    }
    // Not JSON, or text that is not valid (a lone surrogate escape, invalid UTF-8).
    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
    {
      return (null, null);
    }
  }

  /// <summary>
  /// <paramref name="detail"/> without control or formatting characters and without the
  /// credential, should the service ever echo it, cut to <see cref="MaxDetailLength"/>; or
  /// <see langword="null"/> when nothing is left.
  /// </summary>
  private string? Describe(string? detail)
  {
    if (detail is null)
    {
      return null;
    }

    // Scrubbed before it is cut short, so no cut leaves part of the credential behind.
    detail = detail.Replace(_apiKey, Redacted, StringComparison.Ordinal);
    StringBuilder builder = new(Math.Min(detail.Length, MaxDetailLength));
    foreach (var c in detail)
    {
      if (
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
        || (char.IsWhiteSpace(c) && builder.Length == 0)
      )
      {
        continue;
      }

      builder.Append(c);
      if (builder.Length == MaxDetailLength)
      {
        break;
      }
    }

    // Never end on half a surrogate pair.
    if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
    {
      builder.Length--;
    }

    var description = builder.ToString().TrimEnd();
    return description.Length == 0 ? null : description;
  }

  /// <summary>The answer's <c>Retry-After</c> as a wait; a date in the past is no wait.</summary>
  private TimeSpan? RetryAfter(HttpResponseMessage response)
  {
    switch (response.Headers.RetryAfter)
    {
      case { Delta: { } delta }:
        return delta;
      case { Date: { } date }:
        var wait = date - _time.GetUtcNow();
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
      default:
        return null;
    }
  }
}
