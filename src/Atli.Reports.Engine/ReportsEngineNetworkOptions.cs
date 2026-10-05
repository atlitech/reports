namespace Atli.Reports.Engine;

/// <summary>Controls document access to external assets.</summary>
public sealed class ReportsEngineNetworkOptions
{
  /// <summary>
  /// Defaults to <see cref="ReportsEngineNetworkMode.Disabled"/> for both embedded applications
  /// and the standalone server. Opt in to external assets explicitly. Restricted modes are
  /// defense in depth for controlled documents, not containment of a compromised browser.
  /// </summary>
  public ReportsEngineNetworkMode Mode { get; set; } = ReportsEngineNetworkMode.Disabled;

  /// <summary>
  /// Exact HTTP(S) origins permitted in allowlist mode, for example <c>https://assets.example.com</c>.
  /// No paths, credentials, wildcards, or private IP destinations are accepted. Redirect destinations
  /// must also be allowed. The broker never forwards document cookies or authorization headers.
  /// </summary>
  public IList<string> AllowedOrigins { get; } = [];

  /// <summary>Maximum asset requests, including redirect hops, per conversion. Defaults to 100.</summary>
  public int MaxRequests { get; set; } = 100;

  /// <summary>Maximum decoded bytes in one asset response. Defaults to 10 MiB.</summary>
  public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

  /// <summary>Maximum decoded asset bytes fetched per conversion. Defaults to 50 MiB.</summary>
  public long MaxTotalResponseBytes { get; set; } = 50 * 1024 * 1024;

  /// <summary>Maximum time for an asset, including redirects and body reads. Defaults to 15 seconds.</summary>
  public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Document network access policy, configured by the host rather than the conversion request.</summary>
public enum ReportsEngineNetworkMode
{
  /// <summary>Browser networking follows the host's configuration. Only use for trusted documents.</summary>
  Unrestricted,

  /// <summary>Reject external resources. Inline and data URI assets remain available.</summary>
  Disabled,

  /// <summary>
  /// Fetch approved public HTTP(S) assets through a bounded broker. Only GET and HEAD are supported;
  /// external document navigation, browser workers' networking, and WebSockets are unavailable.
  /// </summary>
  AllowList,
}
