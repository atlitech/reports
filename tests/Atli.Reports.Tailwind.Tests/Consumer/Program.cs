using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tailwind;
using Atli.Reports.Engine;
using Consumer.Reports;
using Microsoft.Extensions.DependencyInjection;

var outputDirectory = args[0];
var pdf = args.Contains("--pdf", StringComparer.Ordinal);
Directory.CreateDirectory(outputDirectory);

ServiceCollection services = new();
services.AddBlazorReports();
services.AddReportsEngine(options =>
{
  if (!pdf)
  {
    options.Browser.ExecutablePath = Path.Combine(outputDirectory, "no-browser-installed");
  }

  options.Browser.NoSandbox = true;
  options.Browser.DisableDevShmUsage = true;
  options.Browser.StartupTimeout = TimeSpan.FromSeconds(60);
});
await using var provider = services.BuildServiceProvider();
var registry = provider.GetRequiredService<BlazorReportRegistry>();
var invoice = registry.AddReport<Invoice>(options =>
{
  options.OutputFormat = pdf ? ReportOutputFormat.Pdf : ReportOutputFormat.Html;
  options.PdfOptions.WaitForSignal = pdf ? "tailwindReportReady" : null;
  options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(10);
  options.UseTailwind("Reports/Invoice");
});
var receipt = registry.AddReport<Receipt>(options =>
{
  options.OutputFormat = ReportOutputFormat.Html;
  options.UseTailwind("Reports/Receipt");
});

foreach (var report in new[] { invoice, receipt })
{
  var extension = report.OutputFormat == ReportOutputFormat.Pdf ? "pdf" : "html";
  await using var output = File.Create(Path.Combine(outputDirectory, $"{report.Name}.{extension}"));
  var result = await provider
    .GetRequiredService<IReportService>()
    .GenerateReport(output, report, CancellationToken.None);
  if (result.IsT1)
  {
    throw new InvalidOperationException($"{result.AsT1.Kind}: {result.AsT1.Message}");
  }
}
