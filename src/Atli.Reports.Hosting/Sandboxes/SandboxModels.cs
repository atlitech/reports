using System.Net;

namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>What to create: a sandbox from a private disk image.</summary>
public sealed record SandboxSpec
{
  /// <summary>The ID of a private disk image in the group.</summary>
  public required string DiskImageId { get; init; }

  /// <summary>CPU in the platform's notation, such as <c>1000m</c>.</summary>
  public required string Cpu { get; init; }

  /// <summary>Memory in the platform's notation, such as <c>2048Mi</c>.</summary>
  public required string Memory { get; init; }

  /// <summary>The entrypoint; the image's own is not run by the platform.</summary>
  public required IReadOnlyList<string> Entrypoint { get; init; }

  /// <summary>Environment variables, credentials among them; never logged.</summary>
  public IReadOnlyDictionary<string, string> Environment { get; init; } =
    new Dictionary<string, string>();

  /// <summary>Labels; immutable after creation.</summary>
  public IReadOnlyDictionary<string, string> Labels { get; init; } =
    new Dictionary<string, string>();

  /// <summary><c>Deny</c> or <c>Allow</c>; renderers use <c>Deny</c>.</summary>
  public string EgressDefaultAction { get; init; } = "Deny";

  /// <summary>Suspends the sandbox after this long without traffic; <see langword="null"/> never does.</summary>
  public TimeSpan? AutoSuspendAfter { get; init; }
}

/// <summary>A sandbox as the data plane reports it.</summary>
public sealed record SandboxView
{
  /// <summary>The sandbox ID, a UUID.</summary>
  public required string Id { get; init; }

  /// <summary>The lifecycle state, such as <see cref="SandboxStates.Running"/>.</summary>
  public required string State { get; init; }

  /// <summary>The sandbox's labels.</summary>
  public IReadOnlyDictionary<string, string> Labels { get; init; } =
    new Dictionary<string, string>();

  /// <summary>The exposed ports.</summary>
  public IReadOnlyList<SandboxPort> Ports { get; init; } = [];
}

/// <summary>A port exposed through the platform's proxy.</summary>
public sealed record SandboxPort(int Port, Uri Url, bool Anonymous);

/// <summary>Lifecycle states the data plane reports.</summary>
public static class SandboxStates
{
  /// <summary>Running, using CPU and memory.</summary>
  public const string Running = "Running";

  /// <summary>Suspended or stopped; storage only.</summary>
  public const string Stopped = "Stopped";
}

/// <summary>A data-plane call failed.</summary>
public sealed class SandboxesException : Exception
{
  /// <summary>Creates the exception.</summary>
  public SandboxesException() { }

  /// <summary>Creates the exception.</summary>
  public SandboxesException(string message)
    : base(message) { }

  /// <summary>Creates the exception.</summary>
  public SandboxesException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>Creates the exception for a response status.</summary>
  public SandboxesException(string message, HttpStatusCode statusCode)
    : base(message) => StatusCode = statusCode;

  /// <summary>The response status, when the data plane answered.</summary>
  public HttpStatusCode? StatusCode { get; }
}
