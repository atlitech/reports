using System.Globalization;
using System.Text.RegularExpressions;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Chromium.Discovery;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Health;

/// <summary>
/// Reports whether the engine can launch its browser. Unhealthy while the engine shuts down, and
/// while the most recent launch has failed (with the reason, until a launch succeeds). Otherwise
/// healthy while a browser runs; when none runs (before the first launch, or after an idle close,
/// a recycle, or a crash), healthy as long as the executable the next launch would start exists.
/// </summary>
/// <remarks>
/// The failure reason quotes the browser's own output, and health endpoints are often reachable
/// without authentication, so it is reduced to one line of at most <see cref="MaxReasonLength"/>
/// characters, with URL credentials, DevTools target ids, and secret-looking parameters masked.
/// </remarks>
internal sealed partial class BrowserHealthCheck(
  BrowserManager browsers,
  IOptions<ReportsEngineOptions> options
) : IHealthCheck
{
  /// <summary>
  /// The longest failure reason the check reports.
  /// </summary>
  internal const int MaxReasonLength = 500;

  private const string Mask = "***";
  private const string Elision = " ... ";

  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  )
  {
    var status = browsers.GetStatus();
    if (status.ShuttingDown)
    {
      return Unhealthy(context, "The reports engine is shutting down.");
    }

    if (status.LaunchFailure is { } failure)
    {
      var retrying = status.Retrying ? "; retrying in the background" : string.Empty;
      return Unhealthy(
        context,
        string.Create(
          CultureInfo.InvariantCulture,
          $"The browser failed to start ({status.FailedLaunches} failed launch(es) in a row){retrying}. {SanitizeReason(failure)}"
        )
      );
    }

    if (status.Running is { } browser)
    {
      return Task.FromResult(
        HealthCheckResult.Healthy(
          string.Create(
            CultureInfo.InvariantCulture,
            $"Browser {browser.Generation} is running (process {browser.ProcessId})."
          )
        )
      );
    }

    var browserOptions = options.Value.Browser;
    var path = string.IsNullOrEmpty(browserOptions.ExecutablePath)
      ? BrowserFinder.Find(browserOptions.Kind)
      : browserOptions.ExecutablePath;

    if (path is not null && File.Exists(path))
    {
      return Task.FromResult(
        HealthCheckResult.Healthy($"Not running; the next conversion starts {path}.")
      );
    }

    return Unhealthy(
      context,
      string.IsNullOrEmpty(path)
        ? $"No {browserOptions.Kind} executable was found. Install it or set ReportsEngine:Browser:ExecutablePath."
        : $"The browser executable '{path}' does not exist."
    );
  }

  /// <summary>
  /// Reduces a launch failure to one line of at most <see cref="MaxReasonLength"/> characters, with
  /// URL credentials, DevTools target ids, and secret-looking parameters masked. A long reason keeps
  /// its start (what failed) and its end (the browser's last words), which explain the most.
  /// </summary>
  internal static string SanitizeReason(string reason)
  {
    var masked = UrlCredentials().Replace(reason, Mask + "@");
    masked = DevToolsTargetId().Replace(masked, Mask);
    masked = SecretParameter().Replace(masked, "$1=" + Mask);
    var line = Whitespace().Replace(masked, " ").Trim();
    if (line.Length <= MaxReasonLength)
    {
      return line;
    }

    var keep = (MaxReasonLength - Elision.Length) / 2;
    var headEnd = char.IsHighSurrogate(line[keep - 1]) ? keep - 1 : keep;
    var tailStart = line.Length - keep;
    if (char.IsLowSurrogate(line[tailStart]))
    {
      tailStart++;
    }

    return string.Concat(line.AsSpan(0, headEnd), Elision, line.AsSpan(tailStart));
  }

  private static Task<HealthCheckResult> Unhealthy(
    HealthCheckContext context,
    string description
  ) => Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, description));

  /// <summary><c>user:password@</c> in <c>scheme://user:password@host</c>.</summary>
  [GeneratedRegex(@"(?<=://)[^\s/?#@]+@", RegexOptions.CultureInvariant)]
  private static partial Regex UrlCredentials();

  /// <summary>The id in <c>/devtools/browser/&lt;id&gt;</c>, which addresses a DevTools target.</summary>
  [GeneratedRegex(@"(?<=/devtools/[A-Za-z]+/)[^\s/?#'""|]+", RegexOptions.CultureInvariant)]
  private static partial Regex DevToolsTargetId();

  /// <summary>The value of <c>token=</c>, <c>--password=</c>, <c>api_key=</c>, and the like.</summary>
  [GeneratedRegex(
    @"\b(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret|signature|sig)=[^\s&;,'""|]+",
    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
  )]
  private static partial Regex SecretParameter();

  /// <summary>Runs of whitespace and control characters (line breaks, tabs, escape sequences).</summary>
  [GeneratedRegex(@"[\s\p{Cc}]+", RegexOptions.CultureInvariant)]
  private static partial Regex Whitespace();
}
