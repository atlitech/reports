using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Services.BrowserServices;
using Atli.Reports.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Blazor.Tests.Configuration;

public class AddBlazorReportsTests
{
  [Test]
  public async Task Registers_the_engine_and_the_report_services_once()
  {
    ServiceCollection services = new();

    services.AddBlazorReports();
    services.AddBlazorReports();

    await using var provider = services.BuildServiceProvider(
      new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
    );
    await Assert
      .That(provider.GetRequiredService<IReportService>())
      .IsSameReferenceAs(provider.GetRequiredService<IReportService>());
    await Assert.That(provider.GetServices<IReportService>().Count()).IsEqualTo(1);
    await Assert.That(provider.GetRequiredService<IHtmlToPdfConverter>()).IsNotNull();
  }

  [Test]
  public async Task Browser_options_that_were_set_reach_the_engine()
  {
    ServiceCollection services = new();
    FileInfo executable = new(Path.Combine(Path.GetTempPath(), "custom-browser"));

    services.AddBlazorReports(options =>
    {
      options.BrowserOptions.Browser = Browsers.Edge;
      options.BrowserOptions.BrowserExecutableLocation = executable;
      options.BrowserOptions.NoSandbox = true;
      options.BrowserOptions.DisableDevShmUsage = true;
      options.BrowserOptions.DisableHeadless = true;
      options.BrowserOptions.ResponseTimeout = TimeSpan.FromSeconds(7);
    });

    await using var provider = services.BuildServiceProvider();
    var browser = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value.Browser;
    await Assert.That(browser.Kind).IsEqualTo(BrowserKind.Edge);
    await Assert.That(browser.ExecutablePath).IsEqualTo(executable.FullName);
    await Assert.That(browser.NoSandbox).IsTrue();
    await Assert.That(browser.DisableDevShmUsage).IsTrue();
    await Assert.That(browser.Headless).IsFalse();
    await Assert.That(browser.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
  }

  [Test]
  public async Task Browser_options_left_at_their_defaults_keep_the_engine_configuration()
  {
    ServiceCollection services = new();

    services.AddReportsEngine(options =>
    {
      options.Browser.Kind = BrowserKind.Edge;
      options.Browser.ExecutablePath = "/opt/browser";
      options.Browser.NoSandbox = true;
      options.Browser.Headless = false;
      options.Browser.CommandTimeout = TimeSpan.FromSeconds(5);
    });
    services.AddBlazorReports();

    await using var provider = services.BuildServiceProvider();
    var browser = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value.Browser;
    await Assert.That(browser.Kind).IsEqualTo(BrowserKind.Edge);
    await Assert.That(browser.ExecutablePath).IsEqualTo("/opt/browser");
    await Assert.That(browser.NoSandbox).IsTrue();
    await Assert.That(browser.Headless).IsFalse();
    await Assert.That(browser.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
  }
}
