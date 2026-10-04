using System.Text.Json.Serialization;

namespace Atli.Reports.Hosting.Sandboxes;

// The data plane's JSON, as far as this client reads and writes it. Responses carry many more fields
// (resources, egress policy, quota, region); the client ignores them. Shapes observed on
// api-version 2026-02-01-preview, eastus2, 2026-10-04.

/// <summary>The body of <c>PUT sandboxes</c>.</summary>
internal sealed class CreateSandboxRequest
{
  public required SourcesRefWire SourcesRef { get; init; }

  public required ResourcesWire Resources { get; init; }

  public required EgressPolicyWire EgressPolicy { get; init; }

  public required IReadOnlyList<string> Entrypoint { get; init; }

  public required IReadOnlyDictionary<string, string> Environment { get; init; }

  public required IReadOnlyDictionary<string, string> Labels { get; init; }

  public required LifecycleWire Lifecycle { get; init; }
}

internal sealed class SourcesRefWire
{
  public required DiskImageWire DiskImage { get; init; }
}

internal sealed class DiskImageWire
{
  public required string Id { get; init; }

  public bool IsPublic { get; init; }
}

internal sealed class ResourcesWire
{
  public required string Cpu { get; init; }

  public required string Memory { get; init; }
}

internal sealed class EgressPolicyWire
{
  public required string DefaultAction { get; init; }
}

internal sealed class LifecycleWire
{
  public required AutoSuspendPolicyWire AutoSuspendPolicy { get; init; }
}

internal sealed class AutoSuspendPolicyWire
{
  public bool Enabled { get; init; }

  /// <summary>Idle seconds before the sandbox suspends; omitted when disabled.</summary>
  public int? Interval { get; init; }

  /// <summary><c>Memory</c>: suspend with the memory snapshot, so the browser resumes warm.</summary>
  public string? Mode { get; init; }
}

/// <summary>The body of <c>POST sandboxes/{id}/ports/add</c>.</summary>
internal sealed class AddPortRequest
{
  public int Port { get; init; }

  public required PortAuthWire Auth { get; init; }
}

internal sealed class PortAuthWire
{
  public bool Anonymous { get; init; }
}

/// <summary>
/// A sandbox in a response: <c>PUT sandboxes</c>, <c>GET sandboxes/{id}</c>, the elements of
/// <c>GET sandboxes</c>, and <c>POST sandboxes/{id}/resume</c>. <c>POST .../ports/add</c> answers
/// with only <c>ports</c>, and <c>POST .../stop</c> with the snapshot it took, whose <c>id</c> is the
/// snapshot's.
/// </summary>
internal sealed class SandboxResponse
{
  public string? Id { get; init; }

  public string? State { get; init; }

  public Dictionary<string, string>? Labels { get; init; }

  public List<PortResponse>? Ports { get; init; }
}

internal sealed class PortResponse
{
  public int Port { get; init; }

  public string? Url { get; init; }

  public PortAuthWire? Auth { get; init; }
}

[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(CreateSandboxRequest))]
[JsonSerializable(typeof(AddPortRequest))]
[JsonSerializable(typeof(SandboxResponse))]
[JsonSerializable(typeof(List<SandboxResponse>))]
internal sealed partial class SandboxesJsonContext : JsonSerializerContext;
