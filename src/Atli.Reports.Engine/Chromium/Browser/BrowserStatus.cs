namespace Atli.Reports.Engine.Chromium.Browser;

/// <summary>
/// A snapshot of the engine's browser, taken by <see cref="BrowserManager.GetStatus"/>.
/// </summary>
/// <param name="ShuttingDown">Whether the engine has started shutting down.</param>
/// <param name="Running">The browser new conversions go to, if one is running.</param>
/// <param name="LaunchFailure">
/// Why the most recent launch failed, or <see langword="null"/> when it succeeded or none was
/// attempted. Cleared by the next successful launch.
/// </param>
/// <param name="SandboxUnavailable">
/// Whether the most recent launch failed because Chromium could not create its sandbox
/// (<see cref="BrowserSandboxUnavailableException"/>). <paramref name="LaunchFailure"/> then holds
/// the exception's <see cref="BrowserSandboxUnavailableException.Detail"/>, without the remedy.
/// </param>
/// <param name="FailedLaunches">How many launches in a row have failed.</param>
/// <param name="Retrying">Whether the engine is retrying a failed launch in the background.</param>
internal sealed record BrowserStatus(
  bool ShuttingDown,
  BrowserInstance? Running,
  string? LaunchFailure,
  bool SandboxUnavailable,
  int FailedLaunches,
  bool Retrying
);
