using System.Text.Json.Serialization;
using Atli.Reports.Server.Health;
using Atli.Reports.Server.Models;
using Microsoft.AspNetCore.Mvc;

namespace Atli.Reports.Server.Serialization;

[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(ConvertRequest))]
[JsonSerializable(typeof(PdfOptionsRequest))]
[JsonSerializable(typeof(MarginsRequest))]
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSerializable(typeof(ErrorResponse))]
// Results.Problem writes ProblemDetails; without this, NativeAOT builds cannot serialize error responses.
[JsonSerializable(typeof(ProblemDetails))]
public sealed partial class ServerJsonSerializerContext : JsonSerializerContext;
