using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine;
using Atli.Reports.Worker.Protocol;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Server.Execution;

/// <summary>
/// Experimental one-job worker gateway. The API authenticates and admits work before reaching
/// this adapter; only document data and PDF options cross its protocol boundary.
/// </summary>
internal sealed class WorkerConverter(
  ReportsExecutionOptions options,
  IHostApplicationLifetime lifetime
) : IHtmlToPdfConverter, IDisposable
{
  private readonly SemaphoreSlim _capacity = new(options.MaxConcurrentJobs);
  private readonly SemaphoreSlim _healthGate = new(1);
  private int _cleanupFailed;
  private HealthCheckResult _lastHealth;
  private long _healthAt;

  public async ValueTask<OneOf<Stream, ConversionError>> ConvertAsync(
    string html,
    PdfOptions? pdfOptions = null,
    CancellationToken cancellationToken = default
  )
  {
    var destination = new MemoryStream();
    try
    {
      var result = await ConvertAsync(html, destination, pdfOptions, cancellationToken);
      if (result.IsT1)
      {
        destination.Dispose();
        return result.AsT1;
      }
      destination.Position = 0;
      return destination;
    }
    catch
    {
      destination.Dispose();
      throw;
    }
  }

  public async ValueTask<OneOf<Success, ConversionError>> ConvertAsync(
    string html,
    Stream destination,
    PdfOptions? pdfOptions = null,
    CancellationToken cancellationToken = default
  )
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(destination);
    if (!destination.CanWrite)
    {
      throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }
    if (cancellationToken.IsCancellationRequested)
    {
      return Error(ConversionErrorKind.Canceled);
    }
    if (
      string.IsNullOrWhiteSpace(html)
      || Encoding.UTF8.GetByteCount(html) > WorkerProtocol.MaxRequestBytes
    )
    {
      return Error(ConversionErrorKind.InvalidRequest);
    }
    if (
      Volatile.Read(ref _cleanupFailed) != 0
      || lifetime.ApplicationStopping.IsCancellationRequested
    )
    {
      return Error(ConversionErrorKind.BrowserUnavailable);
    }
    if (!_capacity.Wait(0, CancellationToken.None))
    {
      return Error(ConversionErrorKind.Busy);
    }

    using var deadline = new CancellationTokenSource(options.Timeout);
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken,
      lifetime.ApplicationStopping,
      deadline.Token
    );
    var jobId = Guid.NewGuid();
    var containerName = "atli-reports-worker-" + jobId.ToString("N");
    var directory = Path.Combine(Path.GetTempPath(), containerName);
    Process? process = null;
    Task? writer = null;
    Task<ConversionError?>? reader = null;
    Task? stderr = null;
    var launched = false;
    OneOf<Success, ConversionError> outcome = new Success();
    try
    {
      // Validate and bound serialization before launching a worker. The buffer is private to the
      // admitted job, and is cleared before it is released.
      using var frame = new MemoryStream();
      try
      {
        await WorkerProtocol.WriteRequestAsync(
          frame,
          new WorkerRequest(WorkerProtocol.Version, jobId, html, pdfOptions),
          cancellation.Token
        );
      }
      catch (Exception exception)
        when (exception is InvalidDataException or JsonException or ArgumentException)
      {
        Array.Clear(frame.GetBuffer());
        return Error(ConversionErrorKind.InvalidRequest);
      }
      catch
      {
        Array.Clear(frame.GetBuffer());
        throw;
      }
      try
      {
        frame.Position = 0;
        if (OperatingSystem.IsWindows())
        {
          Directory.CreateDirectory(directory);
        }
        else
        {
          Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
          );
        }
        process = new Process
        {
          StartInfo = WorkerLauncher.Create(options, directory, containerName),
        };
        launched = process.Start();
        if (!launched)
        {
          return Error(ConversionErrorKind.BrowserUnavailable);
        }
        stderr = DrainAsync(process.StandardError.BaseStream, cancellation.Token);
        writer = SendRequestAsync(process, frame, cancellation.Token);
        reader = ReadResultAsync(
          process.StandardOutput.BaseStream,
          jobId,
          destination,
          cancellation.Token
        );
        var first = await Task.WhenAny(writer, reader).WaitAsync(cancellation.Token);
        // If either half fails, stop the other immediately rather than deadlocking on two full pipes.
        if (!first.IsCompletedSuccessfully)
        {
          await cancellation.CancelAsync();
          await first;
        }
        await writer.WaitAsync(cancellation.Token);
        var error = await reader.WaitAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);
        outcome =
          process.ExitCode != 0 ? Error(ConversionErrorKind.RenderFailed)
          : error is null ? new Success()
          : error;
      }
      finally
      {
        // HTML can contain confidential business data. Do not leave it in reusable buffers.
        Array.Clear(frame.GetBuffer());
      }
    }
    catch (DestinationWriteException exception)
    {
      if (
        exception.InnerException is OperationCanceledException
        && (
          cancellationToken.IsCancellationRequested
          || deadline.IsCancellationRequested
          || lifetime.ApplicationStopping.IsCancellationRequested
        )
      )
      {
        outcome = CancellationError(deadline, cancellationToken);
      }
      else
      {
        ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
        throw;
      }
    }
    catch (OperationCanceledException)
    {
      outcome = CancellationError(deadline, cancellationToken);
    }
    catch (Win32Exception)
    {
      outcome = Error(ConversionErrorKind.BrowserUnavailable);
    }
    catch (Exception exception)
      when (exception
          is IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
      )
    {
      outcome = Error(
        launched ? ConversionErrorKind.RenderFailed : ConversionErrorKind.BrowserUnavailable
      );
    }
    finally
    {
      await cancellation.CancelAsync();
      var cleaned = await CleanupAsync(
        process,
        launched,
        directory,
        containerName,
        writer,
        reader,
        stderr
      );
      if (!cleaned)
      {
        Interlocked.Exchange(ref _cleanupFailed, 1);
      }
      _capacity.Release();
    }
    return Volatile.Read(ref _cleanupFailed) != 0 && outcome.IsT0
      ? Error(ConversionErrorKind.BrowserUnavailable)
      : outcome;
  }

  private async Task<ConversionError?> ReadResultAsync(
    Stream input,
    Guid jobId,
    Stream destination,
    CancellationToken cancellationToken
  )
  {
    var header = await WorkerProtocol.ReadResponseHeaderAsync(input, cancellationToken);
    if (header.JobId != jobId)
    {
      throw new InvalidDataException("The worker answered a different job.");
    }
    if (header.Status == WorkerProtocol.ErrorStatus)
    {
      await RequireEofAsync(input, cancellationToken);
      return Error(header.Kind!.Value);
    }

    var buffer = ArrayPool<byte>.Shared.Rent(WorkerProtocol.MaxChunkBytes);
    var prefix = new byte[5];
    var prefixLength = 0;
    long total = 0;
    try
    {
      while (true)
      {
        var count = await WorkerProtocol.ReadPdfChunkAsync(input, buffer, cancellationToken);
        if (count == 0)
        {
          if (prefixLength != prefix.Length)
          {
            throw new InvalidDataException("The worker output is not a PDF.");
          }
          await RequireEofAsync(input, cancellationToken);
          return null;
        }
        total += count;
        if (total > options.MaxPdfBytes)
        {
          throw new InvalidDataException("The worker PDF exceeds its output limit.");
        }
        var offset = 0;
        if (prefixLength < prefix.Length)
        {
          offset = Math.Min(prefix.Length - prefixLength, count);
          buffer.AsSpan(0, offset).CopyTo(prefix.AsSpan(prefixLength));
          prefixLength += offset;
          if (prefixLength < prefix.Length)
          {
            continue;
          }
          if (!prefix.AsSpan().SequenceEqual("%PDF-"u8))
          {
            throw new InvalidDataException("The worker output is not a PDF.");
          }
          await WriteDestinationAsync(destination, prefix, cancellationToken);
        }
        await WriteDestinationAsync(
          destination,
          buffer.AsMemory(offset, count - offset),
          cancellationToken
        );
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
      Array.Clear(prefix);
    }
  }

  private static async Task WriteDestinationAsync(
    Stream destination,
    ReadOnlyMemory<byte> bytes,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await destination.WriteAsync(bytes, cancellationToken);
    }
    catch (Exception exception)
    {
      throw new DestinationWriteException(exception);
    }
  }

  private static async Task RequireEofAsync(Stream input, CancellationToken cancellationToken)
  {
    var one = new byte[1];
    if (await input.ReadAsync(one, cancellationToken) != 0)
    {
      throw new InvalidDataException("The worker sent trailing data.");
    }
  }

  private static async Task SendRequestAsync(
    Process process,
    Stream frame,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await frame.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
      await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }
    finally
    {
      process.StandardInput.Close();
    }
  }

  private static async Task DrainAsync(Stream input, CancellationToken cancellationToken)
  {
    // Stderr is untrusted and can contain HTML, URLs, or credentials. Drain without retaining or
    // logging it so a noisy child cannot deadlock or grow an unbounded diagnostic buffer.
    var buffer = ArrayPool<byte>.Shared.Rent(4096);
    try
    {
      while (await input.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken) != 0) { }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
  }

  private async Task<bool> CleanupAsync(
    Process? process,
    bool launched,
    string directory,
    string containerName,
    params Task?[] ioTasks
  )
  {
    var clean = true;
    var naturalSuccess = false;
    using var timeout = new CancellationTokenSource(options.CleanupTimeout);
    if (process is not null && launched)
    {
      try
      {
        naturalSuccess = process.HasExited && process.ExitCode == 0;
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync(timeout.Token);
      }
      catch (Exception exception)
        when (exception is InvalidOperationException or Win32Exception or OperationCanceledException
        )
      {
        clean = false;
      }
    }
    if (options.Backend == "Docker" && launched)
    {
      // The CLI can die while its container continues running. Address only this invocation's
      // unpredictable owned name, independent of the lifetime of the original docker process.
      try
      {
        var result = await CaptureCommandAsync(
          WorkerLauncher.DockerCommand(options, directory, "rm", "--force", containerName),
          timeout.Token
        );
        clean &=
          result.Code == 0
          || (
            naturalSuccess
            && result.Error.Contains("No such container", StringComparison.OrdinalIgnoreCase)
          );
      }
      catch (Exception exception)
        when (exception
            is IOException
              or InvalidDataException
              or Win32Exception
              or InvalidOperationException
              or OperationCanceledException
        )
      {
        clean = false;
      }
    }
    foreach (var task in ioTasks)
    {
      if (task is null)
        continue;
      try
      {
        await task.WaitAsync(timeout.Token);
      }
      catch (Exception)
      {
        if (!task.IsCompleted)
        {
          clean = false;
          _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
          );
        }
      }
    }
    process?.Dispose();
    try
    {
      if (Directory.Exists(directory))
      {
        var removal = Task.Run(
          () => Directory.Delete(directory, recursive: true),
          CancellationToken.None
        );
        try
        {
          await removal.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
          clean = false;
          _ = removal.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
          );
        }
      }
    }
    catch (IOException)
    {
      clean = false;
    }
    catch (UnauthorizedAccessException)
    {
      clean = false;
    }
    return clean;
  }

  internal async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
  {
    if (Volatile.Read(ref _cleanupFailed) != 0)
      return HealthCheckResult.Unhealthy(
        "Worker cleanup could not be confirmed; restart after inspecting owned workers."
      );
    if (lifetime.ApplicationStopping.IsCancellationRequested)
      return HealthCheckResult.Unhealthy("Worker admission is stopping.");
    var executable =
      options.Backend == "Process" ? options.ProcessExecutablePath : options.DockerExecutablePath;
    if (!File.Exists(executable))
      return HealthCheckResult.Unhealthy("The configured worker launcher is unavailable.");
    if (options.Backend == "Process")
      return HealthCheckResult.Healthy(
        "Development worker executable is present; workers start per job."
      );
    await _healthGate.WaitAsync(cancellationToken);
    try
    {
      if (_healthAt != 0 && Stopwatch.GetElapsedTime(_healthAt) < TimeSpan.FromSeconds(5))
        return _lastHealth;
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(options.CleanupTimeout);
      var healthDirectory = Path.Combine(
        Path.GetTempPath(),
        "atli-worker-health-" + Guid.NewGuid().ToString("N")
      );
      try
      {
        if (OperatingSystem.IsWindows())
          Directory.CreateDirectory(healthDirectory);
        else
          Directory.CreateDirectory(
            healthDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
          );
        var result = await CaptureCommandAsync(
          WorkerLauncher.DockerCommand(
            options,
            healthDirectory,
            "info",
            "--format",
            "{{json .Runtimes}}"
          ),
          timeout.Token
        );
        using var runtimes = JsonDocument.Parse(result.Output);
        _lastHealth =
          result.Code == 0 && runtimes.RootElement.TryGetProperty(options.Runtime, out _)
            ? HealthCheckResult.Healthy(
              "The configured Docker runtime is available; workers start per job."
            )
            : HealthCheckResult.Unhealthy(
              "The required Docker runtime is unavailable; no fallback is enabled."
            );
      }
      catch (Exception exception)
        when (exception
            is IOException
              or InvalidDataException
              or Win32Exception
              or InvalidOperationException
              or OperationCanceledException
              or JsonException
        )
      {
        _lastHealth = HealthCheckResult.Unhealthy(
          "The configured Docker runtime could not be checked."
        );
      }
      finally
      {
        // Probe directories are launcher configuration roots, never document workspaces.
        try
        {
          Directory.Delete(healthDirectory);
        }
        catch (IOException)
        {
          Interlocked.Exchange(ref _cleanupFailed, 1);
        }
        catch (UnauthorizedAccessException)
        {
          Interlocked.Exchange(ref _cleanupFailed, 1);
        }
      }
      _healthAt = Stopwatch.GetTimestamp();
      return _lastHealth;
    }
    finally
    {
      _healthGate.Release();
    }
  }

  private static async Task<(int Code, string Output, string Error)> CaptureCommandAsync(
    ProcessStartInfo info,
    CancellationToken cancellationToken
  )
  {
    using var process = new Process { StartInfo = info };
    if (!process.Start())
      throw new InvalidOperationException("The worker launcher did not start.");
    process.StandardInput.Close();
    try
    {
      var output = ReadBoundedTextAsync(process.StandardOutput.BaseStream, cancellationToken);
      var error = ReadBoundedTextAsync(process.StandardError.BaseStream, cancellationToken);
      await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken));
      return (process.ExitCode, await output, await error);
    }
    finally
    {
      try
      {
        if (!process.HasExited)
          process.Kill(entireProcessTree: true);
      }
      catch (InvalidOperationException) { }
      catch (Win32Exception) { }
    }
  }

  private static async Task<string> ReadBoundedTextAsync(
    Stream input,
    CancellationToken cancellationToken
  )
  {
    var buffer = new byte[64 * 1024 + 1];
    var length = 0;
    while (length < buffer.Length)
    {
      var count = await input.ReadAsync(buffer.AsMemory(length), cancellationToken);
      if (count == 0)
        return Encoding.UTF8.GetString(buffer, 0, length);
      length += count;
    }
    throw new InvalidDataException("The launcher response exceeds its limit.");
  }

  private static ConversionError Error(ConversionErrorKind kind) =>
    new(
      kind,
      kind switch
      {
        ConversionErrorKind.InvalidRequest => "The document cannot be submitted to the worker.",
        ConversionErrorKind.Busy => "No worker capacity is available. Retry later.",
        ConversionErrorKind.Canceled => "The conversion was canceled.",
        ConversionErrorKind.Timeout => "The worker conversion exceeded its time limit.",
        ConversionErrorKind.BrowserUnavailable => "The worker execution service is unavailable.",
        ConversionErrorKind.PolicyDenied => "The document violates the worker rendering policy.",
        _ => "The worker could not complete the conversion.",
      }
    );

  private ConversionError CancellationError(
    CancellationTokenSource deadline,
    CancellationToken caller
  ) =>
    Error(
      caller.IsCancellationRequested ? ConversionErrorKind.Canceled
      : deadline.IsCancellationRequested ? ConversionErrorKind.Timeout
      : lifetime.ApplicationStopping.IsCancellationRequested
        ? ConversionErrorKind.BrowserUnavailable
      : ConversionErrorKind.RenderFailed
    );

  public void Dispose()
  {
    _capacity.Dispose();
    _healthGate.Dispose();
  }

  private sealed class DestinationWriteException(Exception inner)
    : Exception("The PDF destination rejected a write.", inner);
}
