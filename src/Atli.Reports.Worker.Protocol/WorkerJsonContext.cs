using System.Text.Json.Serialization;

namespace Atli.Reports.Worker.Protocol;

[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
  UseStringEnumConverter = true
)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerResponseHeader))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;
