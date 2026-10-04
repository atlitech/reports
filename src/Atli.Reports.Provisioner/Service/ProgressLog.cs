using System.Text;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// The provisioner's progress lines as log entries, one per line, so that the service logs in one
/// format: <c>[tenant] message</c> becomes <c>message</c> with the tenant as <c>TenantId</c>. The
/// lines name tenants, sandboxes, URLs, and disk images, never credentials.
/// </summary>
/// <remarks>
/// Not thread-safe; <see cref="RendererProvisioner"/> writes through
/// <see cref="TextWriter.Synchronized"/>.
/// </remarks>
internal sealed partial class ProgressLog(ILogger logger) : TextWriter
{
  private readonly StringBuilder _line = new();

  public override Encoding Encoding => Encoding.UTF8;

  public override void Write(char value)
  {
    if (value == '\n')
    {
      Emit();
    }
    else if (value != '\r')
    {
      _line.Append(value);
    }
  }

  public override void Write(string? value)
  {
    foreach (var character in value ?? "")
    {
      Write(character);
    }
  }

  public override void WriteLine(string? value)
  {
    Write(value);
    Emit();
  }

  private void Emit()
  {
    var line = _line.ToString();
    _line.Clear();
    var end = line.IndexOf("] ", StringComparison.Ordinal);
    if (line.StartsWith('[') && end > 1)
    {
      LogTenantProgress(logger, line[1..end], line[(end + 2)..]);
    }
    else if (line.Length > 0)
    {
      LogProgress(logger, line);
    }
  }

  [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "[{TenantId}] {Progress}")]
  private static partial void LogTenantProgress(ILogger logger, string tenantId, string progress);

  [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "{Progress}")]
  private static partial void LogProgress(ILogger logger, string progress);
}
