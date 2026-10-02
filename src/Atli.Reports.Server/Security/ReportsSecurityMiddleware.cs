using System.Diagnostics;
using Atli.Reports.Engine;
using Atli.Reports.Server.Endpoints;
using Microsoft.AspNetCore.Http.Features;

namespace Atli.Reports.Server.Security;

internal static partial class ReportsSecurityMiddleware
{
  // Endpoint metadata ensures authentication and admission apply before minimal-API JSON binding.
  internal sealed class ConversionAdmissionMetadata;

  internal static void UseReportsAudit(this WebApplication app)
  {
    app.Use(
      async (context, next) =>
      {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ConversionAdmissionMetadata>() is null)
        {
          await next(context);
          return;
        }

        var started = Stopwatch.GetTimestamp();
        int? failureStatus = null;
        try
        {
          await next(context);
        }
        catch (BadHttpRequestException exception)
        {
          failureStatus = exception.StatusCode;
          throw;
        }
        catch
        {
          failureStatus = StatusCodes.Status500InternalServerError;
          throw;
        }
        finally
        {
          var caller =
            context.User.FindFirst(ReportsSecurityRegistration.CallerClaim)?.Value ?? "anonymous";
          var status = context.RequestAborted.IsCancellationRequested
            ? ConversionProblems.Status499ClientClosedRequest
            : failureStatus ?? context.Response.StatusCode;
          // Deliberately exclude HTML, URLs, credentials, exception messages, and document metadata.
          if (app.Logger.IsEnabled(LogLevel.Information))
          {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LogRequest(app.Logger, caller, status, elapsedMilliseconds);
          }
        }
      }
    );
  }

  internal static void UseReportsAdmission(this WebApplication app)
  {
    var settings = app.Services.GetRequiredService<ReportsSecurityOptions>();
    var admission = app.Services.GetRequiredService<CallerAdmission>();
    var callerPolicies = settings.Callers.ToDictionary(
      policy => policy.CallerId,
      policy => policy.Limits.ApplyTo(settings.Limits),
      StringComparer.Ordinal
    );
    app.Use(
      async (context, next) =>
      {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ConversionAdmissionMetadata>() is null)
        {
          await next(context);
          return;
        }

        var caller =
          context.User.FindFirst(ReportsSecurityRegistration.CallerClaim)?.Value ?? "anonymous";
        var partition =
          context.User.FindFirst(ReportsSecurityRegistration.PartitionClaim)?.Value ?? "anonymous";
        var limits = callerPolicies.GetValueOrDefault(caller, settings.Limits);
        var result = admission.TryAcquire(
          partition,
          limits.MaxConcurrentRequestsPerCaller,
          out var lease
        );
        if (result != AdmissionResult.Accepted)
        {
          context.Response.Headers.RetryAfter = "1";
          await Results
            .Problem(
              statusCode: result == AdmissionResult.CallerBusy ? 429 : 503,
              title: "Conversion capacity is exhausted.",
              detail: "The in-flight request limit was reached. Retry later.",
              extensions: new Dictionary<string, object?>
              {
                ["kind"] = nameof(ConversionErrorKind.Busy),
              }
            )
            .ExecuteAsync(context);
          return;
        }

        using (lease)
        {
          // Respect any tighter Kestrel limit; never raise an operator's transport limit.
          var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
          var bodyLimit = Math.Min(
            limits.MaxRequestBodyBytes,
            feature?.MaxRequestBodySize ?? long.MaxValue
          );
          if (context.Request.ContentLength > bodyLimit)
          {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
          }
          if (feature is { IsReadOnly: false })
          {
            feature.MaxRequestBodySize = bodyLimit;
          }

          var originalCancellation = context.RequestAborted;
          using var deadline = new CancellationTokenSource(limits.RequestTimeout);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            originalCancellation,
            deadline.Token
          );
          context.RequestAborted = linked.Token;
          try
          {
            try
            {
              await next(context);
            }
            catch (Exception)
              when (deadline.IsCancellationRequested
                && !originalCancellation.IsCancellationRequested
              )
            {
              // The body reader and the engine can report cancellation differently. The deadline is
              // authoritative, and no underlying exception details belong in this response/audit.
            }

            if (deadline.IsCancellationRequested && !originalCancellation.IsCancellationRequested)
            {
              context.RequestAborted = originalCancellation;
              if (context.Response.HasStarted)
              {
                context.Abort();
              }
              else
              {
                context.Response.Clear();
                await ConversionProblems.WriteAsync(
                  context,
                  new ConversionError(
                    ConversionErrorKind.Timeout,
                    "The request exceeded the caller's time limit."
                  )
                );
              }
            }
          }
          finally
          {
            context.RequestAborted = originalCancellation;
          }
        }
      }
    );
  }

  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Information,
    Message = "Reports conversion caller {CallerId} completed with status {StatusCode} in {ElapsedMilliseconds} ms."
  )]
  private static partial void LogRequest(
    ILogger logger,
    string callerId,
    int statusCode,
    double elapsedMilliseconds
  );
}
