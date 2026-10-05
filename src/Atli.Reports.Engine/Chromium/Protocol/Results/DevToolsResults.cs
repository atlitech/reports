using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Results;

/// <summary>
/// The result of <c>Target.createBrowserContext</c>.
/// </summary>
internal sealed class CreateBrowserContextResult
{
  [JsonPropertyName("browserContextId")]
  public string BrowserContextId { get; set; } = string.Empty;
}

/// <summary>
/// The result of <c>Target.createTarget</c>.
/// </summary>
internal sealed class CreateTargetResult
{
  [JsonPropertyName("targetId")]
  public string TargetId { get; set; } = string.Empty;
}

/// <summary>
/// The result of <c>Target.attachToTarget</c>.
/// </summary>
internal sealed class AttachToTargetResult
{
  [JsonPropertyName("sessionId")]
  public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// The result of <c>Page.printToPDF</c> with <c>transferMode: ReturnAsStream</c>.
/// </summary>
internal sealed class PrintToPdfResult
{
  [JsonPropertyName("stream")]
  public string? Stream { get; set; }
}

/// <summary>
/// The result of <c>Runtime.evaluate</c>.
/// </summary>
/// <remarks>
/// Only the exception is read: the engine evaluates scripts for their effect, so the
/// <c>result</c> value is skipped rather than deserialized.
/// </remarks>
internal sealed class EvaluateResult
{
  [JsonPropertyName("exceptionDetails")]
  public ExceptionDetails? ExceptionDetails { get; set; }
}

/// <summary>
/// A JavaScript value returned by <c>Runtime.evaluate</c>.
/// </summary>
internal sealed class RemoteObject
{
  [JsonPropertyName("description")]
  public string? Description { get; set; }
}

/// <summary>
/// Describes a JavaScript exception thrown by <c>Runtime.evaluate</c>.
/// </summary>
internal sealed class ExceptionDetails
{
  [JsonPropertyName("text")]
  public string? Text { get; set; }

  [JsonPropertyName("exception")]
  public RemoteObject? Exception { get; set; }
}

/// <summary>
/// Source-generated serialization for the DevTools results the engine reads.
/// </summary>
[JsonSerializable(typeof(CreateBrowserContextResult))]
[JsonSerializable(typeof(CreateTargetResult))]
[JsonSerializable(typeof(AttachToTargetResult))]
[JsonSerializable(typeof(PrintToPdfResult))]
[JsonSerializable(typeof(EvaluateResult))]
internal sealed partial class DevToolsResultsContext : JsonSerializerContext;
