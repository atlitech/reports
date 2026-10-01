using System.Text;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tests.Reports;
using Atli.Reports.Blazor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Blazor.Tests.Configuration;

/// <summary>
/// Base styles and assets are read and encoded once and reused for every render.
/// </summary>
/// <remarks>
/// Each test owns a temporary folder; the tests prove the files are not read again by changing them on
/// disk after registration.
/// </remarks>
public class AssetCachingTests
{
  [Test]
  public async Task Global_styles_and_assets_are_read_once_and_reused_for_every_render()
  {
    using var folder = new TemporaryFolder();
    var stylesPath = folder.Write("base.css", "h1 { color: rebeccapurple; }");
    var logoPath = folder.Write("assets/logo.png", [1, 2, 3]);
    await using var services = TestEngine.CreateServices(options =>
    {
      options.BaseStylesPath = stylesPath;
      options.AssetsPath = Path.GetDirectoryName(logoPath);
    });
    var report = services.GetRequiredService<BlazorReportRegistry>().AddReport<AssetReport>(Html());

    var first = await RenderHtmlAsync(services, report);
    await File.WriteAllTextAsync(stylesPath, "h1 { color: red; }");
    await File.WriteAllBytesAsync(logoPath, [9, 9, 9]);
    var second = await RenderHtmlAsync(services, report);

    await Assert.That(first).Contains("h1 { color: rebeccapurple; }");
    await Assert.That(first).Contains("data:image/png;base64,AQID");
    await Assert.That(second).IsEqualTo(first);
  }

  [Test]
  public async Task Report_styles_and_assets_are_read_once_per_path()
  {
    using var folder = new TemporaryFolder();
    var stylesPath = folder.Write("report.css", "p { margin: 0; }");
    var stampPath = folder.Write("stamps/stamp.png", [4, 5, 6]);
    await using var services = TestEngine.CreateServices();
    var registry = services.GetRequiredService<BlazorReportRegistry>();
    BlazorReportRegistrationOptions Options(string name) =>
      new()
      {
        ReportName = name,
        OutputFormat = ReportOutputFormat.Html,
        BaseStylesPath = stylesPath,
        AssetsPath = Path.GetDirectoryName(stampPath),
      };

    var first = registry.AddReport<ReportAssetReport>(Options("first"));
    File.Delete(stampPath);
    File.Delete(stylesPath);
    var second = registry.AddReport<ReportAssetReport>(Options("second"));
    var html = await RenderHtmlAsync(services, second);

    await Assert.That(second.BaseStyles).IsSameReferenceAs(first.BaseStyles);
    await Assert.That(second.Assets["stamp.png"]).IsSameReferenceAs(first.Assets["stamp.png"]);
    await Assert.That(second.Assets).IsNotSameReferenceAs(first.Assets);
    await Assert.That(html).Contains("p { margin: 0; }");
    await Assert.That(html).Contains("data:image/png;base64,BAUG");
  }

  private static BlazorReportRegistrationOptions Html() =>
    new() { OutputFormat = ReportOutputFormat.Html };

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
    private readonly string _root = Directory
      .CreateTempSubdirectory("atli-reports-tests-")
      .FullName;

    public string Write(string relativePath, string content) =>
      Write(relativePath, Encoding.UTF8.GetBytes(content));

    public string Write(string relativePath, byte[] content)
    {
      var path = Path.Combine(_root, relativePath);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, content);
      return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
  }
}
