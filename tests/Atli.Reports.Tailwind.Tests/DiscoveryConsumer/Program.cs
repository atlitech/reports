using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Blazor.Tailwind;
using Atli.Reports.Engine;
using DiscoveryConsumer.Dynamic;
using DiscoveryConsumer.Reports;
using Microsoft.Extensions.DependencyInjection;

var outputDirectory = args[0];
var pdf = args.Contains("--pdf", StringComparer.Ordinal);
var dynamicReport = args.Contains("--dynamic", StringComparer.Ordinal);
AppContext.SetData(
  "stamp",
  args.Contains("--blue", StringComparer.Ordinal) ? typeof(BlueStamp) : typeof(RedStamp)
);
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
var invoice = registry.AddReport<Invoice>(Options("Reports/Invoice", pdf));
var receipt = registry.AddReport<Receipt>(Options("Reports/Receipt", pdf: false));
List<BlazorReport> reports = [invoice, receipt];
if (dynamicReport)
{
  reports.Add(registry.AddReport<DynamicReport>(Options("Reports/DynamicReport", pdf)));
}

foreach (var report in reports)
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

static Action<BlazorReportRegistrationOptions> Options(string bundle, bool pdf) =>
  options =>
  {
    options.OutputFormat = pdf ? ReportOutputFormat.Pdf : ReportOutputFormat.Html;
    options.PdfOptions.WaitForSignal = pdf ? "tailwindReportReady" : null;
    options.PdfOptions.WaitTimeout = TimeSpan.FromSeconds(10);
    options.UseTailwind(bundle);
  };
