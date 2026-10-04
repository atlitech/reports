namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// The environment a renderer's server image runs with: API-key authentication that admits only the
/// gateway's credential for this renderer, and conversion permission only; the size's conversions
/// with a queue behind them; and a request body limit no body the gateway admitted can exceed.
/// Everything else keeps the image's defaults, Chromium's sandbox and disabled document networking
/// among them.
/// </summary>
public static class RendererServerEnvironment
{
  /// <summary>The caller identity the renderer gives the gateway in its logs and limits.</summary>
  public const string GatewayCallerId = "gateway";

  /// <summary>The server image's entrypoint; Sandboxes do not run the image's own.</summary>
  public static IReadOnlyList<string> Entrypoint { get; } =
  ["/usr/bin/tini", "--", "/app/Atli.Reports.Server"];

  /// <summary>
  /// How many of the gateway's requests a renderer admits at once per conversion it runs: one
  /// converting and one waiting in the engine's queue. A tenant's short burst then waits its turn
  /// in the renderer, for at most the engine's queue timeout, instead of being refused as busy;
  /// more would only wait longer, since a renderer converts about one document per vCPU at a time.
  /// </summary>
  public const int AdmittedRequestsPerConversion = 2;

  /// <summary>
  /// How many requests a renderer of <paramref name="size"/> admits from the gateway at once: its
  /// conversions and their queue. The provisioner records it, so the gateway can hold a tenant to it.
  /// </summary>
  public static int MaxConcurrentRequests(RendererSize size)
  {
    ArgumentNullException.ThrowIfNull(size);
    return size.MaxConcurrentConversions * AdmittedRequestsPerConversion;
  }

  /// <summary>
  /// The largest request body a renderer accepts: three times the server's default 10 MiB. The
  /// gateway writes the caller's request anew, and its JSON encoder can grow text up to about three
  /// times (a character outside the Basic Multilingual Plane becomes two <c>\uXXXX</c> escapes), so
  /// any body the gateway admitted fits.
  /// </summary>
  public const long MaxRequestBodyBytes = 3 * 10 * 1024 * 1024;

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
    var conversions = size.MaxConcurrentConversions;
    var admitted = MaxConcurrentRequests(size);
    return new Dictionary<string, string>
    {
      ["ReportsServer__Authentication__Mode"] = "ApiKey",
      ["ReportsServer__Authentication__ApiKeys__0__Id"] = credential.KeyId,
      ["ReportsServer__Authentication__ApiKeys__0__Hash"] = credential.Verifier,
      ["ReportsServer__Authentication__ApiKeys__0__CallerId"] = GatewayCallerId,
      ["ReportsServer__Authentication__ApiKeys__0__Permissions__0"] = "reports.convert",
      // The gateway is the only caller: what it may have in flight is what runs plus what queues.
      ["ReportsServer__Limits__MaxConcurrentRequestsPerCaller"] = Format(admitted),
      ["ReportsServer__Limits__MaxRequestBodyBytes"] = Format(MaxRequestBodyBytes),
      // Kestrel's own limit (30,000,000 bytes by default) also applies, and the lower one wins.
      ["Kestrel__Limits__MaxRequestBodySize"] = Format(MaxRequestBodyBytes),
      ["ReportsEngine__Concurrency__MaxConcurrentConversions"] = Format(conversions),
      ["ReportsEngine__Concurrency__MaxQueueLength"] = Format(admitted - conversions),
      ["ReportsEngine__Network__Mode"] = "Disabled",
    };
  }

  private static string Format(long value) =>
    value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
