using System.Text;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Configuration;

public class StylesheetReloadTests
{
  [Test]
  public async Task Report_styles_reload_without_changing_cached_reports_that_share_the_file()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("report.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var cached = registry.AddReport<StaticReport>(Html("cached", path));
    var reloading = registry.AddReport<StaticReport>(Html("reloading", path, reload: true));

    var before = await RenderHtmlAsync(services, reloading);
    File.WriteAllText(path, ".updated { color: green; }");
    var after = await RenderHtmlAsync(services, reloading);
    var cachedHtml = await RenderHtmlAsync(services, cached);

    await Assert.That(before).Contains(".original { color: purple; }");
    await Assert.That(after).Contains(".updated { color: green; }");
    await Assert.That(after).DoesNotContain(".original { color: purple; }");
    await Assert.That(cachedHtml).Contains(".original { color: purple; }");
    await Assert.That(cachedHtml).DoesNotContain(".updated { color: green; }");
  }

  [Test]
  public async Task Reloading_registration_reads_current_styles_even_when_the_path_is_cached()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("report.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    registry.AddReport<StaticReport>(Html("cached", path));

    File.WriteAllText(path, ".updated { color: green; }");
    var reloading = registry.AddReport<StaticReport>(Html("reloading", path, reload: true));

    await Assert.That(reloading.BaseStyles).IsEqualTo(".updated { color: green; }");
  }

  [Test]
  public async Task Global_styles_reload_for_reports_using_the_global_stylesheet()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("global.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices(options =>
    {
      options.BaseStylesPath = path;
      options.BaseStylesReloadOnChange = true;
    });
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var report = registry.AddReport<StaticReport>(Html("global"));

    var before = await RenderHtmlAsync(services, report);
    File.WriteAllText(path, ".updated { color: green; }");
    var after = await RenderHtmlAsync(services, report);

    await Assert.That(before).Contains(".original { color: purple; }");
    await Assert.That(after).Contains(".updated { color: green; }");
    await Assert.That(after).DoesNotContain(".original { color: purple; }");
  }

  [Test]
  public async Task Report_styles_override_global_reload_without_reading_the_global_file()
  {
    using var folder = new TemporaryFolder();
    var globalPath = folder.Write("global.css", ".global { color: purple; }");
    var reportPath = folder.Write("report.css", ".report { color: green; }");
    await using var services = TestEngine.CreateServices(options =>
    {
      options.BaseStylesPath = globalPath;
      options.BaseStylesReloadOnChange = true;
    });
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var report = registry.AddReport<StaticReport>(Html("report", reportPath));
    File.Delete(globalPath);

    var html = await RenderHtmlAsync(services, report);

    await Assert.That(html).Contains(".report { color: green; }");
    await Assert.That(html).DoesNotContain(".global { color: purple; }");
  }

  [Test]
  public async Task Assigning_report_styles_disables_the_file_provider()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("report.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices();
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<StaticReport>(Html("report", path, reload: true));

    report.BaseStyles = ".manual { color: blue; }";
    File.Delete(path);
    var html = await RenderHtmlAsync(services, report);

    await Assert.That(html).Contains(".manual { color: blue; }");
    await Assert.That(html).DoesNotContain(".original { color: purple; }");
  }

  [Test]
  public async Task Assigning_global_styles_disables_the_file_provider()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("global.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices(options =>
    {
      options.BaseStylesPath = path;
      options.BaseStylesReloadOnChange = true;
    });
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    var report = registry.AddReport<StaticReport>(Html("global"));

    registry.BaseStyles = ".manual { color: blue; }";
    File.Delete(path);
    var html = await RenderHtmlAsync(services, report);

    await Assert.That(html).Contains(".manual { color: blue; }");
    await Assert.That(html).DoesNotContain(".original { color: purple; }");
  }

  [Test]
  public async Task Missing_styles_fail_registration_with_the_path_and_build_guidance()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("report.css", "");
    File.Delete(path);
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();

    var exception = await Assert
      .That(() => registry.AddReport<StaticReport>(Html("missing", path)))
      .Throws<FileNotFoundException>();

    await Assert.That(exception!.FileName).IsEqualTo(path);
    await Assert.That(exception.Message).Contains("compiled");
    await Assert.That(exception.Message).Contains("build or publish output");
  }

  [Test]
  public async Task Removing_a_reloading_stylesheet_fails_instead_of_serving_stale_styles()
  {
    using var folder = new TemporaryFolder();
    var path = folder.Write("report.css", ".original { color: purple; }");
    await using var services = TestEngine.CreateServices();
    var report = services
      .GetRequiredService<BlazorReportRegistry>()
      .AddReport<StaticReport>(Html("reloading", path, reload: true));
    File.Delete(path);

    var exception = await Assert
      .That(async () => await RenderHtmlAsync(services, report))
      .Throws<FileNotFoundException>();

    await Assert.That(exception!.FileName).IsEqualTo(path);
  }

  private static BlazorReportRegistrationOptions Html(
    string name,
    string? path = null,
    bool reload = false
  ) =>
    new()
    {
      ReportName = name,
      OutputFormat = ReportOutputFormat.Html,
      BaseStylesPath = path,
      BaseStylesReloadOnChange = reload,
    };

  private static async Task<string> RenderHtmlAsync(IServiceProvider services, BlazorReport report)
  {
    using MemoryStream destination = new();
    var result = await services
      .GetRequiredService<IReportService>()
      .GenerateReport(destination, report, TestContext.Current!.Execution.CancellationToken);
    await Assert.That(result.IsT0).IsTrue();
    return Encoding.UTF8.GetString(destination.ToArray());
  }

  private sealed class TemporaryFolder : IDisposable
  {
    private readonly string _root = Directory.CreateTempSubdirectory("atli-styles-tests-").FullName;

    public string Write(string name, string content)
    {
      var path = Path.Combine(_root, name);
      File.WriteAllText(path, content);
      return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
  }
}
