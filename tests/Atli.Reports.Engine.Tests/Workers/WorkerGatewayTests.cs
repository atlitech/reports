using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server;
using Atli.Reports.Server.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Tests.Workers;

[NotInParallel("worker-gateway-processes")]
public class WorkerGatewayTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments("success")]
  [Arguments("split")]
  [Arguments("noisy")]
  public async Task Valid_worker_PDF_is_streamed_and_its_process_exits(string mode)
  {
    using var fixture = new Fixture(mode);
    using var converter = fixture.Create();
    using MemoryStream destination = new();
    var result = await converter.ConvertAsync(
      "<h1>safe</h1>",
      destination,
      cancellationToken: TestToken
    );
    await Assert.That(result.IsT0).IsTrue();
    await Assert
      .That(Encoding.UTF8.GetString(destination.ToArray()))
      .IsEqualTo("%PDF-1.7 safe %%EOF");
    await Assert.That(fixture.WorkerHasExited()).IsTrue();
  }

  [Test]
  [Arguments("wrong-job")]
  [Arguments("wrong-version")]
  [Arguments("wrong-status")]
  [Arguments("unknown-error")]
  [Arguments("oversized-chunk")]
  [Arguments("empty-pdf")]
  [Arguments("not-pdf")]
  public async Task Malformed_worker_responses_fail_without_exposing_data(string mode)
  {
    using var fixture = new Fixture(mode);
    using var converter = fixture.Create();
    using MemoryStream destination = new();
    var result = await converter.ConvertAsync(
      "<h1>sensitive</h1>",
      destination,
      cancellationToken: TestToken
    );
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(result.AsT1.Message).DoesNotContain("sensitive");
    await Assert.That(result.AsT1.Exception).IsNull();
    await Assert.That(destination.Length).IsEqualTo(0);
    await Assert.That(fixture.WorkerHasExited()).IsTrue();
  }

  [Test]
  [Arguments("trailing")]
  [Arguments("nonzero")]
  [Arguments("truncated")]
  public async Task Completion_requires_a_terminator_clean_EOF_and_successful_exit(string mode)
  {
    using var fixture = new Fixture(mode);
    using var converter = fixture.Create();
    using MemoryStream destination = new();
    var result = await converter.ConvertAsync("safe", destination, cancellationToken: TestToken);
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(fixture.WorkerHasExited()).IsTrue();
  }

  [Test]
  public async Task Output_limits_are_checked_before_writing_the_oversized_chunk()
  {
    using var fixture = new Fixture("success");
    fixture.Options.MaxPdfBytes = 10;
    using var converter = fixture.Create();
    using MemoryStream destination = new();
    var result = await converter.ConvertAsync("safe", destination, cancellationToken: TestToken);
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.RenderFailed);
    await Assert.That(destination.Length).IsEqualTo(0);
  }

  [Test]
  public async Task Engine_error_kinds_cross_the_boundary_without_worker_diagnostics()
  {
    using var fixture = new Fixture("error");
    using var converter = fixture.Create();
    var result = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
    await Assert.That(result.AsT1.Exception).IsNull();
  }

  [Test]
  public async Task Worker_requests_and_environment_exclude_application_identity_and_secrets()
  {
    using var fixture = new Fixture("inspect");
    var canary = "ATLI_GATEWAY_SECRET_" + Guid.NewGuid().ToString("N");
    Environment.SetEnvironmentVariable(canary, "must-not-cross-the-boundary");
    try
    {
      using var converter = fixture.Create();
      var result = await converter.ConvertAsync("safe", cancellationToken: TestToken);
      await using var stream = result.AsT0;
      using StreamReader reader = new(stream);
      var pdf = await reader.ReadToEndAsync(TestToken);
      await Assert.That(pdf).DoesNotContain(canary);
      await Assert.That(pdf).DoesNotContain("must-not-cross-the-boundary");
      using var inspection = JsonDocument.Parse(pdf[5..]);
      await Assert
        .That(
          inspection
            .RootElement.GetProperty("keys")
            .EnumerateArray()
            .Select(value => value.GetString()!)
        )
        .IsEquivalentTo(["version", "jobId", "html"]);
      await Assert
        .That(inspection.RootElement.GetProperty("environment").TryGetProperty("HOME", out _))
        .IsFalse();
    }
    finally
    {
      Environment.SetEnvironmentVariable(canary, null);
    }
  }

  [Test]
  public async Task Oversized_request_is_rejected_before_a_worker_is_launched()
  {
    using var fixture = new Fixture("success");
    using var converter = fixture.Create();
    var result = await converter.ConvertAsync(
      new string('x', 16 * 1024 * 1024 + 1),
      Stream.Null,
      cancellationToken: TestToken
    );
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(File.Exists(fixture.Marker)).IsFalse();
  }

  [Test]
  public async Task Serialized_options_also_obey_the_request_limit_before_launch()
  {
    using var fixture = new Fixture("success");
    using var converter = fixture.Create();
    var result = await converter.ConvertAsync(
      "safe",
      Stream.Null,
      new PdfOptions { HeaderTemplate = new string('x', 16 * 1024 * 1024 + 1) },
      TestToken
    );
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.InvalidRequest);
    await Assert.That(File.Exists(fixture.Marker)).IsFalse();
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Cancellation_or_deadline_kills_the_worker_and_releases_capacity(bool deadline)
  {
    using var fixture = new Fixture("sleep");
    fixture.Options.MaxConcurrentJobs = 1;
    fixture.Options.Timeout = deadline ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
    using var converter = fixture.Create();
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    var first = converter
      .ConvertAsync("safe", Stream.Null, cancellationToken: cancellation.Token)
      .AsTask();
    await fixture.WaitForWorkerAsync();
    var busy = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
    await Assert.That(busy.AsT1.Kind).IsEqualTo(ConversionErrorKind.Busy);
    if (!deadline)
      await cancellation.CancelAsync();
    var result = await first.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
    await Assert
      .That(result.AsT1.Kind)
      .IsEqualTo(deadline ? ConversionErrorKind.Timeout : ConversionErrorKind.Canceled);
    await Assert.That(fixture.WorkerHasExited()).IsTrue();
    fixture.Options.ProcessArguments[1] = "success";
    var next = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
    await Assert.That(next.IsT0).IsTrue();
  }

  [Test]
  public async Task Destination_failures_propagate_unchanged_and_cleanup_finishes()
  {
    using var fixture = new Fixture("success");
    using var converter = fixture.Create();
    using var destination = new FailingDestination();
    await Assert
      .That(async () =>
        await converter.ConvertAsync("safe", destination, cancellationToken: TestToken)
      )
      .Throws<IOException>()
      .WithMessage("destination failed");
    await Assert.That(fixture.WorkerHasExited()).IsTrue();
    var next = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
    await Assert.That(next.IsT0).IsTrue();
  }

  [Test]
  public async Task Uncooperative_destination_is_bounded_and_unsettled_cleanup_stops_new_jobs()
  {
    using var fixture = new Fixture("success");
    fixture.Options.Timeout = TimeSpan.FromSeconds(10);
    fixture.Options.CleanupTimeout = TimeSpan.FromMilliseconds(100);
    using var converter = fixture.Create();
    using var destination = new BlockingDestination();
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    try
    {
      var conversion = converter
        .ConvertAsync("safe", destination, cancellationToken: cancellation.Token)
        .AsTask();
      await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
      await cancellation.CancelAsync();
      var result = await conversion.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
      await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
      await Assert
        .That((await converter.CheckHealthAsync(TestToken)).Status)
        .IsEqualTo(HealthStatus.Unhealthy);
      var next = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
      await Assert.That(next.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    }
    finally
    {
      destination.Release.TrySetResult();
    }
  }

  [Test]
  public async Task Docker_launches_have_explicit_isolation_and_private_configuration()
  {
    var options = new ReportsExecutionOptions { Mode = "Worker", Image = "reports-worker:test" };
    options.Validate();
    var info = WorkerLauncher.Create(options, "/private-job", "owned-container");
    await Assert.That(info.ArgumentList).Contains("runsc");
    await Assert.That(info.ArgumentList).Contains("none");
    await Assert.That(info.ArgumentList).Contains("--read-only");
    await Assert.That(info.ArgumentList).Contains("1654:1654");
    await Assert.That(info.ArgumentList).Contains("no-new-privileges:true");
    await Assert.That(info.ArgumentList).Contains("--memory-swap");
    await Assert.That(info.ArgumentList).DoesNotContain("--privileged");
    await Assert.That(info.ArgumentList).DoesNotContain("--volume");
    await Assert.That(info.Environment["DOCKER_CONFIG"]).IsEqualTo("/private-job");
    await Assert.That(info.Environment.ContainsKey("HOME")).IsFalse();
    await Assert.That(info.Environment.ContainsKey("DOCKER_HOST")).IsFalse();
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Missing_Docker_container_after_killed_CLI_is_not_confirmed_cleanup(bool cancel)
  {
    using var fixture = new Fixture(cancel ? "docker-sleep" : "docker-success", docker: true);
    using var converter = fixture.Create();
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    var conversion = converter
      .ConvertAsync("safe", Stream.Null, cancellationToken: cancellation.Token)
      .AsTask();
    if (cancel)
    {
      await fixture.WaitForWorkerAsync();
      await cancellation.CancelAsync();
    }
    var result = await conversion.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
    if (cancel)
    {
      await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.Canceled);
      var next = await converter.ConvertAsync("safe", Stream.Null, cancellationToken: TestToken);
      await Assert.That(next.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    }
    else
      await Assert.That(result.IsT0).IsTrue();
    await Assert.That(File.Exists(fixture.Removed)).IsTrue();
  }

  [Test]
  public async Task Worker_mode_does_not_register_browser_services_and_default_mode_preserves_overrides()
  {
    using var fixture = new Fixture("success");
    await using (
      var app = ReportsServerApplication.Create([
        "--ReportsServer:Authentication:Mode=None",
        "--ReportsServer:Execution:Mode=Worker",
        "--ReportsServer:Execution:Backend=Process",
        "--ReportsServer:Execution:AllowDevelopmentProcess=true",
        "--ReportsServer:Execution:ProcessExecutablePath=" + fixture.Options.ProcessExecutablePath,
      ])
    )
    {
      await Assert
        .That(app.Services.GetRequiredService<IHtmlToPdfConverter>())
        .IsTypeOf<WorkerConverter>();
      await Assert
        .That(
          app.Services.GetServices<IHostedService>()
            .Any(service => service.GetType().Assembly == typeof(IHtmlToPdfConverter).Assembly)
        )
        .IsFalse();
      var registrations = app
        .Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
        .Value.Registrations;
      await Assert
        .That(registrations.Select(registration => registration.Name))
        .IsEquivalentTo(["worker_execution"]);
    }
    await using var integrated = ReportsServerApplication.Create(
      ["--ReportsServer:Authentication:Mode=None", "--ReportsEngine:Browser:WarmUpOnStartup=false"],
      builder =>
        builder.Services.Configure<ReportsEngineOptions>(engine =>
        {
          engine.Network.Mode = ReportsEngineNetworkMode.Unrestricted;
          engine.Browser.NoSandbox = false;
        })
    );
    var engineOptions = integrated
      .Services.GetRequiredService<IOptions<ReportsEngineOptions>>()
      .Value;
    await Assert.That(engineOptions.Network.Mode).IsEqualTo(ReportsEngineNetworkMode.Unrestricted);
    await Assert.That(engineOptions.Browser.NoSandbox).IsFalse();
    await Assert
      .That(integrated.Services.GetRequiredService<IHtmlToPdfConverter>())
      .IsNotTypeOf<WorkerConverter>();
  }

  [Test]
  public async Task Unsafe_or_ambiguous_execution_configuration_fails_closed()
  {
    await Assert
      .That(() => new ReportsExecutionOptions { Mode = "Unknown" }.Validate())
      .Throws<InvalidOperationException>();
    await Assert
      .That(() =>
        new ReportsExecutionOptions
        {
          Mode = "Worker",
          Backend = "Process",
          ProcessExecutablePath = "/usr/bin/python3",
        }.Validate()
      )
      .Throws<InvalidOperationException>();
    await Assert
      .That(() =>
        new ReportsExecutionOptions { Mode = "Worker", Image = "--privileged" }.Validate()
      )
      .Throws<InvalidOperationException>();
    await Assert
      .That(() =>
        new ReportsExecutionOptions
        {
          Mode = "Worker",
          Image = "worker",
          Runtime = "",
        }.Validate()
      )
      .Throws<InvalidOperationException>();
  }

  private sealed class Fixture : IDisposable
  {
    private readonly string _directory = Path.Combine(
      Path.GetTempPath(),
      "atli-gateway-test-" + Guid.NewGuid().ToString("N")
    );
    internal string Marker => Path.Combine(_directory, "worker.pid");
    internal string Removed => Path.Combine(_directory, "removed");
    internal ReportsExecutionOptions Options { get; }

    internal Fixture(string mode, bool docker = false)
    {
      Directory.CreateDirectory(_directory);
      var script = Path.Combine(_directory, "worker.py");
      File.WriteAllText(script, Script);
      Options = new ReportsExecutionOptions
      {
        Mode = "Worker",
        Backend = docker ? "Docker" : "Process",
        AllowDevelopmentProcess = true,
        ProcessExecutablePath = "/usr/bin/python3",
        ProcessArguments = [script, mode, Marker],
        Timeout = TimeSpan.FromSeconds(5),
        CleanupTimeout = TimeSpan.FromSeconds(1),
        Image = "worker:test",
      };
      if (docker)
      {
        var wrapper = Path.Combine(_directory, "docker");
        File.WriteAllText(
          wrapper,
          $"#!/bin/sh\nexec /usr/bin/python3 '{script}' '{mode}' '{Marker}' \"$@\"\n"
        );
        if (!OperatingSystem.IsWindows())
          File.SetUnixFileMode(
            wrapper,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
          );
        Options.DockerExecutablePath = wrapper;
      }
      Options.Validate();
    }

    internal WorkerConverter Create() => new(Options, new TestLifetime());

    internal async Task WaitForWorkerAsync() =>
      await Assert
        .That(
          await TestEngine.EventuallyAsync(
            () => Task.FromResult(File.Exists(Marker)),
            TimeSpan.FromSeconds(3)
          )
        )
        .IsTrue();

    internal bool WorkerHasExited()
    {
      if (!File.Exists(Marker))
        return true;
      try
      {
        using var process = Process.GetProcessById(
          int.Parse(File.ReadAllText(Marker), System.Globalization.CultureInfo.InvariantCulture)
        );
        return process.HasExited;
      }
      catch (ArgumentException)
      {
        return true;
      }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Script = """
      import sys, os, json, struct, time, uuid
      from pathlib import Path
      mode, marker = sys.argv[1:3]
      if mode.startswith('docker-'):
          if sys.argv[3] == 'rm':
              Path(marker).with_name('removed').write_text('yes')
              sys.stderr.write('Error response from daemon: No such container')
              sys.exit(1)
          mode = 'sleep' if mode == 'docker-sleep' else 'success'
      source, output = sys.stdin.buffer, sys.stdout.buffer
      length = struct.unpack('<i', source.read(4))[0]
      request = json.loads(source.read(length))
      Path(marker).write_text(str(os.getpid()))
      if mode == 'sleep':
          time.sleep(30)
          sys.exit(0)
      def frame(value):
          output.write(struct.pack('<i', len(value)))
          output.write(value)
          output.flush()
      header = {'version': 1, 'jobId': request['jobId'], 'status': 'pdf'}
      if mode == 'wrong-job': header['jobId'] = str(uuid.uuid4())
      if mode == 'wrong-version': header['version'] = 2
      if mode == 'wrong-status': header['status'] = 'unknown'
      if mode in ('error', 'unknown-error'):
          header['status'] = 'error'
          header['kind'] = 'PolicyDenied' if mode == 'error' else 'sensitive-unknown-kind'
      frame(json.dumps(header).encode())
      if mode in ('error', 'unknown-error'): sys.exit(0)
      if mode == 'noisy':
          sys.stderr.write('sensitive-diagnostics' * 100000)
          sys.stderr.flush()
      if mode == 'oversized-chunk':
          output.write(struct.pack('<i', 65537)); output.flush(); sys.exit(0)
      if mode == 'empty-pdf': frame(b''); sys.exit(0)
      if mode == 'not-pdf': frame(b'sensitive document'); frame(b''); sys.exit(0)
      if mode == 'truncated':
          frame(b'%PDF-')
          output.write(struct.pack('<i', 100)); output.write(b'partial'); output.flush(); sys.exit(0)
      if mode == 'inspect':
          frame(b'%PDF-' + json.dumps({'keys': list(request), 'environment': dict(os.environ)}).encode())
      elif mode == 'split':
          for chunk in (b'%', b'P', b'D', b'F', b'-1.7 safe %%EOF'): frame(chunk)
      else: frame(b'%PDF-1.7 safe %%EOF')
      frame(b'')
      if mode == 'trailing': output.write(b'unexpected'); output.flush()
      if mode == 'nonzero': sys.exit(7)
      """;
  }

  private sealed class TestLifetime : IHostApplicationLifetime
  {
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
  }

  private sealed class FailingDestination : MemoryStream
  {
    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    ) => throw new IOException("destination failed");
  }

  private sealed class BlockingDestination : MemoryStream
  {
    internal TaskCompletionSource Entered { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      Entered.TrySetResult();
      return new(Release.Task);
    }
  }
}
