using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Atli.Reports.Client;

/// <summary>
/// Configures the client for an Atli Reports server.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReportsClientExtensions.AddReportsClient(Microsoft.Extensions.Hosting.IHostApplicationBuilder, string, Action{ReportsClientSettings}?)"/>
/// fills these settings in three steps, each overriding the one before: the
/// <see cref="DefaultConfigSectionName"/> configuration section, the connection string, and the
/// <c>configureSettings</c> callback.
/// </para>
/// <para>
/// The default timeouts suit the server's own defaults: it lets a conversion wait up to 30 seconds
/// for a turn and gives the whole conversion up to 60 seconds before it answers
/// <c>504 Gateway Timeout</c>. Raise <see cref="AttemptTimeout"/> and <see cref="TotalTimeout"/>
/// when the server allows longer conversions, so the server's clean error arrives before the
/// client gives up.
/// </para>
/// </remarks>
public sealed class ReportsClientSettings
{
  /// <summary>
  /// The configuration section the settings bind from: <c>ReportsClient</c>.
  /// </summary>
  public const string DefaultConfigSectionName = "ReportsClient";

  /// <summary>
  /// The base address of the reports server, for example <c>http://reports:8080</c>. Required.
  /// </summary>
  /// <remarks>
  /// The client posts to <c>convert</c> and probes <c>health/ready</c> relative to this address, so a
  /// server published under a path prefix (<c>https://gateway/reports</c>) works too.
  /// </remarks>
  public Uri? Endpoint { get; set; }

  /// <summary>
  /// A full API credential (<c>key-id.secret</c>), sent only to conversion requests in the
  /// <c>X-Reports-Api-Key</c> header. Configure through a secret store, not source control.
  /// Cannot be combined with <see cref="AccessTokenProvider"/>.
  /// </summary>
  public string? ApiKey { get; set; }

  /// <summary>
  /// Acquires a bearer access token for each conversion attempt. The application owns token
  /// caching, expiry, and refresh; return the token without the <c>Bearer</c> prefix.
  /// The callback runs within the attempt timeout and receives its cancellation token.
  /// Set this in code, for example using a workload identity credential. Authentication
  /// failures (<c>401</c>, <c>403</c>) are not retried. Health probes do not acquire credentials.
  /// </summary>
  public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; set; }

  /// <summary>
  /// Whether to skip registering the health check that probes the server's <c>/health/ready</c>.
  /// Defaults to <see langword="false"/>.
  /// </summary>
  public bool DisableHealthChecks { get; set; }

  /// <summary>
  /// How long the health check waits for the server's answer before it reports unhealthy. Defaults
  /// to 5 seconds.
  /// </summary>
  public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// How long one attempt may wait for the server to start answering. Defaults to 2 minutes.
  /// </summary>
  /// <remarks>
  /// The server answers once the PDF starts to stream (or with an error), so this bounds its queue
  /// wait plus rendering, not the PDF transfer: by default the server answers within 30 + 60 = 90
  /// seconds. An attempt that runs out of time fails with
  /// <see cref="Engine.ConversionErrorKind.Timeout"/> and is not retried. Must be between 10
  /// milliseconds and 24 hours.
  /// </remarks>
  public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);

  /// <summary>
  /// How long a conversion may wait for the server to start answering, across all its attempts and
  /// the delays between them. Defaults to 5 minutes.
  /// </summary>
  /// <remarks>
  /// A conversion that runs out of time fails with <see cref="Engine.ConversionErrorKind.Timeout"/>.
  /// Must be between 10 milliseconds and 24 hours.
  /// </remarks>
  public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// How many times to retry a conversion the server could not take (<c>503 Service Unavailable</c>,
  /// after the server's <c>Retry-After</c> delay) or that failed to reach the server (a connection or
  /// transport error, after an exponential back-off from 1 second). Defaults to 3. <c>0</c> never
  /// retries.
  /// </summary>
  /// <remarks>
  /// Every other answer (<c>4xx</c>, <c>500</c>, <c>504</c>) is final: the same document would fail the
  /// same way again.
  /// </remarks>
  public int MaxRetryAttempts { get; set; } = 3;

  /// <summary>
  /// Reads <see cref="Endpoint"/> and an optional <see cref="ApiKey"/> from a connection string:
  /// <c>Endpoint=&lt;url&gt;;ApiKey=&lt;credential&gt;</c>, as
  /// the Aspire hosting integration writes it, or a bare absolute URL.
  /// </summary>
  /// <exception cref="ArgumentException">The connection string has neither form.</exception>
  internal void ParseConnectionString(string connectionString)
  {
    if (TryCreateEndpoint(connectionString, out var endpoint))
    {
      Endpoint = endpoint;
      return;
    }

    DbConnectionStringBuilder builder = new();
    try
    {
      builder.ConnectionString = connectionString;
    }
    catch (ArgumentException)
    {
      // Parser errors can include the connection string, which may contain a credential.
      throw new ArgumentException(InvalidConnectionString, nameof(connectionString));
    }

    if (
      builder.TryGetValue("Endpoint", out var value)
      && TryCreateEndpoint(value as string, out endpoint)
    )
    {
      Endpoint = endpoint;
      if (builder.TryGetValue("ApiKey", out var apiKey))
      {
        ApiKey = apiKey as string;
      }
      return;
    }

    throw new ArgumentException(InvalidConnectionString, nameof(connectionString));
  }

  private const string InvalidConnectionString =
    "The reports server connection string must be 'Endpoint=<url>' or an absolute http or https URL.";

  /// <summary>
  /// Whether <paramref name="value"/> is an absolute http or https URL.
  /// </summary>
  internal static bool TryCreateEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint)
  {
    if (
      Uri.TryCreate(value?.Trim(), UriKind.Absolute, out endpoint)
      && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
      && string.IsNullOrEmpty(endpoint.UserInfo)
      && string.IsNullOrEmpty(endpoint.Query)
      && string.IsNullOrEmpty(endpoint.Fragment)
    )
    {
      return true;
    }

    endpoint = null;
    return false;
  }
}
