using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// A logger provider that keeps every entry, so a test can check the engine's structured logs.
/// </summary>
internal sealed class LogCollector : ILoggerProvider
{
  private readonly ConcurrentQueue<LogEntry> _entries = new();

  /// <summary>
  /// The entries logged so far, in order.
  /// </summary>
  public IReadOnlyList<LogEntry> Entries => [.. _entries];

  /// <summary>
  /// The entries logged so far with event id <paramref name="eventId"/>.
  /// </summary>
  public IReadOnlyList<LogEntry> WithEventId(int eventId) =>
    [.. _entries.Where(entry => entry.EventId == eventId)];

  public ILogger CreateLogger(string categoryName) => new Logger(this);

  public void Dispose() { }

  private sealed class Logger(LogCollector collector) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter
    ) =>
      collector._entries.Enqueue(
        new LogEntry(
          logLevel,
          eventId.Id,
          formatter(state, exception),
          // Copied now: some generators (the server's, through Microsoft.Extensions.Telemetry)
          // pool their state and clear it once Log returns.
          state is IReadOnlyList<KeyValuePair<string, object?>> values
            ? [.. values]
            : []
        )
      );
  }
}

/// <summary>
/// One log entry, with the values of its message template.
/// </summary>
internal sealed record LogEntry(
  LogLevel Level,
  int EventId,
  string Message,
  IReadOnlyList<KeyValuePair<string, object?>> Values
)
{
  /// <summary>
  /// The value of the template placeholder <paramref name="name"/>.
  /// </summary>
  public object? this[string name] => Values.Single(value => value.Key == name).Value;
}
