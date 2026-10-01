using System.Text.Json.Serialization;

namespace Atli.Reports.Engine.Chromium.Protocol.Responses;

/// <summary>
/// Response for CDP commands that return an empty result (e.g., Runtime.addBinding, Runtime.enable)
/// </summary>
internal sealed class EmptyResponse;

/// <summary>
/// Serialization context for EmptyResponse
/// </summary>
[JsonSerializable(typeof(DevToolsResponse<EmptyResponse>))]
internal sealed partial class EmptyResponseSerializationContext : JsonSerializerContext;
