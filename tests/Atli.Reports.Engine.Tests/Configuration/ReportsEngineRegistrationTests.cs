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
          ["ReportsEngine:Browser:WarmUpOnStartup"] = "true",
          ["ReportsEngine:Browser:MaxConversionsPerProcess"] = "250",
          ["ReportsEngine:Browser:MaxProcessLifetime"] = "00:20:00",
          ["ReportsEngine:Browser:IdleTimeout"] = "00:05:00",
          ["ReportsEngine:Browser:ShutdownTimeout"] = "00:00:03",
          ["ReportsEngine:Concurrency:MaxConcurrentConversions"] = "6",
          ["ReportsEngine:Concurrency:MaxQueueLength"] = "12",
          ["ReportsEngine:Concurrency:QueueTimeout"] = "00:00:09",
          ["ReportsEngine:ConversionTimeout"] = "00:02:00",
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
    await Assert.That(browser.WarmUpOnStartup).IsTrue();
    await Assert.That(browser.MaxConversionsPerProcess).IsEqualTo(250);
    await Assert.That(browser.MaxProcessLifetime).IsEqualTo(TimeSpan.FromMinutes(20));
    await Assert.That(browser.IdleTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(browser.ShutdownTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
    var options = provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value;
    await Assert.That(options.Concurrency.MaxConcurrentConversions).IsEqualTo(6);
    await Assert.That(options.Concurrency.MaxQueueLength).IsEqualTo(12);
    await Assert.That(options.Concurrency.QueueTimeout).IsEqualTo(TimeSpan.FromSeconds(9));
    await Assert.That(options.ConversionTimeout).IsEqualTo(TimeSpan.FromMinutes(2));
  }

  [Test]
  public async Task Defaults_are_valid_and_bounded()
  {
    ReportsEngineOptions options = new();

    await Assert.That(options.Concurrency.MaxConcurrentConversions).IsBetween(2, 8);
    await Assert.That(options.Concurrency.MaxQueueLength).IsEqualTo(100);
    await Assert.That(options.Concurrency.QueueTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.ConversionTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
    await Assert.That(options.Browser.MaxConversionsPerProcess).IsEqualTo(1000);
    await Assert.That(options.Browser.MaxProcessLifetime).IsEqualTo(TimeSpan.FromHours(1));
    await Assert.That(options.Browser.IdleTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
    await Assert.That(options.Browser.WarmUpOnStartup).IsFalse();
  }

  [Test]
  public async Task Invalid_options_are_rejected_when_the_engine_is_resolved()
  {
    ServiceCollection services = new();
    services.AddReportsEngine(options =>
    {
      options.Concurrency.MaxConcurrentConversions = 0;
      options.Concurrency.MaxQueueLength = -1;
      options.Browser.CommandTimeout = TimeSpan.Zero;
      options.ConversionTimeout = TimeSpan.FromSeconds(-5);
    });
    await using var provider = services.BuildServiceProvider();

    var exception = await Assert
      .That(() => provider.GetRequiredService<IHtmlToPdfConverter>())
      .Throws<OptionsValidationException>();

    await Assert.That(exception!.Failures.Count()).IsEqualTo(4);
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
