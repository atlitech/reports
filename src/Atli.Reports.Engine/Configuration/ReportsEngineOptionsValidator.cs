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
