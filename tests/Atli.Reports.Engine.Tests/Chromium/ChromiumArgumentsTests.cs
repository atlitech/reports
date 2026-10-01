using Atli.Reports.Engine.Chromium.Browser;

namespace Atli.Reports.Engine.Tests.Chromium;

public class ChromiumArgumentsTests
{
  [Test]
  public async Task Defaults_run_headless_with_the_sandbox_and_shared_memory()
  {
    var arguments = ChromiumArguments.Build(new ReportsEngineBrowserOptions(), "/tmp/profile");

    await Assert.That(arguments).Contains("--headless");
    await Assert.That(arguments).Contains("--remote-debugging-port=0");
    await Assert.That(arguments).Contains("--user-data-dir=/tmp/profile");
    await Assert.That(arguments).DoesNotContain("--no-sandbox");
    await Assert.That(arguments).DoesNotContain("--disable-dev-shm-usage");
  }

  [Test]
  public async Task Paths_with_spaces_stay_one_argument()
  {
    // Arguments go through ProcessStartInfo.ArgumentList, so they need no quoting of their own.
    var arguments = ChromiumArguments.Build(new ReportsEngineBrowserOptions(), "/tmp/my profile");

    await Assert.That(arguments).Contains("--user-data-dir=/tmp/my profile");
  }

  [Test]
  public async Task Browser_options_map_to_their_switches_and_extra_arguments_come_last()
  {
    ReportsEngineBrowserOptions options = new()
    {
      Headless = false,
      NoSandbox = true,
      DisableDevShmUsage = true,
    };
    options.ExtraArguments.Add("--lang=es");

    var arguments = ChromiumArguments.Build(options, "/tmp/profile");

    await Assert.That(arguments).DoesNotContain("--headless");
    await Assert.That(arguments).Contains("--no-sandbox");
    await Assert.That(arguments).Contains("--disable-dev-shm-usage");
    await Assert.That(arguments[^1]).IsEqualTo("--lang=es");
  }
}
