using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.AppHost.Tests.Support;

/// <summary>
/// Writes the AppHost's logs to files, one per resource, so a failed run leaves the resources' console
/// output behind.
/// </summary>
/// <remarks>
/// Aspire.Hosting.Testing forwards each resource's console output to the logger category
/// <c>{AppHost}.Resources.{resource}</c>; those lines go to <c>{resource}.log</c>, and everything
/// else (the AppHost and the orchestrator) to <c>apphost.log</c>. Terminal color codes are removed.
/// </remarks>
internal sealed partial class FileLoggerProvider : ILoggerProvider
{
  private const string ResourceCategoryMarker = ".Resources.";

  private readonly string _directory;
  private readonly ConcurrentDictionary<string, LogFile> _files = new(StringComparer.Ordinal);

  public FileLoggerProvider(string directory)
  {
    _directory = directory;
    Directory.CreateDirectory(directory);
  }

  public ILogger CreateLogger(string categoryName)
  {
    var marker = categoryName.IndexOf(ResourceCategoryMarker, StringComparison.Ordinal);
    var fileName =
      marker >= 0 ? categoryName[(marker + ResourceCategoryMarker.Length)..] : "apphost";
    var file = _files.GetOrAdd(
      fileName,
      name => new LogFile(Path.Combine(_directory, $"{name}.log"))
    );
    return new FileLogger(file, categoryName);
  }

  public void Dispose()
  {
    foreach (var file in _files.Values)
    {
      file.Dispose();
    }
  }

  private sealed class LogFile(string path) : IDisposable
  {
    private readonly Lock _lock = new();
    private readonly StreamWriter _writer = new(path, append: false) { AutoFlush = true };
    private bool _disposed;

    public void WriteLine(string line)
    {
      lock (_lock)
      {
        if (!_disposed)
        {
          _writer.WriteLine(line);
        }
      }
    }

    public void Dispose()
    {
      lock (_lock)
      {
        _disposed = true;
        _writer.Dispose();
      }
    }
  }

  private sealed class FileLogger(LogFile file, string categoryName) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter
    )
    {
      var message = AnsiEscape().Replace(formatter(state, exception), string.Empty);
      if (exception is not null)
      {
        message += Environment.NewLine + exception;
      }

      file.WriteLine(
        string.Create(
          CultureInfo.InvariantCulture,
          $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {logLevel} {categoryName}: {message}"
        )
      );
    }
  }

  [GeneratedRegex(@"\x1B\[[0-9;]*m")]
  private static partial Regex AnsiEscape();
}
