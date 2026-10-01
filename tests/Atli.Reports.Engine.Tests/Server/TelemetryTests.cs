using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>
/// The server's OTLP export: off without an endpoint, and on with the standard OpenTelemetry
/// variables. The variables are passed on the command line, which feeds the same configuration as
/// the environment, and are set blank where a test needs them unset.
/// </summary>
public class TelemetryTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Without_an_otlp_endpoint_no_telemetry_is_registered()
  {
    await using var server = await RunningServer.StartAsync(
      converter: null,
      "--OTEL_EXPORTER_OTLP_ENDPOINT="
    );

    await Assert.That(server.Services.GetService<TracerProvider>()).IsNull();
    await Assert.That(server.Services.GetService<MeterProvider>()).IsNull();
    await Assert.That(server.Services.GetService<LoggerProvider>()).IsNull();
  }

  [Test]
  public async Task The_service_name_defaults_to_the_server_and_the_standard_variables_override_it()
  {
    await using var collector = new FakeOtlpCollector();
    await using var defaults = await RunningServer.StartAsync(
      converter: null,
      $"--OTEL_EXPORTER_OTLP_ENDPOINT={collector.Endpoint}",
      "--OTEL_SERVICE_NAME=",
      "--OTEL_RESOURCE_ATTRIBUTES="
    );
    await using var overridden = await RunningServer.StartAsync(
      converter: null,
      $"--OTEL_EXPORTER_OTLP_ENDPOINT={collector.Endpoint}",
      "--OTEL_SERVICE_NAME=reports-under-test",
      "--OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=test"
    );

    var defaultResource = ResourceOf(defaults);
    var overriddenResource = ResourceOf(overridden);

    await Assert.That(defaultResource["service.name"]).IsEqualTo("atli-reports-server");
    await Assert.That(overriddenResource["service.name"]).IsEqualTo("reports-under-test");
    await Assert.That(overriddenResource["deployment.environment.name"]).IsEqualTo("test");
  }

  [Test]
  public async Task Requests_and_conversions_are_exported_but_health_probes_are_not()
  {
    await using var collector = new FakeOtlpCollector();
    await using var server = await RunningServer.StartAsync(
      converter: null,
      $"--OTEL_EXPORTER_OTLP_ENDPOINT={collector.Endpoint}",
      "--OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf"
    );
    var tracing = server.Services.GetRequiredService<TracerProvider>();

    using var health = await server.Client.GetAsync("/health/live", TestToken);
    // A blank signal name fails in the engine's validation, before any browser is needed.
    using var conversion = await server.PostAsync(
      """{"html":"<p>x</p>","options":{"waitForSignal":" "}}"""
    );

    await Assert.That(conversion.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    var exported = await TestEngine.EventuallyAsync(
      () =>
      {
        tracing.ForceFlush(5_000);
        var traces = collector.Received("/v1/traces");
        return Task.FromResult(
          traces.Contains("/convert", StringComparison.Ordinal)
            && traces.Contains("atli.reports.convert", StringComparison.Ordinal)
        );
      },
      TestEngine.GenerousTimeout
    );

    await Assert.That(exported).IsTrue().Because("the request and conversion spans are exported");
    var received = collector.Received("/v1/traces");
    await Assert.That(received).Contains("InvalidRequest");
    await Assert.That(received).DoesNotContain("/health/live");
  }

  private static Dictionary<string, object> ResourceOf(RunningServer server) =>
    server
      .Services.GetRequiredService<TracerProvider>()
      .GetResource()
      .Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

  /// <summary>
  /// Accepts OTLP/HTTP exports on a free loopback port and keeps their bodies per path. OTLP
  /// protobuf carries strings as plain UTF-8, so tests can search the bodies as text.
  /// </summary>
  private sealed class FakeOtlpCollector : IAsyncDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _bodies = new();
    private readonly Task _loop;

    public FakeOtlpCollector()
    {
      using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
      {
        probe.Start();
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        probe.Stop();
      }

      _listener.Prefixes.Add(Endpoint + "/");
      _listener.Start();
      _loop = Task.Run(ServeAsync);
    }

    public string Endpoint { get; }

    /// <summary>
    /// Everything received at <paramref name="path"/>, as text.
    /// </summary>
    public string Received(string path) =>
      _bodies.TryGetValue(path, out var bodies)
        ? string.Concat(bodies.Select(body => Encoding.UTF8.GetString(body)))
        : "";

    public async ValueTask DisposeAsync()
    {
      _listener.Stop();
      _listener.Close();
      try
      {
        await _loop;
      }
      catch (Exception exception)
        when (exception is HttpListenerException or ObjectDisposedException)
      {
        // Stopped.
      }
    }

    private async Task ServeAsync()
    {
      while (_listener.IsListening)
      {
        HttpListenerContext context;
        try
        {
          context = await _listener.GetContextAsync();
        }
        catch (Exception exception)
          when (exception
              is HttpListenerException
                or ObjectDisposedException
                or InvalidOperationException
          )
        {
          return;
        }

        try
        {
          using MemoryStream body = new();
          await context.Request.InputStream.CopyToAsync(body);
          _bodies.GetOrAdd(context.Request.Url!.AbsolutePath, _ => new()).Enqueue(body.ToArray());
          context.Response.StatusCode = 200;
          context.Response.ContentType = "application/x-protobuf";
          context.Response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException)
        {
          // The exporter went away.
        }
      }
    }
  }
}
