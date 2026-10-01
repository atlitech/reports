using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// The dashboard command that converts a one-page document on the server, as a quick end-to-end check.
/// </summary>
/// <remarks>
/// The outcome shows as the command's notification; the PDF's size and the time the round trip took
/// go to the server's console log. (Aspire 13.0, the oldest release the package supports, has no
/// success message on a command result.)
/// </remarks>
internal static partial class ReportsServerTestPageCommand
{
  /// <summary>
  /// The command's name, which <c>aspire resource &lt;name&gt; convert-test-page</c> also takes.
  /// </summary>
  internal const string Name = "convert-test-page";

  private const string TestPage = """
    <!DOCTYPE html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Atli Reports test page</title></head>
    <body style="font-family: sans-serif">
    <h1>Atli Reports</h1>
    <p>The reports server converted this test page for the Aspire dashboard.</p>
    </body>
    </html>
    """;

  private static readonly string RequestBody = new JsonObject
  {
    ["html"] = TestPage,
  }.ToJsonString();

  /// <summary>
  /// Adds the command to the server. It is enabled while the server is healthy.
  /// </summary>
  internal static IResourceBuilder<ReportsServerResource> WithConvertTestPageCommand(
    this IResourceBuilder<ReportsServerResource> builder
  ) =>
    builder.WithCommand(
      Name,
      "Convert a test page",
      context => ExecuteAsync(builder.Resource, context),
      new CommandOptions
      {
        Description =
          "Converts a one-page document to a PDF and logs the PDF's size and the time it took.",
        IconName = "DocumentPdf",
        UpdateState = context =>
          context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled,
      }
    );

  private static async Task<ExecuteCommandResult> ExecuteAsync(
    ReportsServerResource resource,
    ExecuteCommandContext context
  )
  {
    var logger = context
      .ServiceProvider.GetRequiredService<ResourceLoggerService>()
      .GetLogger(resource);
    var client = context.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
    Uri convert = new(new Uri(resource.PrimaryEndpoint.Url), "convert");

    using StringContent content = new(RequestBody);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
    {
      CharSet = "utf-8",
    };

    var started = Stopwatch.GetTimestamp();
    try
    {
      using var response = await client.PostAsync(convert, content, context.CancellationToken);
      var body = await response.Content.ReadAsByteArrayAsync(context.CancellationToken);
      var elapsed = Stopwatch.GetElapsedTime(started);

      if (!response.IsSuccessStatusCode)
      {
        var status = (int)response.StatusCode;
        LogTestPageRejected(logger, status, Encoding.UTF8.GetString(body));
        return CommandResults.Failure(
          $"The server answered {status} {response.ReasonPhrase}; its console log has the details."
        );
      }

      if (!body.AsSpan().StartsWith("%PDF-"u8))
      {
        return CommandResults.Failure("The server answered, but not with a PDF.");
      }

      LogTestPageConverted(logger, body.Length, (long)elapsed.TotalMilliseconds);
      return CommandResults.Success();
    }
    catch (HttpRequestException exception)
    {
      return CommandResults.Failure($"The server could not be reached: {exception.Message}");
    }
    catch (TaskCanceledException) when (!context.CancellationToken.IsCancellationRequested)
    {
      return CommandResults.Failure("The server did not answer in time.");
    }
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Converted the test page to a {Size}-byte PDF in {ElapsedMilliseconds} ms."
  )]
  private static partial void LogTestPageConverted(
    ILogger logger,
    int size,
    long elapsedMilliseconds
  );

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Warning,
    Message = "The test page failed with status {StatusCode}: {Problem}"
  )]
  private static partial void LogTestPageRejected(ILogger logger, int statusCode, string problem);
}
