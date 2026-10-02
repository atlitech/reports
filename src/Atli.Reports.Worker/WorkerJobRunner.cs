using Atli.Reports.Engine;
using Atli.Reports.Worker.Protocol;

namespace Atli.Reports.Worker;

/// <summary>
/// Executes correlated jobs sequentially. The default is one job; bounded reuse exists only for
/// operator-controlled benchmarks in one trust domain. Any job error stops reuse immediately.
/// </summary>
public static class WorkerJobRunner
{
  public static async Task<int> RunAsync(
    Stream input,
    Stream output,
    IHtmlToPdfConverter converter,
    WorkerExecutionOptions? options = null,
    CancellationToken cancellationToken = default
  )
  {
    options ??= new WorkerExecutionOptions();
    options.Validate();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    lifetime.CancelAfter(options.LifetimeTimeout);
    try
    {
      for (var job = 0; job < options.MaxJobs; job++)
      {
        WorkerRequest? request;
        using (var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
          inputDeadline.CancelAfter(options.InputTimeout);
          request = await AwaitBoundedAsync(
            WorkerProtocol.TryReadRequestAsync(input, inputDeadline.Token),
            inputDeadline.Token
          );
        }
        if (request is null)
        {
          return 0;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(options.JobTimeout);
        using WorkerPdfStream pdf = new(output, request.JobId);
        var result = await AwaitBoundedAsync(
          converter.ConvertAsync(request.Html, pdf, request.Options, deadline.Token).AsTask(),
          deadline.Token
        );
        if (result.TryPickT1(out var error, out _))
        {
          if (pdf.HasStarted)
          {
            // Never write a completion terminator or a second header for an incomplete PDF.
            return 2;
          }

          // Only an enum crosses this boundary. Engine exceptions, URLs, and document strings do not.
          await AwaitBoundedAsync(
            WorkerProtocol.WriteResponseHeaderAsync(
              output,
              new WorkerResponseHeader(
                WorkerProtocol.Version,
                request.JobId,
                WorkerProtocol.ErrorStatus,
                error.Kind
              ),
              deadline.Token
            ),
            deadline.Token
          );
          return 0;
        }

        await AwaitBoundedAsync(pdf.CompleteAsync(deadline.Token), deadline.Token);
      }
      return 0;
    }
    catch (Exception)
    {
      // A protocol failure, canceled I/O, or unexpected converter fault poisons this worker. The
      // parent classifies it using its deadline and process status; stdout receives no diagnostics.
      return 2;
    }
  }

  // Console pipe implementations may ignore cancellation after entering a blocking OS operation.
  // Stop awaiting at the deadline, observe late faults, and poison the process instead of reusing it.
  private static async Task<T> AwaitBoundedAsync<T>(
    Task<T> task,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await task.WaitAsync(cancellationToken);
    }
    catch
    {
      Observe(task);
      throw;
    }
  }

  private static async Task AwaitBoundedAsync(Task task, CancellationToken cancellationToken)
  {
    try
    {
      await task.WaitAsync(cancellationToken);
    }
    catch
    {
      Observe(task);
      throw;
    }
  }

  private static void Observe(Task task)
  {
    if (task.IsCompleted)
    {
      _ = task.Exception;
      return;
    }
    _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
  }
}

/// <summary>Bounded execution settings for a worker process, independent of request content.</summary>
public sealed class WorkerExecutionOptions
{
  public int MaxJobs { get; set; } = 1;
  public TimeSpan InputTimeout { get; set; } = TimeSpan.FromSeconds(15);
  public TimeSpan JobTimeout { get; set; } = TimeSpan.FromSeconds(90);
  public TimeSpan LifetimeTimeout { get; set; } = TimeSpan.FromSeconds(300);

  internal void Validate()
  {
    if (
      MaxJobs is < 1 or > 100
      || InputTimeout <= TimeSpan.Zero
      || InputTimeout > TimeSpan.FromSeconds(90)
      || JobTimeout <= TimeSpan.Zero
      || JobTimeout > TimeSpan.FromSeconds(90)
      || LifetimeTimeout <= TimeSpan.Zero
      || LifetimeTimeout > TimeSpan.FromSeconds(300)
    )
    {
      throw new ArgumentOutOfRangeException(
        nameof(MaxJobs),
        "Worker execution limits are outside the supported bounds."
      );
    }
  }
}
