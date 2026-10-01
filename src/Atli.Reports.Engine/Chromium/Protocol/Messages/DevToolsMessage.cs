using System.Globalization;
using System.Text.Json;

namespace Atli.Reports.Engine.Chromium.Protocol.Messages;

/// <summary>
/// A DevTools Protocol command: a method name and flat parameters.
/// </summary>
/// <remarks>
/// Parameter values must be <see cref="string"/>, <see cref="bool"/>, <see cref="int"/>,
/// <see cref="long"/>, or <see cref="double"/>. The message is written straight to UTF-8 JSON by
/// <see cref="WriteTo"/>, so no reflection-based serialization is involved.
/// </remarks>
internal sealed class DevToolsMessage(string method)
{
  /// <summary>
  /// The DevTools method, for example <c>Page.printToPDF</c>.
  /// </summary>
  public string Method { get; } = method;

  /// <summary>
  /// The command parameters, written as the <c>params</c> object.
  /// </summary>
  public Dictionary<string, object> Parameters { get; } = [];

  /// <summary>
  /// Writes the command as <c>{"id":…,"method":…,"sessionId":…,"params":{…}}</c>.
  /// </summary>
  public void WriteTo(Utf8JsonWriter writer, int id, string? sessionId)
  {
    writer.WriteStartObject();
    writer.WriteNumber("id"u8, id);
    writer.WriteString("method"u8, Method);
    if (sessionId is not null)
    {
      writer.WriteString("sessionId"u8, sessionId);
    }

    writer.WriteStartObject("params"u8);
    foreach (var (name, value) in Parameters)
    {
      switch (value)
      {
        case string text:
          writer.WriteString(name, text);
          break;
        case bool flag:
          writer.WriteBoolean(name, flag);
          break;
        case int number:
          writer.WriteNumber(name, number);
          break;
        case long number:
          writer.WriteNumber(name, number);
          break;
        case double number:
          writer.WriteNumber(name, number);
          break;
        default:
          throw new NotSupportedException(
            string.Create(
              CultureInfo.InvariantCulture,
              $"DevTools parameter '{name}' has the unsupported type {value.GetType()}."
            )
          );
      }
    }

    writer.WriteEndObject();
    writer.WriteEndObject();
  }
}
