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
// Results.Problem writes ProblemDetails; without this, NativeAOT builds cannot serialize error responses.
[JsonSerializable(typeof(ProblemDetails))]
// Never serialized, but the OpenAPI document needs metadata for every type it describes, including
// the Stream of the PDF response (which it describes as binary content); without it, NativeAOT builds
// fail to generate the document.
[JsonSerializable(typeof(Stream))]
internal sealed partial class ServerJsonSerializerContext : JsonSerializerContext;
