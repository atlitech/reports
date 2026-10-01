using System.Net;
using System.Net.Http.Headers;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;

namespace Atli.Reports.Client.Tests.Http;

/// <summary>
/// How the server's answers, and failures to reach it, map onto <see cref="ConversionError"/>s.
/// </summary>
public class ResponseMappingTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments(HttpStatusCode.BadRequest, "InvalidRequest", ConversionErrorKind.InvalidRequest)]
  [Arguments(
    HttpStatusCode.UnprocessableEntity,
    "SignalTimeout",
    ConversionErrorKind.SignalTimeout
  )]
  [Arguments(HttpStatusCode.ServiceUnavailable, "Busy", ConversionErrorKind.Busy)]
  [Arguments(
    HttpStatusCode.ServiceUnavailable,
    "BrowserUnavailable",
    ConversionErrorKind.BrowserUnavailable
  )]
  [Arguments(HttpStatusCode.GatewayTimeout, "Timeout", ConversionErrorKind.Timeout)]
  [Arguments(HttpStatusCode.InternalServerError, "RenderFailed", ConversionErrorKind.RenderFailed)]
  public async Task Server_problems_map_to_the_kind_they_name(
    HttpStatusCode status,
    string kind,
    ConversionErrorKind expected
  )
  {
    // No retries, so a 503 maps at once.
    await using var server = StubServer.Answering(
      status,
      kind,
      configure: settings => settings.MaxRetryAttempts = 0
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(expected);
    await Assert.That(result.AsT1.Message).Contains($"{(int)status}");
    await Assert.That(result.AsT1.Message).Contains("It failed.");
  }

  [Test]
  [Arguments(HttpStatusCode.BadRequest, ConversionErrorKind.InvalidRequest)]
  [Arguments(HttpStatusCode.NotFound, ConversionErrorKind.InvalidRequest)]
  [Arguments(HttpStatusCode.RequestEntityTooLarge, ConversionErrorKind.InvalidRequest)]
  [Arguments(HttpStatusCode.RequestTimeout, ConversionErrorKind.Timeout)]
  [Arguments(HttpStatusCode.UnprocessableEntity, ConversionErrorKind.SignalTimeout)]
  [Arguments(HttpStatusCode.TooManyRequests, ConversionErrorKind.Busy)]
  [Arguments((HttpStatusCode)499, ConversionErrorKind.Canceled)]
  [Arguments(HttpStatusCode.InternalServerError, ConversionErrorKind.RenderFailed)]
  [Arguments(HttpStatusCode.BadGateway, ConversionErrorKind.BrowserUnavailable)]
  [Arguments(HttpStatusCode.ServiceUnavailable, ConversionErrorKind.BrowserUnavailable)]
  [Arguments(HttpStatusCode.GatewayTimeout, ConversionErrorKind.Timeout)]
  [Arguments(HttpStatusCode.NoContent, ConversionErrorKind.RenderFailed)]
  public async Task Without_a_problem_kind_the_status_decides(
    HttpStatusCode status,
    ConversionErrorKind expected
  )
  {
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          new HttpResponseMessage(status) { Content = new StringContent("<html>proxy</html>") }
        ),
      settings => settings.MaxRetryAttempts = 0
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(expected);
  }

  [Test]
  [Arguments("Canceled")]
  [Arguments("42")]
  [Arguments("NotAKind")]
  public async Task A_kind_the_server_never_sends_leaves_the_status_to_decide(string kind)
  {
    await using var server = StubServer.Answering(HttpStatusCode.InternalServerError, kind);

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
  }

  [Test]
  public async Task A_200_that_is_not_a_pdf_is_a_failed_render()
  {
    await using var server = StubServer.Start(
      (_, _) =>
        Task.FromResult(
          new HttpResponseMessage(HttpStatusCode.OK)
          {
            Content = new StringContent("<html>a web page</html>", null, "text/html"),
          }
        )
    );

    using MemoryStream destination = new();
    var result = await server.Converter.ConvertAsync("<p>x</p>", destination, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(result.AsT1.Message).Contains("text/html");
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task A_server_that_cannot_be_reached_is_an_unavailable_browser()
  {
    await using var server = StubServer.Start(
      (_, _) => throw new HttpRequestException("Connection refused"),
      settings => settings.MaxRetryAttempts = 0
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Exception).IsTypeOf<HttpRequestException>();
  }

  [Test]
  public async Task An_attempt_that_runs_out_of_time_is_a_timeout_and_is_not_retried()
  {
    await using var server = StubServer.Start(
      async (_, cancellationToken) =>
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
      },
      settings => settings.AttemptTimeout = TimeSpan.FromMilliseconds(200)
    );

    var result = await server.Converter.ConvertAsync("<p>x</p>", Stream.Null, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Timeout);
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task The_callers_cancellation_is_canceled()
  {
    using CancellationTokenSource cancellation = new();
    await using var server = StubServer.Start(
      async (_, cancellationToken) =>
      {
        await cancellation.CancelAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
      }
    );

    var result = await server.Converter.ConvertAsync(
      "<p>x</p>",
      Stream.Null,
      null,
      cancellation.Token
    );

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task A_transfer_broken_off_after_the_first_byte_is_an_unavailable_browser_with_a_partial_pdf()
  {
    await using var server = StubServer.Start(
      (_, _) =>
      {
        StreamContent content = new(new BreakingStream("%PDF-1.7 partial"u8.ToArray()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
      }
    );

    using MemoryStream destination = new();
    var result = await server.Converter.ConvertAsync("<p>x</p>", destination, null, TestToken);

    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Message).Contains("after 16 bytes");
    await Assert.That(destination.ToArray()).IsEquivalentTo("%PDF-1.7 partial"u8.ToArray());
  }

  [Test]
  public async Task Errors_from_the_destination_propagate()
  {
    await using var server = StubServer.Start(
      (_, _) => Task.FromResult(StubServer.Pdf("%PDF-1.7 %%EOF"u8.ToArray()))
    );

    await Assert
      .That(async () =>
        await server.Converter.ConvertAsync("<p>x</p>", new FailingStream(), null, TestToken)
      )
      .Throws<IOException>()
      .WithMessage("The disk is full.");
  }

  [Test]
  public async Task Arguments_are_checked_like_the_engine_checks_them()
  {
    await using var server = StubServer.Answering(HttpStatusCode.OK);

    await Assert
      .That(async () => await server.Converter.ConvertAsync(null!, Stream.Null))
      .Throws<ArgumentNullException>();
    await Assert
      .That(async () => await server.Converter.ConvertAsync("<p>x</p>", (Stream)null!))
      .Throws<ArgumentNullException>();
    await Assert
      .That(async () =>
        await server.Converter.ConvertAsync("<p>x</p>", new MemoryStream([], writable: false))
      )
      .Throws<ArgumentException>();
    await Assert.That(server.Handler.Requests).IsEmpty();
  }

  /// <summary>
  /// Returns <paramref name="prefix"/>, then fails like a connection the server aborted.
  /// </summary>
  private sealed class BreakingStream(byte[] prefix) : MemoryStream(prefix)
  {
    public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      var read = await base.ReadAsync(buffer, cancellationToken);
      return read > 0
        ? read
        : throw new HttpIOException(
          HttpRequestError.ResponseEnded,
          "The response ended prematurely."
        );
    }
  }

  private sealed class FailingStream : MemoryStream
  {
    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    ) => throw new IOException("The disk is full.");
  }
}
