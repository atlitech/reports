using System.Globalization;
using System.Net;
using System.Net.Sockets;

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

  /// <summary>
  /// The group's virtual network connection (<c>aca sandboxgroup network create --name</c>) to start
  /// the sandbox in, so that its DNS goes through that network and the network's rules apply to it;
  /// <see langword="null"/> for none. Fixed at creation.
  /// </summary>
  public string? NetworkConnectionName { get; init; }
}

/// <summary>A sandbox as the data plane reports it.</summary>
public sealed record SandboxView
{
  /// <summary>The sandbox ID, a UUID.</summary>
  public required string Id { get; init; }

  /// <summary>The lifecycle state, such as <see cref="SandboxStates.Running"/>.</summary>
  public required string State { get; init; }

  /// <summary>
  /// Why the sandbox last stopped, such as <see cref="SandboxStoppedReasons.Disabled"/>, or
  /// <see langword="null"/> when the data plane did not say. The data plane keeps the reason after
  /// a resume, so it means something only while the sandbox is stopped.
  /// </summary>
  public string? StoppedReason { get; init; }

  /// <summary>
  /// When the sandbox last stopped, or <see langword="null"/> when the data plane did not say or
  /// said it in a form this client cannot read. As with <see cref="StoppedReason"/>, it means
  /// something only while the sandbox is stopped.
  /// </summary>
  public DateTimeOffset? StoppedAt { get; init; }

  /// <summary>
  /// Whether the sandbox is stopped because it was disabled: the platform refuses to start it,
  /// whether a request reaches an on-demand port or something resumes it, until it is enabled.
  /// </summary>
  public bool IsDisabled =>
    State == SandboxStates.Stopped && StoppedReason == SandboxStoppedReasons.Disabled;

  /// <summary>The sandbox's labels.</summary>
  public IReadOnlyDictionary<string, string> Labels { get; init; } =
    new Dictionary<string, string>();

  /// <summary>The exposed ports.</summary>
  public IReadOnlyList<SandboxPort> Ports { get; init; } = [];

  /// <summary>When the sandbox was created, or <see langword="null"/> when the data plane did not say.</summary>
  public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>A port exposed through the platform's proxy.</summary>
/// <param name="Port">The port in the sandbox.</param>
/// <param name="Url">The port's public address.</param>
/// <param name="Anonymous">Whether the proxy admits requests without a user's sign-in.</param>
public sealed record SandboxPort(int Port, Uri Url, bool Anonymous)
{
  /// <summary>
  /// What a request to the port does while the sandbox is stopped:
  /// <see cref="SandboxPortActivation.Manual"/> when the data plane reports no mode, or one this
  /// client does not know.
  /// </summary>
  public SandboxPortActivation Activation { get; init; } = SandboxPortActivation.Manual;

  /// <summary>
  /// The source addresses the port admits, when the data plane reports an IP access control that
  /// denies all others; empty when it reports none.
  /// </summary>
  public IReadOnlyList<string> AllowedSourceCidrs { get; init; } = [];
}

/// <summary>How to expose a port through the platform's proxy.</summary>
public sealed record SandboxPortOptions
{
  /// <summary>The most IP access rules a port takes.</summary>
  public const int MaxRules = 10;

  /// <summary>The most source ranges one IP access rule takes.</summary>
  public const int MaxCidrsPerRule = 10;

  /// <summary>
  /// Whether the proxy admits requests without a user's sign-in. A port that is not anonymous
  /// needs one user's email, which this client does not set, so the data plane refuses it.
  /// </summary>
  public required bool Anonymous { get; init; }

  /// <summary>What a request to the port does while the sandbox is stopped.</summary>
  public SandboxPortActivation Activation { get; init; } = SandboxPortActivation.Manual;

  /// <summary>
  /// The source ranges the port admits, in CIDR notation (<c>203.0.113.7/32</c>); the proxy refuses
  /// every other address. Empty, the default, admits any address. At most
  /// <see cref="MaxRules"/> × <see cref="MaxCidrsPerRule"/>.
  /// </summary>
  /// <remarks>
  /// An anonymous port with <see cref="SandboxPortActivation.OnDemand"/> activation lets anyone
  /// who learns its URL wake the sandbox, and run up its compute, before the renderer checks a
  /// credential; limiting it to the callers' addresses closes that.
  /// </remarks>
  public IReadOnlyList<string> AllowedSourceCidrs { get; init; } = [];

  /// <summary>
  /// Throws <see cref="ArgumentException"/> unless the options can be sent: a known activation,
  /// and at most <see cref="MaxRules"/> × <see cref="MaxCidrsPerRule"/> source ranges, each an IPv4
  /// or IPv6 network in canonical CIDR notation.
  /// </summary>
  public void Validate()
  {
    if (!Enum.IsDefined(Activation))
    {
      throw new ArgumentException($"Unknown port activation {Activation}.", nameof(Activation));
    }

    ArgumentNullException.ThrowIfNull(AllowedSourceCidrs, nameof(AllowedSourceCidrs));
    if (AllowedSourceCidrs.Count > MaxRules * MaxCidrsPerRule)
    {
      throw new ArgumentException(
        $"A port admits at most {MaxRules * MaxCidrsPerRule} source ranges.",
        nameof(AllowedSourceCidrs)
      );
    }

    foreach (var cidr in AllowedSourceCidrs)
    {
      if (!IsCidr(cidr))
      {
        throw new ArgumentException(
          $"'{cidr}' is not a source range in CIDR notation, such as 203.0.113.7/32.",
          nameof(AllowedSourceCidrs)
        );
      }
    }
  }

  /// <summary>
  /// Whether <paramref name="value"/> is <c>address/prefix</c>: an IPv4 address in dotted quads or
  /// an IPv6 address without a scope, and a prefix no longer than the address with no address bits
  /// set past it. <c>10.0.0.1/24</c> is refused as ambiguous: a host, or its network?
  /// </summary>
  private static bool IsCidr(string? value)
  {
    var slash = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
    if (
      slash <= 0
      || !IPAddress.TryParse(value.AsSpan(0, slash), out var address)
      || !int.TryParse(
        value.AsSpan(slash + 1),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var prefix
      )
    )
    {
      return false;
    }

    // IPAddress also reads "10" as 0.0.0.10, and an IPv6 address with a %scope.
    if (
      address.AddressFamily == AddressFamily.InterNetwork
        ? !value.AsSpan(0, slash).SequenceEqual(address.ToString())
        : address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0
    )
    {
      return false;
    }

    var bytes = address.GetAddressBytes();
    if (prefix > bytes.Length * 8)
    {
      return false;
    }

    for (var bit = prefix; bit < bytes.Length * 8; bit++)
    {
      if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
      {
        return false;
      }
    }

    return true;
  }
}

/// <summary>What a request to an exposed port does while its sandbox is stopped.</summary>
public enum SandboxPortActivation
{
  /// <summary>
  /// The data plane's default: the proxy answers <c>403 {"error":"Sandbox is not running"}</c> until
  /// the sandbox is resumed.
  /// </summary>
  Manual,

  /// <summary>
  /// The request resumes the sandbox and is then served, with no resume call. Observed on
  /// 2026-10-04 with a plain listener: the first answer from a stopped sandbox came in about
  /// 1.1 seconds.
  /// </summary>
  OnDemand,
}

/// <summary>
/// The settled lifecycle states the data plane reports. Others, such as <c>Stopping</c>, are
/// transitions between them.
/// </summary>
public static class SandboxStates
{
  /// <summary>Running, using CPU and memory.</summary>
  public const string Running = "Running";

  /// <summary>Suspended or stopped; storage only.</summary>
  public const string Stopped = "Stopped";
}

/// <summary>
/// Reasons the data plane gives for a stopped sandbox (its <c>stateDetails.stoppedReason</c>),
/// as observed on 2026-10-04.
/// </summary>
public static class SandboxStoppedReasons
{
  /// <summary>Disabled: nothing can start it until it is enabled, after which it reads <see cref="UserStopped"/>.</summary>
  public const string Disabled = "Disabled";

  /// <summary>Suspended by its auto-suspend policy after an idle period.</summary>
  public const string Idle = "Idle";

  /// <summary>Stopped through the data plane, or enabled again after being disabled.</summary>
  public const string UserStopped = "UserStopped";
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
