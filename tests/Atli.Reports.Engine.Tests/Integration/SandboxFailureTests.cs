using Atli.Reports.Engine.Chromium;
using Atli.Reports.Engine.Chromium.Browser;
using Atli.Reports.Engine.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atli.Reports.Engine.Tests.Integration;

/// <summary>
/// How a browser that cannot create its sandbox is reported, over a fake browser that prints what
/// chrome-headless-shell 154 prints under Docker's default seccomp profile: the launch error, the
/// conversion error, and the browser health check all say what to do about it.
/// </summary>
public class SandboxFailureTests
{
  /// <summary>
  /// chrome-headless-shell 154's last words when the container denies it user namespaces.
  /// </summary>
  internal const string NoUsableSandbox =
    "[1002/231009.649502:FATAL:content/browser/zygote_host/zygote_host_impl_linux.cc:129] No usable sandbox! If you are running on Ubuntu 23.10+ or another Linux distro that has disabled unprivileged user namespaces with AppArmor, see https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md. Otherwise see https://chromium.googlesource.com/chromium/src/+/main/docs/linux/suid_sandbox_development.md for more information on developing with the (older) SUID sandbox. If you want to live dangerously and need an immediate workaround, you can try using --no-sandbox.";

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_launch_error_starts_with_the_remedy_and_keeps_the_browser_output()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create($"echo '{NoUsableSandbox}' >&2; exit 133");

    var error = await Assert
      .That(async () =>
        await BrowserProcess.LaunchAsync(
          new ReportsEngineBrowserOptions { ExecutablePath = fake.Path },
          NullLogger.Instance,
          TestToken
        )
      )
      .Throws<BrowserSandboxUnavailableException>();

    await Assert.That(error!.Message).StartsWith(BrowserSandboxUnavailableException.Remedy);
    await Assert
      .That(error.Detail)
      .StartsWith("The browser exited with code 133 before it reported its DevTools endpoint.")
      .And.Contains("No usable sandbox!");
    await Assert.That(error.Message).EndsWith(error.Detail);
  }

  [Test]
  public async Task Conversions_and_the_health_check_report_the_remedy_whole()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create($"echo '{NoUsableSandbox}' >&2; exit 133");
    ServiceCollection services = new();
    services.AddReportsEngine(options =>
    {
      TestEngine.ConfigureForTests(options);
      options.Browser.NoSandbox = false;
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.LaunchRetryDelay = TimeSpan.FromHours(1);
    });
    services.AddHealthChecks().AddReportsEngineBrowserCheck();
    await using var provider = services.BuildServiceProvider();

    var result = await provider
      .GetRequiredService<IHtmlToPdfConverter>()
      .ConvertAsync("<p>Never rendered</p>", cancellationToken: TestToken);

    await Assert.That(result.IsT1).IsTrue();
    await Assert.That(result.AsT1.Kind).IsEqualTo(ConversionErrorKind.BrowserUnavailable);
    await Assert.That(result.AsT1.Message).Contains(BrowserSandboxUnavailableException.Remedy);

    var status = provider.GetRequiredService<BrowserManager>().GetStatus();
    await Assert.That(status.SandboxUnavailable).IsTrue();
    await Assert
      .That(status.LaunchFailure)
      .DoesNotContain(BrowserSandboxUnavailableException.Remedy);

    // Chromium's own output makes the reason longer than the check quotes, so the check shortens
    // the browser's part; the remedy is the engine's own text and stays whole, once.
    var browser = (
      await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestToken)
    ).Entries[ReportsEngineHealthChecksBuilderExtensions.BrowserCheckName];
    await Assert.That(browser.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert
      .That(browser.Description)
      .StartsWith("The browser failed to start (1 failed launch(es) in a row)")
      .And.Contains(
        ". " + BrowserSandboxUnavailableException.Remedy + " The browser exited with code 133"
      )
      .And.Contains("No usable sandbox!");
    await Assert
      .That(browser.Description!.Split(BrowserSandboxUnavailableException.Remedy).Length)
      .IsEqualTo(2);
  }

  [Test]
  public async Task Another_launch_failure_replaces_the_sandbox_failure()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Uses a shell script as the browser executable.");
      return;
    }

    using var fake = FakeBrowser.Create($"echo '{NoUsableSandbox}' >&2; exit 133");
    await using var provider = TestEngine.Create(options =>
    {
      options.Browser.ExecutablePath = fake.Path;
      options.Browser.LaunchRetryDelay = TimeSpan.FromHours(1);
    });
    var browsers = provider.GetRequiredService<BrowserManager>();
    var converter = provider.GetRequiredService<IHtmlToPdfConverter>();

    var failed = await converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);
    await Assert.That(failed.IsT1).IsTrue();
    await Assert.That(browsers.GetStatus().SandboxUnavailable).IsTrue();

    fake.Replace("echo 'missing library' >&2; exit 127");
    var other = await converter.ConvertAsync("<p>x</p>", cancellationToken: TestToken);
    await Assert.That(other.IsT1).IsTrue();
    var status = browsers.GetStatus();
    await Assert.That(status.SandboxUnavailable).IsFalse();
    await Assert.That(status.LaunchFailure).Contains("exited with code 127");
    await Assert.That(other.AsT1.Message).DoesNotContain(BrowserSandboxUnavailableException.Remedy);
  }
}
