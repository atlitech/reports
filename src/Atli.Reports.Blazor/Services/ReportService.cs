using System.IO.Pipelines;
using Atli.Reports.Blazor.Components;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices;
using Atli.Reports.Blazor.Services.BrowserServices.Problems;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Blazor.Services;

/// <summary>
/// Service for generating reports
/// </summary>
/// <remarks>
/// Creates a new instance of <see cref="ReportService"/>
/// </remarks>
/// <param name="serviceProvider"> The service provider </param>
/// <param name="reportRegistry"> The report registry </param>
/// <param name="browserService"></param>
public sealed class ReportService(
  IServiceProvider serviceProvider,
  BlazorReportRegistry reportRegistry,
  IBrowserService browserService
) : IReportService
{
  /// <summary>
  /// Generates a report using the specified component and data
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The component to use in the report </typeparam>
  /// <typeparam name="TD"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
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
    using var scope = serviceProvider.CreateScope();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

    var baseStyles = string.Empty;
    if (!string.IsNullOrEmpty(reportRegistry.BaseStyles))
    {
      baseStyles = reportRegistry.BaseStyles;
    }

    Dictionary<string, object?> componentParameters = new()
    {
      { "BaseStyles", baseStyles },
      { "Data", data },
      { "GlobalAssets", reportRegistry.GlobalAssets },
    };

    await using HtmlRenderer htmlRenderer = new(scope.ServiceProvider, loggerFactory);
    var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
    {
      ParameterView parameters = ParameterView.FromDictionary(componentParameters);
      var output = await htmlRenderer.RenderComponentAsync<T>(parameters);
      return output.ToHtmlString();
    });

    return await browserService.GenerateReport(
      pipeWriter,
      html,
      reportRegistry.DefaultPageSettings,
      cancellationToken
    );
  }

  /// <summary>
  /// Generates a report using the specified component and data
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="data"> The data to use in the report </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <typeparam name="T"> The type of data to use in the report </typeparam>
  /// <returns> The generated report </returns>
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
    using var scope = serviceProvider.CreateScope();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

    var baseStyles = string.Empty;
    if (!string.IsNullOrEmpty(blazorReport.BaseStyles))
    {
      baseStyles = blazorReport.BaseStyles;
    }
    else if (!string.IsNullOrEmpty(reportRegistry.BaseStyles))
    {
      baseStyles = reportRegistry.BaseStyles;
    }

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

    Dictionary<string, object?> baseComponentParameters = [];
    if (!string.IsNullOrEmpty(baseStyles))
    {
      baseComponentParameters.Add("BaseStyles", baseStyles);
    }

    baseComponentParameters.Add("ChildComponentType", blazorReport.Component);
    baseComponentParameters.Add("ChildComponentParameters", childComponentParameters);

    await using HtmlRenderer htmlRenderer = new(scope.ServiceProvider, loggerFactory);

    if (blazorReport.OutputFormat == ReportOutputFormat.Pdf)
    {
      var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
      {
        ParameterView parameters = ParameterView.FromDictionary(baseComponentParameters);
        var output = await htmlRenderer.RenderComponentAsync<BlazorReportsTemplate>(parameters);
        return output.ToHtmlString();
      });

      var pageSettings = blazorReport.PageSettings ?? reportRegistry.DefaultPageSettings;

      return await browserService.GenerateReport(pipeWriter, html, pageSettings, cancellationToken);
    }
    else
    {
      var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
      {
        ParameterView parameters = ParameterView.FromDictionary(baseComponentParameters);
        var output = await htmlRenderer.RenderComponentAsync<BlazorReportsTemplate>(parameters);
        return output.ToHtmlString();
      });

      await pipeWriter.WriteAsync(System.Text.Encoding.UTF8.GetBytes(html), cancellationToken);
      return new Success();
    }
  }

  /// <summary>
  /// Generates a report using the specified component
  /// </summary>
  /// <param name="pipeWriter"> The pipe writer to write the report to </param>
  /// <param name="blazorReport"> The report to generate </param>
  /// <param name="cancellationToken"> The cancellation token </param>
  /// <returns> The generated report </returns>
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
}
