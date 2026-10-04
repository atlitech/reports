namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// The environment a renderer's server image runs with: API-key authentication that admits only the
/// gateway's credential for this renderer, and conversion permission only. Everything else keeps the
/// image's defaults, Chromium's sandbox and disabled document networking among them.
/// </summary>
public static class RendererServerEnvironment
{
  /// <summary>The caller identity the renderer gives the gateway in its logs and limits.</summary>
  public const string GatewayCallerId = "gateway";

  /// <summary>The server image's entrypoint; Sandboxes do not run the image's own.</summary>
  public static IReadOnlyList<string> Entrypoint { get; } =
  ["/usr/bin/tini", "--", "/app/Atli.Reports.Server"];

  /// <summary>Builds the environment for a renderer that admits <paramref name="credential"/>.</summary>
  /// <param name="credential">The renderer's own credential; only its verifier enters the environment.</param>
  /// <param name="size">The renderer's size, which sets its concurrency.</param>
  public static IReadOnlyDictionary<string, string> Create(
    RendererCredential credential,
    RendererSize size
  )
  {
    ArgumentNullException.ThrowIfNull(credential);
    ArgumentNullException.ThrowIfNull(size);
    return new Dictionary<string, string>
    {
      ["ReportsServer__Authentication__Mode"] = "ApiKey",
      ["ReportsServer__Authentication__ApiKeys__0__Id"] = credential.KeyId,
      ["ReportsServer__Authentication__ApiKeys__0__Hash"] = credential.Verifier,
      ["ReportsServer__Authentication__ApiKeys__0__CallerId"] = GatewayCallerId,
      ["ReportsServer__Authentication__ApiKeys__0__Permissions__0"] = "reports.convert",
      ["ReportsServer__Limits__MaxConcurrentRequestsPerCaller"] =
        size.MaxConcurrentConversions.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["ReportsEngine__Concurrency__MaxConcurrentConversions"] =
        size.MaxConcurrentConversions.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["ReportsEngine__Network__Mode"] = "Disabled",
    };
  }
}
