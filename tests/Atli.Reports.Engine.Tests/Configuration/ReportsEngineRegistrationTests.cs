using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Engine.Tests.Configuration;

public class ReportsEngineRegistrationTests
{
  [Test]
  public async Task Options_bind_from_a_configuration_section()
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(
        new Dictionary<string, string?>
        {
          ["ReportsEngine:Browser:Kind"] = "Edge",
          ["ReportsEngine:Browser:ExecutablePath"] = "/usr/bin/chromium",
          ["ReportsEngine:Browser:Headless"] = "false",
          ["ReportsEngine:Browser:NoSandbox"] = "true",
          ["ReportsEngine:Browser:DisableDevShmUsage"] = "true",
          ["ReportsEngine:Browser:ExtraArguments:0"] = "--lang=es",
          ["ReportsEngine:Browser:StartupTimeout"] = "00:01:00",
          ["ReportsEngine:Browser:CommandTimeout"] = "00:00:05",
        }
      )
      .Build();
    ServiceCollection services = new();

    services.AddReportsEngine(configuration.GetSection(ReportsEngineOptions.SectionName));

    await using var provider = services.BuildServiceProvider();
    var browser = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value.Browser;
    await Assert.That(browser.Kind).IsEqualTo(BrowserKind.Edge);
    await Assert.That(browser.ExecutablePath).IsEqualTo("/usr/bin/chromium");
    await Assert.That(browser.Headless).IsFalse();
    await Assert.That(browser.NoSandbox).IsTrue();
    await Assert.That(browser.DisableDevShmUsage).IsTrue();
    await Assert.That(browser.ExtraArguments).IsEquivalentTo(["--lang=es"]);
    await Assert.That(browser.StartupTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(browser.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task Configure_actions_apply_and_the_converter_is_a_singleton()
  {
    ServiceCollection services = new();

    services.AddReportsEngine(options => options.Browser.NoSandbox = true);
    services.AddReportsEngine(options => options.Browser.CommandTimeout = TimeSpan.FromSeconds(3));

    await using var provider = services.BuildServiceProvider(
      new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
    );
    var browser = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value.Browser;
    await Assert.That(browser.NoSandbox).IsTrue();
    await Assert.That(browser.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
    await Assert
      .That(provider.GetRequiredService<IHtmlToPdfConverter>())
      .IsSameReferenceAs(provider.GetRequiredService<IHtmlToPdfConverter>());
    await Assert
      .That(services.Count(descriptor => descriptor.ServiceType == typeof(IHtmlToPdfConverter)))
      .IsEqualTo(1);
  }
}
