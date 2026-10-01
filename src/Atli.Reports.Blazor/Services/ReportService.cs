using System.IO.Pipelines;
using System.Text;
using Atli.Reports.Blazor.Components;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
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
  /// <inheritdoc />
  public async ValueTask<OneOf<Success, ConversionError>> GenerateReport<T, TD>(
    Stream destination,
    TD data,
    CancellationToken cancellationToken = default
  )
    where T : ComponentBase
    where TD : class
  {
    ArgumentNullException.ThrowIfNull(destination);

    using var activity = BlazorReportsTelemetry.StartGenerate(
      typeof(T),
      reportName: null,
      ReportOutputFormat.Pdf
    );
    try
    {
      Dictionary<string, object?> componentParameters = new()
      {
        { "BaseStyles", reportRegistry.BaseStyles },
        { "Data", data },
        { "GlobalAssets", reportRegistry.GlobalAssets },
      };

      var html = await RenderAsync(typeof(T), componentParameters);
      var result = await converter.ConvertAsync(
        html,
        destination,
        PdfOptionsMapper.Map(reportRegistry.DefaultPageSettings),
        cancellationToken
      );
      return BlazorReportsTelemetry.Finished(activity, result);
    }
    catch (Exception exception)
    {
      BlazorReportsTelemetry.Failed(activity, exception);
      throw;
    }
  }

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

    using var activity = BlazorReportsTelemetry.StartGenerate(
      blazorReport.Component,
      blazorReport.Name,
      blazorReport.OutputFormat
    );
    try
    {
      return BlazorReportsTelemetry.Finished(
        activity,
        await GenerateCoreAsync(destination, blazorReport, data, cancellationToken)
      );
    }
    catch (Exception exception)
    {
      BlazorReportsTelemetry.Failed(activity, exception);
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
    var html = await RenderAsync(
      typeof(BlazorReportsTemplate),
      GetTemplateParameters(blazorReport, javaScriptSettings, data)
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

    var pageSettings = blazorReport.PageSettings ?? reportRegistry.DefaultPageSettings;
    return await converter.ConvertAsync(
      html,
      destination,
      PdfOptionsMapper.Map(pageSettings, javaScriptSettings),
      cancellationToken
    );
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

  /// <inheritdoc />
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
  public async ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport<T, TD>(
    PipeWriter pipeWriter,
    TD data,
    CancellationToken cancellationToken = default
  )
    where T : ComponentBase
    where TD : class
  {
    ArgumentNullException.ThrowIfNull(pipeWriter);

    var result = await GenerateReport<T, TD>(
      pipeWriter.AsStream(leaveOpen: true),
      data,
      cancellationToken
    );
    return await LegacyResults.ToLegacyResultAsync(pipeWriter, result, completeOnSuccess: true);
  }

  /// <inheritdoc />
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
  public async ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport<T>(
    PipeWriter pipeWriter,
    BlazorReport blazorReport,
    T? data,
    CancellationToken cancellationToken = default
  )
    where T : class
  {
    ArgumentNullException.ThrowIfNull(pipeWriter);
    ArgumentNullException.ThrowIfNull(blazorReport);

    var result = await GenerateReport(
      pipeWriter.AsStream(leaveOpen: true),
      blazorReport,
      data,
      cancellationToken
    );
    return await LegacyResults.ToLegacyResultAsync(
      pipeWriter,
      result,
      completeOnSuccess: blazorReport.OutputFormat == ReportOutputFormat.Pdf
    );
  }

  /// <inheritdoc />
  [Obsolete(ObsoleteMessages.PipeWriterOverloads)]
  public ValueTask<
    OneOf<Success, ServerBusyProblem, OperationCancelledProblem, BrowserProblem>
  > GenerateReport(
    PipeWriter pipeWriter,
    BlazorReport blazorReport,
    CancellationToken cancellationToken = default
  )
  {
    return GenerateReport<object>(pipeWriter, blazorReport, null, cancellationToken);
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
    BlazorReportsJavaScriptSettings javaScriptSettings,
    object? data
  )
  {
    var baseStyles = !string.IsNullOrEmpty(blazorReport.BaseStyles)
      ? blazorReport.BaseStyles
      : reportRegistry.BaseStyles;

    Dictionary<string, object?> childComponentParameters = [];
    if (
      blazorReport.Component.BaseType == typeof(BlazorReportsBase)
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

    if (javaScriptSettings.WaitForCompletedSignal)
    {
      templateParameters.Add("CompletedSignalName", PdfOptionsMapper.CompletedSignalName);
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
    using var activity = BlazorReportsTelemetry.StartRender();
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
      BlazorReportsTelemetry.Failed(activity, exception);
      throw;
    }
  }
}
