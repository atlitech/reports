using System.Text;
using Atli.Reports.Blazor.Components;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Renders Blazor components to PDF or HTML reports.
/// </summary>
/// <remarks>
/// Creates a new instance of <see cref="ReportService"/>
/// </remarks>
/// <param name="serviceProvider"> The service provider used to render components </param>
/// <param name="reportRegistry"> The report registry </param>
/// <param name="converter"> The HTML-to-PDF converter </param>
public sealed class ReportService(
  IServiceProvider serviceProvider,
  BlazorReportRegistry reportRegistry,
  IHtmlToPdfConverter converter
) : IReportService
{
  internal const string CompletedSignalName = "atliReportCompleted";

  /// <inheritdoc />
  public async ValueTask<OneOf<Success, ConversionError>> GenerateReport<T>(
    Stream destination,
    BlazorReport blazorReport,
    T? data,
    CancellationToken cancellationToken = default
  )
    where T : class
  {
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentNullException.ThrowIfNull(blazorReport);

    using var activity = BlazorReportTelemetry.StartGenerate(
      blazorReport.Component,
      blazorReport.Name,
      blazorReport.OutputFormat
    );
    try
    {
      return BlazorReportTelemetry.Finished(
        activity,
        await GenerateCoreAsync(destination, blazorReport, data, cancellationToken)
      );
    }
    catch (Exception exception)
    {
      BlazorReportTelemetry.Failed(activity, exception);
      throw;
    }
  }

  private async ValueTask<OneOf<Success, ConversionError>> GenerateCoreAsync<T>(
    Stream destination,
    BlazorReport blazorReport,
    T? data,
    CancellationToken cancellationToken
  )
    where T : class
  {
    var javaScriptSettings =
      blazorReport.JavaScriptSettings ?? reportRegistry.DefaultJavaScriptSettings;
    var pdfOptions = (blazorReport.PdfOptions ?? reportRegistry.DefaultPdfOptions).Clone();
    if (javaScriptSettings.WaitForCompletedSignal)
    {
      pdfOptions.WaitForSignal = CompletedSignalName;
      pdfOptions.WaitTimeout = javaScriptSettings.CompletedSignalTimeout;
    }

    var html = await RenderAsync(
      typeof(BlazorReportTemplate),
      GetTemplateParameters(blazorReport, pdfOptions.WaitForSignal, data)
    );

    if (blazorReport.OutputFormat == ReportOutputFormat.Html)
    {
      try
      {
        await destination.WriteAsync(Encoding.UTF8.GetBytes(html), cancellationToken);
      }
      catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
      {
        return new ConversionError(
          ConversionErrorKind.Canceled,
          "The report generation was canceled.",
          exception
        );
      }

      return new Success();
    }

    return await converter.ConvertAsync(html, destination, pdfOptions, cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask<OneOf<Success, ConversionError>> GenerateReport(
    Stream destination,
    BlazorReport blazorReport,
    CancellationToken cancellationToken = default
  )
  {
    return GenerateReport<object>(destination, blazorReport, null, cancellationToken);
  }

  /// <summary>
  /// Gets a blazor report by name
  /// </summary>
  /// <param name="name"> The name of the report to get </param>
  /// <returns> The blazor report </returns>
  public BlazorReport? GetReportByName(string name)
  {
    var reportNormalizedName = name.ToLowerInvariant().Trim();
    var foundReport = reportRegistry.Reports.TryGetValue(reportNormalizedName, out var report);
    return foundReport ? report : null;
  }

  private Dictionary<string, object?> GetTemplateParameters(
    BlazorReport blazorReport,
    string? completedSignalName,
    object? data
  )
  {
    var baseStyles = !string.IsNullOrEmpty(blazorReport.BaseStyles)
      ? blazorReport.BaseStyles
      : reportRegistry.BaseStyles;

    Dictionary<string, object?> childComponentParameters = [];
    if (
      blazorReport.Component.BaseType == typeof(BlazorReportBase)
      && reportRegistry.GlobalAssets.Count != 0
    )
    {
      childComponentParameters.Add("GlobalAssets", reportRegistry.GlobalAssets);
    }

    if (blazorReport.Assets.Count != 0)
    {
      childComponentParameters.Add("ReportAssets", blazorReport.Assets);
    }

    if (data is not null)
    {
      childComponentParameters.Add("Data", data);
    }

    Dictionary<string, object?> templateParameters = [];
    if (!string.IsNullOrEmpty(baseStyles))
    {
      templateParameters.Add("BaseStyles", baseStyles);
    }

    if (completedSignalName is not null)
    {
      templateParameters.Add("CompletedSignalName", completedSignalName);
    }

    templateParameters.Add("ChildComponentType", blazorReport.Component);
    templateParameters.Add("ChildComponentParameters", childComponentParameters);
    return templateParameters;
  }

  private async Task<string> RenderAsync(
    Type componentType,
    IDictionary<string, object?> parameters
  )
  {
    using var activity = BlazorReportTelemetry.StartRender();
    try
    {
      await using var scope = serviceProvider.CreateAsyncScope();
      var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
      await using HtmlRenderer htmlRenderer = new(scope.ServiceProvider, loggerFactory);

      return await htmlRenderer.Dispatcher.InvokeAsync(async () =>
      {
        var output = await htmlRenderer.RenderComponentAsync(
          componentType,
          ParameterView.FromDictionary(parameters)
        );
        return output.ToHtmlString();
      });
    }
    catch (Exception exception)
    {
      BlazorReportTelemetry.Failed(activity, exception);
      throw;
    }
  }
}
