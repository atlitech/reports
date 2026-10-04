using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// Everything logged through it, at every level, with each entry's exception and the state of
/// every scope begun, so a test can wait for an entry or search all of it for a secret.
/// </summary>
internal sealed class LogCapture : ILoggerProvider
{
  /// <summary>How long to wait for an entry before failing; only a failing test waits this long.</summary>
  private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

  private readonly Lock _lock = new();
  private readonly List<LogEntry> _entries = [];
  private readonly List<string> _scopes = [];
  private TaskCompletionSource _added = NewSignal();

  public IReadOnlyList<LogEntry> Entries
  {
    get
    {
      lock (_lock)
      {
        return [.. _entries];
      }
    }
  }

  /// <summary>Every entry, its exception, and every scope's state, one per line.</summary>
  public string Text
  {
    get
    {
      lock (_lock)
      {
        return string.Join("\n", [.. _entries.Select(entry => entry.ToString()), .. _scopes]);
      }
    }
  }

  /// <summary>The entries of the event <paramref name="eventId"/> in the category <typeparamref name="T"/>.</summary>
  public IReadOnlyList<LogEntry> Of<T>(int eventId) =>
    [
      .. Entries.Where(entry =>
        entry.Category == typeof(T).FullName && entry.EventId.Id == eventId
      ),
    ];

  /// <summary>Waits until <paramref name="count"/> entries of the event have been logged.</summary>
  public async Task WaitForAsync<T>(int eventId, int count = 1)
  {
    using CancellationTokenSource giveUp = new(GiveUpAfter);
    while (true)
    {
      Task added;
      lock (_lock)
      {
        if (
          _entries.Count(entry =>
            entry.Category == typeof(T).FullName && entry.EventId.Id == eventId
          ) >= count
        )
        {
          return;
        }

        added = _added.Task;
      }

      try
      {
        await added.WaitAsync(giveUp.Token);
      }
      catch (OperationCanceledException)
      {
        throw new TimeoutException(
          $"{typeof(T).Name} did not log event {eventId} {count} time(s). Logged:\n{Text}"
        );
      }
    }
  }

  public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

  public void Dispose() { }

  /// <summary>
  /// Called with each entry as it is logged, on the logging thread, before the code that logged it
  /// goes on: a test can act at exactly that point.
  /// </summary>
  public Action<LogEntry>? Logged { get; set; }

  private void Add(LogEntry entry)
  {
    lock (_lock)
    {
      _entries.Add(entry);
      _added.SetResult();
      _added = NewSignal();
    }

    Logged?.Invoke(entry);
  }

  private void AddScope(string? scope)
  {
    lock (_lock)
    {
      _scopes.Add($"scope: {scope}");
    }
  }

  private static TaskCompletionSource NewSignal() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  private sealed class Logger(LogCapture capture, string category) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull
    {
      capture.AddScope(state.ToString());
      return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter
    ) =>
      capture.Add(
        new LogEntry(category, logLevel, eventId, formatter(state, exception), exception)
      );
  }
}

/// <summary>One log entry.</summary>
internal sealed record LogEntry(
  string Category,
  LogLevel Level,
  EventId EventId,
  string Message,
  Exception? Exception
)
{
  public override string ToString() =>
    $"{Level} {Category}[{EventId.Id}]: {Message}" + (Exception is null ? "" : "\n" + Exception);
}
