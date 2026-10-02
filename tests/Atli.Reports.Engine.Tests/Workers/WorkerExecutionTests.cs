using System.Text;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Worker;
using Atli.Reports.Worker.Protocol;

namespace Atli.Reports.Engine.Tests.Workers;

public class WorkerExecutionTests
{
  private static readonly string[] OperationalEnvironmentNames =
  [
    "ATLI_WORKER_BROWSER_PATH",
    "ATLI_WORKER_NO_SANDBOX",
    "ATLI_WORKER_DISABLE_DEV_SHM_USAGE",
    "ATLI_WORKER_MAX_JOBS",
  ];

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  public async Task Worker_runs_only_its_configured_number_of_jobs_with_correlated_streams(
    int maxJobs
  )
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in ids)
    {
      await WorkerProtocol.WriteRequestAsync(
        input,
        new WorkerRequest(1, id, "<p>safe</p>"),
        TestToken
      );
    }
    input.Position = 0;
    var payload = Encoding.UTF8.GetBytes(
      "%PDF-" + new string('x', WorkerProtocol.MaxChunkBytes + 7)
    );
    FakeConverter converter = new(
      async (stream, token) =>
      {
        await stream.WriteAsync(payload, token);
        return null;
      }
    );
    var exit = await WorkerJobRunner.RunAsync(
      input,
      output,
      converter,
      new WorkerExecutionOptions { MaxJobs = maxJobs },
      TestToken
    );
    await Assert.That(exit).IsEqualTo(0);
    await Assert.That(converter.Calls).IsEqualTo(maxJobs);
    output.Position = 0;
    for (var job = 0; job < maxJobs; job++)
    {
      var header = await WorkerProtocol.ReadResponseHeaderAsync(output, TestToken);
      await Assert.That(header.JobId).IsEqualTo(ids[job]);
      await Assert.That(header.Status).IsEqualTo("pdf");
      using MemoryStream received = new();
      var buffer = new byte[WorkerProtocol.MaxChunkBytes];
      int count;
      while ((count = await WorkerProtocol.ReadPdfChunkAsync(output, buffer, TestToken)) != 0)
      {
        received.Write(buffer, 0, count);
      }
      await Assert.That(received.ToArray()).IsEquivalentTo(payload);
    }
    await Assert.That(output.Position).IsEqualTo(output.Length);
    await Assert.That(input.Position == input.Length).IsEqualTo(maxJobs == 2);
  }

  [Test]
  public async Task An_error_emits_only_a_sanitized_kind_and_stops_reuse()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    var id = Guid.NewGuid();
    await WorkerProtocol.WriteRequestAsync(
      input,
      new WorkerRequest(1, id, "<p>private</p>"),
      TestToken
    );
    await WorkerProtocol.WriteRequestAsync(
      input,
      new WorkerRequest(1, Guid.NewGuid(), "<p>next</p>"),
      TestToken
    );
    input.Position = 0;
    FakeConverter converter = new(
      (_, _) =>
        Task.FromResult<ConversionError?>(
          new ConversionError(
            ConversionErrorKind.PolicyDenied,
            "secret access token and https://private.example",
            new InvalidOperationException("private details")
          )
        )
    );
    var exit = await WorkerJobRunner.RunAsync(
      input,
      output,
      converter,
      new WorkerExecutionOptions { MaxJobs = 2 },
      TestToken
    );
    await Assert.That(exit).IsEqualTo(0);
    await Assert.That(converter.Calls).IsEqualTo(1);
    output.Position = 0;
    var header = await WorkerProtocol.ReadResponseHeaderAsync(output, TestToken);
    await Assert.That(header.JobId).IsEqualTo(id);
    await Assert.That(header.Kind).IsEqualTo(ConversionErrorKind.PolicyDenied);
    await Assert.That(output.Position).IsEqualTo(output.Length);
    var raw = Encoding.UTF8.GetString(output.ToArray());
    await Assert
      .That(raw)
      .DoesNotContain("secret")
      .And.DoesNotContain("private")
      .And.DoesNotContain("token");
  }

  [Test]
  public async Task Failure_after_PDF_bytes_never_emits_a_success_terminator()
  {
    using var input = await RequestInput();
    using MemoryStream output = new();
    FakeConverter converter = new(
      async (stream, token) =>
      {
        await stream.WriteAsync("%PDF-partial"u8.ToArray(), token);
        return new ConversionError(ConversionErrorKind.RenderFailed, "private");
      }
    );
    await Assert
      .That(await WorkerJobRunner.RunAsync(input, output, converter, cancellationToken: TestToken))
      .IsEqualTo(2);
    output.Position = 0;
    await WorkerProtocol.ReadResponseHeaderAsync(output, TestToken);
    await Assert
      .That((await WorkerProtocol.ReadPdfChunkAsync(output, TestToken)).Length)
      .IsGreaterThan(0);
    await Assert
      .That(async () => await WorkerProtocol.ReadPdfChunkAsync(output, TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task Protocol_and_unexpected_engine_failures_never_write_exception_details()
  {
    using MemoryStream malformed = new(new byte[] { 255, 255, 255, 255 });
    using MemoryStream output = new();
    FakeConverter converter = new((_, _) => throw new InvalidOperationException("private-token"));
    await Assert
      .That(
        await WorkerJobRunner.RunAsync(malformed, output, converter, cancellationToken: TestToken)
      )
      .IsEqualTo(2);
    await Assert.That(converter.Calls).IsEqualTo(0);
    using var input = await RequestInput();
    await Assert
      .That(await WorkerJobRunner.RunAsync(input, output, converter, cancellationToken: TestToken))
      .IsEqualTo(2);
    await Assert.That(output.Length).IsEqualTo(0L);
  }

  [Test]
  public async Task Job_deadline_cancels_the_engine_and_never_reuses_it()
  {
    using var input = await RequestInput();
    using MemoryStream output = new();
    FakeConverter converter = new(
      async (_, token) =>
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return null;
      }
    );
    var options = new WorkerExecutionOptions
    {
      JobTimeout = TimeSpan.FromMilliseconds(20),
      MaxJobs = 2,
    };
    await Assert
      .That(await WorkerJobRunner.RunAsync(input, output, converter, options, TestToken))
      .IsEqualTo(2);
    await Assert.That(converter.Calls).IsEqualTo(1);
    await Assert.That(output.Length).IsEqualTo(0L);
  }

  [Test]
  public async Task Idle_input_is_bounded_and_does_not_start_the_engine()
  {
    using StalledInput input = new();
    using MemoryStream output = new();
    FakeConverter converter = new((_, _) => Task.FromResult<ConversionError?>(null));
    var options = new WorkerExecutionOptions { InputTimeout = TimeSpan.FromMilliseconds(20) };
    await Assert
      .That(await WorkerJobRunner.RunAsync(input, output, converter, options, TestToken))
      .IsEqualTo(2);
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Deadlines_bound_input_and_conversion_even_when_the_operation_ignores_cancellation()
  {
    using MemoryStream output = new();
    FakeConverter unused = new((_, _) => Task.FromResult<ConversionError?>(null));
    using (var input = new StalledInput(ignoreCancellation: true))
    {
      var options = new WorkerExecutionOptions { InputTimeout = TimeSpan.FromMilliseconds(20) };
      await Assert
        .That(
          await WorkerJobRunner
            .RunAsync(input, output, unused, options, TestToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestToken)
        )
        .IsEqualTo(2);
    }
    await Assert.That(unused.Calls).IsEqualTo(0);

    TaskCompletionSource<ConversionError?> late = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    FakeConverter converter = new((_, _) => late.Task);
    using var request = await RequestInput();
    try
    {
      var options = new WorkerExecutionOptions { JobTimeout = TimeSpan.FromMilliseconds(20) };
      await Assert
        .That(
          await WorkerJobRunner
            .RunAsync(request, output, converter, options, TestToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestToken)
        )
        .IsEqualTo(2);
    }
    finally
    {
      late.TrySetException(new IOException("late private failure"));
    }
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Output_completion_and_error_headers_are_bounded_when_writes_ignore_cancellation(
    bool error
  )
  {
    using var input = await RequestInput();
    using StalledOutput output = new();
    FakeConverter converter = new(
      (_, _) =>
        Task.FromResult<ConversionError?>(
          error ? new ConversionError(ConversionErrorKind.RenderFailed, "private") : null
        )
    );
    var options = new WorkerExecutionOptions { JobTimeout = TimeSpan.FromMilliseconds(20) };
    await Assert
      .That(
        await WorkerJobRunner
          .RunAsync(input, output, converter, options, TestToken)
          .WaitAsync(TimeSpan.FromSeconds(2), TestToken)
      )
      .IsEqualTo(2);
  }

  [Test]
  public async Task Lifetime_timeout_bounds_a_longer_input_timeout()
  {
    using StalledInput input = new(ignoreCancellation: true);
    using MemoryStream output = new();
    FakeConverter converter = new((_, _) => Task.FromResult<ConversionError?>(null));
    var options = new WorkerExecutionOptions { LifetimeTimeout = TimeSpan.FromMilliseconds(20) };
    await Assert
      .That(
        await WorkerJobRunner
          .RunAsync(input, output, converter, options, TestToken)
          .WaitAsync(TimeSpan.FromSeconds(2), TestToken)
      )
      .IsEqualTo(2);
  }

  [Test]
  public async Task Clean_EOF_after_one_job_ends_a_reusable_worker()
  {
    using var input = await RequestInput();
    using MemoryStream output = new();
    FakeConverter converter = new((_, _) => Task.FromResult<ConversionError?>(null));
    await Assert
      .That(
        await WorkerJobRunner.RunAsync(
          input,
          output,
          converter,
          new WorkerExecutionOptions { MaxJobs = 2 },
          TestToken
        )
      )
      .IsEqualTo(0);
    await Assert.That(converter.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task Worker_configuration_reads_only_explicit_operational_values()
  {
    List<string> requested = [];
    var settings = WorkerSettings.Read(name =>
    {
      requested.Add(name);
      return name == "ATLI_WORKER_MAX_JOBS" ? "2" : null;
    });
    ReportsEngineOptions engine = new();
    settings.Configure(engine);
    await Assert.That(settings.NoSandbox).IsFalse();
    await Assert.That(settings.MaxJobs).IsEqualTo(2);
    await Assert.That(engine.Network.Mode).IsEqualTo(ReportsEngineNetworkMode.Disabled);
    await Assert.That(engine.Concurrency.MaxConcurrentConversions).IsEqualTo(1);
    await Assert.That(engine.Browser.EnvironmentVariables).IsEmpty();
    await Assert.That(requested).IsEquivalentTo(OperationalEnvironmentNames);
  }

  private static async Task<MemoryStream> RequestInput()
  {
    MemoryStream input = new();
    await WorkerProtocol.WriteRequestAsync(
      input,
      new WorkerRequest(1, Guid.NewGuid(), "safe"),
      TestToken
    );
    input.Position = 0;
    return input;
  }

  private sealed class StalledOutput : MemoryStream
  {
    private readonly TaskCompletionSource _released = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default
    ) => new(_released.Task);

    protected override void Dispose(bool disposing)
    {
      _released.TrySetResult();
      base.Dispose(disposing);
    }
  }

  private sealed class StalledInput(bool ignoreCancellation = false) : Stream
  {
    private readonly TaskCompletionSource _released = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default
    )
    {
      if (ignoreCancellation)
      {
        await _released.Task;
      }
      else
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      return 0;
    }

    protected override void Dispose(bool disposing)
    {
      _released.TrySetResult();
      base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
  }
}
