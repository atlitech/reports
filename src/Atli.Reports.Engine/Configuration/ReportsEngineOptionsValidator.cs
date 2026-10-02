using Atli.Reports.Engine.Chromium.Network;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Configuration;

/// <summary>
/// Rejects option values the engine cannot work with, when the options are first used (or at host
/// start-up).
/// </summary>
internal sealed class ReportsEngineOptionsValidator : IValidateOptions<ReportsEngineOptions>
{
  public ValidateOptionsResult Validate(string? name, ReportsEngineOptions options)
  {
    List<string> failures = [];
    var browser = options.Browser;
    var concurrency = options.Concurrency;

    RequirePositive(failures, "Browser:StartupTimeout", browser.StartupTimeout);
    RequirePositive(failures, "Browser:CommandTimeout", browser.CommandTimeout);
    RequirePositiveOrInfinite(failures, "Browser:MaxProcessLifetime", browser.MaxProcessLifetime);
    RequirePositiveOrInfinite(failures, "ConversionTimeout", options.ConversionTimeout);
    RequirePositiveOrInfinite(failures, "Browser:IdleTimeout", browser.IdleTimeout);
    RequirePositive(failures, "Network:RequestTimeout", options.Network.RequestTimeout);

    if (!Enum.IsDefined(options.Network.Mode))
    {
      failures.Add("Network:Mode must be Unrestricted, Disabled, or AllowList.");
    }

    if (
      options.Network.MaxRequests < 1
      || options.Network.MaxResponseBytes < 1
      || options.Network.MaxTotalResponseBytes < 1
    )
    {
      failures.Add("Network request and response byte limits must be positive.");
    }

    if (options.Network.AllowedOrigins.Any(origin => !AssetNetworkPolicy.IsValidOrigin(origin)))
    {
      failures.Add(
        "Network:AllowedOrigins must contain exact HTTP(S) origins without paths, credentials, queries, or wildcards."
      );
    }

    if (
      options.Network.Mode == ReportsEngineNetworkMode.AllowList
      && options.Network.AllowedOrigins.Count == 0
    )
    {
      failures.Add("Network:AllowedOrigins must not be empty in AllowList mode.");
    }

    if (
      browser.EnvironmentVariables.Any(variable =>
        string.IsNullOrEmpty(variable.Key)
        || variable.Key.Contains('=', StringComparison.Ordinal)
        || variable.Key.Contains('\0', StringComparison.Ordinal)
        || variable.Value.Contains('\0', StringComparison.Ordinal)
      )
    )
    {
      failures.Add(
        "Browser:EnvironmentVariables must have valid names and values without null characters."
      );
    }

    if (browser.MaxConversionsPerProcess < 0)
    {
      failures.Add("Browser:MaxConversionsPerProcess must be 0 (unlimited) or more.");
    }

    if (browser.ShutdownTimeout < TimeSpan.Zero)
    {
      failures.Add("Browser:ShutdownTimeout must not be negative.");
    }

    if (concurrency.MaxConcurrentConversions < 1)
    {
      failures.Add("Concurrency:MaxConcurrentConversions must be at least 1.");
    }

    if (concurrency.MaxQueueLength < 0)
    {
      failures.Add("Concurrency:MaxQueueLength must not be negative.");
    }

    if (
      concurrency.QueueTimeout < TimeSpan.Zero
      && concurrency.QueueTimeout != Timeout.InfiniteTimeSpan
    )
    {
      failures.Add(
        "Concurrency:QueueTimeout must not be negative (use -00:00:00.001 for infinite)."
      );
    }

    return failures.Count == 0
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(failures.Select(failure => $"ReportsEngine:{failure}"));
  }

  private static void RequirePositive(List<string> failures, string name, TimeSpan value)
  {
    if (value <= TimeSpan.Zero)
    {
      failures.Add($"{name} must be greater than zero.");
    }
  }

  private static void RequirePositiveOrInfinite(List<string> failures, string name, TimeSpan value)
  {
    if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
    {
      failures.Add($"{name} must be greater than zero, or infinite (-00:00:00.001).");
    }
  }
}
