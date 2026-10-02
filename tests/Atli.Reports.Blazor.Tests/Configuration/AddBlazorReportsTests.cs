using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Services;
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
  [Arguments(true)]
  [Arguments(false)]
  public async Task Engine_configuration_is_preserved_in_either_registration_order(bool engineFirst)
  {
    ServiceCollection services = new();
    if (!engineFirst)
    {
      services.AddBlazorReports();
    }

    services.AddReportsEngine(options =>
    {
      options.Browser.Kind = BrowserKind.Edge;
      options.Browser.ExecutablePath = "/opt/browser";
      options.Browser.NoSandbox = true;
      options.Browser.DisableDevShmUsage = true;
      options.Browser.Headless = false;
      options.Browser.CommandTimeout = TimeSpan.FromSeconds(5);
      options.Concurrency.MaxConcurrentConversions = 3;
    });
    services.AddBlazorReports();

    await using var provider = services.BuildServiceProvider();
    var engine = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value;
    await Assert.That(engine.Browser.Kind).IsEqualTo(BrowserKind.Edge);
    await Assert.That(engine.Browser.ExecutablePath).IsEqualTo("/opt/browser");
    await Assert.That(engine.Browser.NoSandbox).IsTrue();
    await Assert.That(engine.Browser.DisableDevShmUsage).IsTrue();
    await Assert.That(engine.Browser.Headless).IsFalse();
    await Assert.That(engine.Browser.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(engine.Concurrency.MaxConcurrentConversions).IsEqualTo(3);
    await Assert.That(provider.GetServices<IHtmlToPdfConverter>().Count()).IsEqualTo(1);
  }
}
