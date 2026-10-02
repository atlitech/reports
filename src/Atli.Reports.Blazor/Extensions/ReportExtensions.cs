using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services;
using Atli.Reports.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Atli.Reports.Blazor.Extensions;

/// <summary>
///  Extension methods for <see cref="IApplicationBuilder" />.
/// </summary>
public static partial class ReportExtensions
{
  /// <summary>
  /// Registers a Blazor report with component type <typeparamref name="T" />.
  /// </summary>
  /// <param name="app"> The <see cref="IApplicationBuilder" /> to register the report with. </param>
  /// <param name="setupAction"> The <see cref="BlazorReportRegistrationOptions" /> to use. </param>
  /// <typeparam name="T"></typeparam>
  /// <returns> The <see cref="IApplicationBuilder" />. </returns>
  /// <exception cref="InvalidOperationException"></exception>
  public static IApplicationBuilder RegisterBlazorReport<T>(
    this IApplicationBuilder app,
    Action<BlazorReportRegistrationOptions>? setupAction = null
  )
    where T : ComponentBase
  {
    using var serviceScope = app.ApplicationServices.CreateScope();
    var options = GetReportRegistrationOptions(serviceScope, setupAction);
    var reportRegistry = serviceScope.ServiceProvider.GetRequiredService<BlazorReportRegistry>();

    reportRegistry.AddReport<T>(options);

    return app;
  }

  /// <summary>
  /// Registers a Blazor report with a component type <typeparamref name="T" />.  and registers a minimal api endpoint to generate the report.
  /// </summary>
  /// <param name="endpoints"> The <see cref="IEndpointRouteBuilder" /> to register the report with. </param>
  /// <param name="setupAction"> The <see cref="BlazorReportRegistrationOptions" /> to use. </param>
  /// <typeparam name="T"> The component type. </typeparam>
  /// <returns> The <see cref="RouteHandlerBuilder" />. </returns>
  /// <exception cref="InvalidOperationException"></exception>
  /// <remarks>
  /// The endpoint streams the report to the response. When generation fails before anything was written,
  /// it answers with a problem details response whose status code depends on <see cref="ConversionError.Kind"/>:
  /// 400 for <see cref="ConversionErrorKind.InvalidRequest"/>; 503 for <see cref="ConversionErrorKind.Busy"/>
  /// and <see cref="ConversionErrorKind.BrowserUnavailable"/>; 504 for <see cref="ConversionErrorKind.Timeout"/>
  /// and <see cref="ConversionErrorKind.SignalTimeout"/>; 499 for <see cref="ConversionErrorKind.Canceled"/>;
  /// and 500 otherwise. When it fails after part of the report was sent, the request fails with an
  /// exception and the response ends incomplete.
  /// </remarks>
  public static RouteHandlerBuilder MapBlazorReport<T>(
    this IEndpointRouteBuilder endpoints,
    Action<BlazorReportRegistrationOptions>? setupAction = null
  )
    where T : ComponentBase
  {
    using var serviceScope = endpoints.ServiceProvider.CreateScope();
    var options = GetReportRegistrationOptions(serviceScope, setupAction);

    var reportRegistry = serviceScope.ServiceProvider.GetRequiredService<BlazorReportRegistry>();
    var blazorReport = reportRegistry.AddReport<T>(options);

    return endpoints
      .MapPost(
        $"{blazorReport.NormalizedName}",
        (
          [FromServices] IReportService reportService,
          HttpContext context,
          CancellationToken token
        ) => WriteReportAsync(context, reportService, blazorReport, null, token)
      )
      .WithReportResponses(blazorReport);
  }

  /// <summary>
  /// Registers a Blazor report with a component type <typeparamref name="T" /> and data type <typeparamref name="TD" />.  and registers a minimal api endpoint to generate the report.
  /// </summary>
  /// <param name="endpoints"> The <see cref="IEndpointRouteBuilder" /> to register the report with. </param>
  /// <param name="setupAction"> The <see cref="BlazorReportRegistrationOptions" /> to use. </param>
  /// <typeparam name="T"> The component type. </typeparam>
  /// <typeparam name="TD"> The data type. </typeparam>
  /// <returns> The <see cref="RouteHandlerBuilder" />. </returns>
  /// <exception cref="InvalidOperationException"></exception>
  /// <remarks>
  /// The endpoint streams the report to the response. When generation fails before anything was written,
  /// it answers with a problem details response whose status code depends on <see cref="ConversionError.Kind"/>:
  /// 400 for <see cref="ConversionErrorKind.InvalidRequest"/>; 503 for <see cref="ConversionErrorKind.Busy"/>
  /// and <see cref="ConversionErrorKind.BrowserUnavailable"/>; 504 for <see cref="ConversionErrorKind.Timeout"/>
  /// and <see cref="ConversionErrorKind.SignalTimeout"/>; 499 for <see cref="ConversionErrorKind.Canceled"/>;
  /// and 500 otherwise. When it fails after part of the report was sent, the request fails with an
  /// exception and the response ends incomplete.
  /// </remarks>
  public static RouteHandlerBuilder MapBlazorReport<T, TD>(
    this IEndpointRouteBuilder endpoints,
    Action<BlazorReportRegistrationOptions>? setupAction = null
  )
    where T : ComponentBase
    where TD : class
  {
    using var serviceScope = endpoints.ServiceProvider.CreateScope();
    var options = GetReportRegistrationOptions(serviceScope, setupAction);

    var reportRegistry = serviceScope.ServiceProvider.GetRequiredService<BlazorReportRegistry>();
    var blazorReport = reportRegistry.AddReport<T>(options);

    return endpoints
      .MapPost(
        $"{blazorReport.NormalizedName}",
        (
          TD data,
          [FromServices] IReportService reportService,
          HttpContext context,
          CancellationToken token
        ) => WriteReportAsync(context, reportService, blazorReport, data, token)
      )
      .WithReportResponses(blazorReport);
  }

  /// <summary>
  /// Gets the HTTP status code that report endpoints answer with for a failed conversion.
  /// </summary>
  internal static int GetStatusCode(ConversionErrorKind kind)
  {
    return kind switch
    {
      ConversionErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
      ConversionErrorKind.Busy => StatusCodes.Status503ServiceUnavailable,
      ConversionErrorKind.BrowserUnavailable => StatusCodes.Status503ServiceUnavailable,
      ConversionErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
      ConversionErrorKind.SignalTimeout => StatusCodes.Status504GatewayTimeout,
      ConversionErrorKind.Canceled => StatusCodes.Status499ClientClosedRequest,
      _ => StatusCodes.Status500InternalServerError,
    };
  }

  private static string GetProblemTitle(ConversionErrorKind kind)
  {
    return kind switch
    {
      ConversionErrorKind.InvalidRequest => "The report could not be converted to PDF.",
      ConversionErrorKind.Busy => "The report engine is busy. Try again later.",
      ConversionErrorKind.BrowserUnavailable => "The browser that renders reports is unavailable.",
      ConversionErrorKind.Timeout => "The browser that renders reports did not respond in time.",
      ConversionErrorKind.SignalTimeout =>
        "The report did not signal that its JavaScript completed in time.",
      ConversionErrorKind.Canceled => "The request was canceled.",
      _ => "The report could not be rendered.",
    };
  }

  private static RouteHandlerBuilder WithReportResponses(
    this RouteHandlerBuilder builder,
    BlazorReport blazorReport
  )
  {
    // Stream makes OpenAPI describe the report as binary content, not as a JSON object.
    return builder
      .Produces<Stream>(StatusCodes.Status200OK, blazorReport.GetContentType())
      .ProducesProblem(StatusCodes.Status400BadRequest)
      .ProducesProblem(StatusCodes.Status500InternalServerError)
      .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
      .ProducesProblem(StatusCodes.Status504GatewayTimeout);
  }

  private static async Task<IResult> WriteReportAsync(
    HttpContext context,
    IReportService reportService,
    BlazorReport blazorReport,
    object? data,
    CancellationToken token
  )
  {
    var response = context.Response;
    response.ContentType = blazorReport.GetContentType();
    response.Headers.ContentDisposition =
      $"attachment; filename=\"{blazorReport.Name}.{blazorReport.GetFileExtension()}\"";

    var result = await reportService.GenerateReport(response.Body, blazorReport, data, token);
    if (!result.TryPickT1(out var error, out _))
    {
      return Results.Empty;
    }

    if (response.HasStarted)
    {
      // Part of the report is already on the wire with a 200 status. Failing the request makes the
      // server end the response without completing it, so the client sees a broken response instead
      // of a truncated report that looks complete.
      throw new InvalidOperationException(
        $"Report '{blazorReport.Name}' failed after part of it was sent: {error.Kind}: {error.Message}",
        error.Exception
      );
    }

    var logger = context
      .RequestServices.GetRequiredService<ILoggerFactory>()
      .CreateLogger(typeof(ReportExtensions).FullName!);
    LogReportFailed(logger, error.Exception, blazorReport.Name, error.Kind, error.Message);

    response.Headers.Remove(HeaderNames.ContentDisposition);
    response.ContentType = null;
    return Results.Problem(
      statusCode: GetStatusCode(error.Kind),
      title: GetProblemTitle(error.Kind)
    );
  }

  private static BlazorReportRegistrationOptions GetReportRegistrationOptions(
    IServiceScope serviceScope,
    Action<BlazorReportRegistrationOptions>? setupAction = null
  )
  {
    BlazorReportRegistrationOptions options = new();
    var globalOptions = serviceScope
      .ServiceProvider.GetRequiredService<IOptionsSnapshot<BlazorReportOptions>>()
      .Value;
    options.PdfOptions = globalOptions.PdfOptions.Clone();
    options.JavaScriptSettings = globalOptions.JavaScriptSettings.Clone();
    setupAction?.Invoke(options);
    return options;
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Report {ReportName} failed: {ErrorKind}: {ErrorMessage}"
  )]
  private static partial void LogReportFailed(
    ILogger logger,
    Exception? exception,
    string reportName,
    ConversionErrorKind errorKind,
    string errorMessage
  );
}
