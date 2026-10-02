using Atli.Reports.Engine;
using Atli.Reports.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Standard output is exclusively the worker protocol. Even initialization failures are sanitized.
try
{
  if (args.Length != 0)
  {
    throw new InvalidOperationException("Worker command-line configuration is not supported.");
  }
  var settings = WorkerSettings.Read(Environment.GetEnvironmentVariable);
  ServiceCollection services = new();
  services.AddLogging(logging => logging.AddProvider(new WorkerLogProvider(Console.Error)));
  services.AddReportsEngine(settings.Configure);
  await using var provider = services.BuildServiceProvider();
  var hosted = provider.GetServices<IHostedService>().ToArray();
  using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(300));
  try
  {
    foreach (var service in hosted)
    {
      await service.StartAsync(lifetime.Token);
    }
    return await WorkerJobRunner.RunAsync(
      Console.OpenStandardInput(),
      Console.OpenStandardOutput(),
      provider.GetRequiredService<IHtmlToPdfConverter>(),
      new WorkerExecutionOptions { MaxJobs = settings.MaxJobs },
      lifetime.Token
    );
  }
  finally
  {
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    foreach (var service in hosted.Reverse())
    {
      await service.StopAsync(shutdown.Token);
    }
  }
}
catch (Exception)
{
  await Console.Error.WriteLineAsync("The reports worker failed.");
  return 2;
}

internal sealed class WorkerLogProvider(TextWriter error) : ILoggerProvider
{
  public ILogger CreateLogger(string categoryName) => new WorkerLogger(error);

  public void Dispose() { }

  private sealed class WorkerLogger(TextWriter error) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter
    )
    {
      if (IsEnabled(logLevel))
      {
        // Avoid forwarding browser output, document URLs, exception details, or host environment.
        error.WriteLine($"Reports worker diagnostic: {logLevel}, event {eventId.Id}.");
      }
    }
  }
}
