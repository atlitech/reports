using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Atli.Reports.Engine;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Client.Http;

/// <summary>
/// Converts HTML to PDF by posting it to an Atli Reports server's <c>/convert</c> endpoint.
/// </summary>
/// <remarks>
/// A singleton that creates an <see cref="HttpClient"/> per conversion from
/// <see cref="IHttpClientFactory"/>, so the factory keeps rotating its handlers (and picking up DNS
/// changes) even though the converter, and the singletons that depend on it, live for the whole app.
/// </remarks>
internal sealed class ReportsServerConverter(IHttpClientFactory httpClientFactory)
  : IHtmlToPdfConverter
{
  /// <summary>
  /// The name of the <see cref="HttpClient"/> the converter and the health check use.
  /// </summary>
  public const string HttpClientName = "Atli.Reports.Client";

  /// <summary>
  /// The path of the conversion endpoint, relative to the server's endpoint.
  /// </summary>
  public const string ConvertPath = "convert";

  private const int CopyBufferSize = 81920;

  /// <inheritdoc />
  /// <remarks>
  /// The stream reads the PDF from the response as the server sends it; nothing is buffered, so it
  /// is forward-only (<see cref="Stream.CanSeek"/> is <see langword="false"/>). Disposing it releases
  /// the connection. A transfer the server breaks off (it aborts the response when the conversion
  /// fails after the first byte) surfaces as an <see cref="IOException"/> from the stream's reads.
  /// </remarks>
  public async ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);

    var request = CreateRequest(html, options);
    var sent = await SendAsync(request, cancellationToken);
    if (sent.TryPickT1(out var error, out var response))
    {
      request.Dispose();
      return error;
    }

    try
    {
      var body = await response.Content.ReadAsStreamAsync(cancellationToken);
      return new ResponseBodyStream(body, response, request);
    }
    catch (Exception exception) when (ServerErrors.IsMapped(exception))
    {
      response.Dispose();
      request.Dispose();
      return ServerErrors.FromException(exception, cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// The PDF is copied to <paramref name="destination"/> chunk by chunk as the server sends it. If the
  /// server breaks the transfer off, the conversion fails with
  /// <see cref="ConversionErrorKind.BrowserUnavailable"/> and <paramref name="destination"/> holds
  /// the part of the PDF that arrived.
  /// </remarks>
  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(destination);
    if (!destination.CanWrite)
    {
      throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }

    using var request = CreateRequest(html, options);
    var sent = await SendAsync(request, cancellationToken);
    if (sent.TryPickT1(out var error, out var response))
    {
      return error;
    }

    using (response)
    {
      return await CopyAsync(response, destination, cancellationToken);
    }
  }

  private static HttpRequestMessage CreateRequest(string html, PdfOptions? options)
  {
    HttpRequestMessage request = new(HttpMethod.Post, ConvertPath)
    {
      // JsonContent serializes as it sends, so the HTML is not copied into a second buffer, and a
      // retry serializes it again.
      Content = JsonContent.Create(ConvertRequestBody.Create(html, options), RequestJson.TypeInfo),
    };
    request.Options.Set(ReportsAuthenticationHandler.Authenticate, true);
    return request;
  }

  /// <summary>
  /// Sends the request through the resilience pipeline and returns the response once its headers
  /// arrive, or the error the server or the transport reported.
  /// </summary>
  private async ValueTask<OneOf<HttpResponseMessage, ConversionError>> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken
  )
  {
    HttpResponseMessage response;
    try
    {
      response = await httpClientFactory
        .CreateClient(HttpClientName)
        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
    catch (Exception exception) when (ServerErrors.IsMapped(exception))
    {
      return ServerErrors.FromException(exception, cancellationToken);
    }

    if (response.StatusCode == HttpStatusCode.OK && IsPdf(response))
    {
      return response;
    }

    using (response)
    {
      if (response.StatusCode == HttpStatusCode.OK)
      {
        return ServerErrors.NotAPdf(response);
      }

      try
      {
        return await ServerErrors.FromResponseAsync(response, cancellationToken);
      }
      catch (OperationCanceledException exception)
      {
        return ServerErrors.FromException(exception, cancellationToken);
      }
    }
  }

  private static bool IsPdf(HttpResponseMessage response) =>
    string.Equals(
      response.Content.Headers.ContentType?.MediaType,
      "application/pdf",
      StringComparison.OrdinalIgnoreCase
    );

  /// <summary>
  /// Copies the PDF to <paramref name="destination"/>. Failures reading the response become
  /// conversion errors; failures writing to <paramref name="destination"/> propagate, as the
  /// <see cref="IHtmlToPdfConverter"/> contract asks, except for the caller's own cancellation.
  /// </summary>
  private static async Task<OneOf<Success, ConversionError>> CopyAsync(
    HttpResponseMessage response,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    Stream body;
    try
    {
      body = await response.Content.ReadAsStreamAsync(cancellationToken);
    }
    catch (Exception exception) when (ServerErrors.IsMapped(exception))
    {
      return ServerErrors.FromException(exception, cancellationToken);
    }

    await using var _ = body;
    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
    long copied = 0;
    try
    {
      while (true)
      {
        int read;
        try
        {
          read = await body.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (ServerErrors.IsMapped(exception))
        {
          return ServerErrors.FromException(
            exception,
            cancellationToken,
            string.Create(
              CultureInfo.InvariantCulture,
              $"The reports server broke the PDF transfer off after {copied} bytes, which the destination holds: {exception.Message}"
            )
          );
        }

        if (read == 0)
        {
          return new Success();
        }

        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        copied += read;
      }
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      return ServerErrors.FromException(exception, cancellationToken);
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
