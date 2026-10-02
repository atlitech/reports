using System.Net;
using Atli.Reports.Blazor.Extensions;
using Atli.Reports.Client.Tests.Support;
using Atli.Reports.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Client.Tests.Configuration;

/// <summary>
/// Reading the settings from configuration, and what the registration puts in the container.
/// </summary>
public class AddReportsClientTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments("Endpoint=http://reports:8080", "http://reports:8080/")]
  [Arguments("endpoint=https://reports.example.com/base", "https://reports.example.com/base/")]
  [Arguments("Endpoint=\"http://reports:8080\";Other=ignored", "http://reports:8080/")]
  [Arguments("http://localhost:5123", "http://localhost:5123/")]
  [Arguments("  https://gateway/reports/  ", "https://gateway/reports/")]
  public async Task The_connection_string_is_an_endpoint_pair_or_a_bare_url(
    string connectionString,
    string expected
  )
  {
    using var host = Build(new() { ["ConnectionStrings:reports"] = connectionString });

    await Assert.That(BaseAddress(host)).IsEqualTo(new Uri(expected));
  }

  [Test]
  [Arguments("Endpoint=ftp://reports")]
  [Arguments("Endpoint=/relative")]
  [Arguments("Host=reports;Port=8080")]
  [Arguments("not a url")]
  [Arguments("https://caller:secret@reports.example.com")]
  [Arguments("https://reports.example.com?api-key=secret")]
  [Arguments("https://reports.example.com#secret")]
  public async Task An_unusable_connection_string_fails_with_its_name(string connectionString)
  {
    var exception = await Assert
      .That(() => Build(new() { ["ConnectionStrings:reports"] = connectionString }))
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("ConnectionStrings:reports");
  }

  [Test]
  public async Task A_missing_endpoint_says_where_to_set_one()
  {
    var exception = await Assert.That(() => Build(new())).Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("ConnectionStrings:reports");
    await Assert.That(exception.Message).Contains("ReportsClient:Endpoint");
  }

  [Test]
  public async Task Api_keys_bind_from_secret_configuration_and_connection_strings_override_them()
  {
    ReportsClientSettings? seen = null;
    using var host = Build(
      new()
      {
        ["ReportsClient:ApiKey"] = "section.secret",
        ["ConnectionStrings:reports"] =
          "Endpoint=https://reports.example.com;ApiKey=connection.secret",
      },
      settings => seen = settings
    );

    await Assert.That(seen!.ApiKey).IsEqualTo("connection.secret");
  }

  [Test]
  public async Task Malformed_secret_connection_strings_are_not_exposed_by_exceptions()
  {
    var exception = await Assert
      .That(() =>
        Build(
          new()
          {
            ["ConnectionStrings:reports"] =
              "Endpoint=https://reports.example.com;ApiKey=secret;\"sensitive-secret",
          }
        )
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.ToString()).DoesNotContain("sensitive-secret");
  }

  [Test]
  public async Task Configuring_both_authentication_methods_is_rejected()
  {
    ServiceCollection services = new();
    await Assert
      .That(() =>
        services.AddReportsClient(
          new ReportsClientSettings
          {
            Endpoint = StubServer.Endpoint,
            ApiKey = "caller.secret",
            AccessTokenProvider = _ => ValueTask.FromResult("token"),
          }
        )
      )
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task The_section_supplies_settings_and_the_connection_string_overrides_its_endpoint()
  {
    ReportsClientSettings? seen = null;
    using var host = Build(
      new()
      {
        ["ReportsClient:Endpoint"] = "http://from-section:1",
        ["ReportsClient:AttemptTimeout"] = "00:00:42",
        ["ReportsClient:TotalTimeout"] = "00:03:00",
        ["ReportsClient:MaxRetryAttempts"] = "1",
        ["ReportsClient:DisableHealthChecks"] = "true",
        ["ReportsClient:HealthCheckTimeout"] = "00:00:02",
        ["ConnectionStrings:reports"] = "Endpoint=http://from-connection-string:2",
      },
      settings => seen = Copy(settings)
    );

    await Assert.That(seen!.Endpoint).IsEqualTo(new Uri("http://from-connection-string:2"));
    await Assert.That(seen.AttemptTimeout).IsEqualTo(TimeSpan.FromSeconds(42));
    await Assert.That(seen.TotalTimeout).IsEqualTo(TimeSpan.FromMinutes(3));
    await Assert.That(seen.MaxRetryAttempts).IsEqualTo(1);
    await Assert.That(seen.DisableHealthChecks).IsTrue();
    await Assert.That(seen.HealthCheckTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task The_section_alone_is_enough_and_the_callback_has_the_last_word()
  {
    using var host = Build(
      new() { ["ReportsClient:Endpoint"] = "http://from-section:1" },
      settings => settings.Endpoint = new Uri("http://from-callback:3")
    );

    await Assert.That(BaseAddress(host)).IsEqualTo(new Uri("http://from-callback:3/"));
  }

  [Test]
  public async Task The_defaults_outlast_the_servers_own_queue_and_conversion_timeouts()
  {
    ReportsClientSettings settings = new();

    // The server's appsettings: Concurrency:QueueTimeout 30 s, ConversionTimeout 60 s.
    await Assert
      .That(settings.AttemptTimeout)
      .IsGreaterThan(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(60));
    await Assert.That(settings.TotalTimeout).IsGreaterThan(settings.AttemptTimeout);
    await Assert.That(settings.MaxRetryAttempts).IsEqualTo(3);
    await Assert.That(settings.DisableHealthChecks).IsFalse();
  }

  [Test]
  [Arguments(null)]
  [Arguments("/relative")]
  public async Task Settings_without_an_absolute_endpoint_are_rejected(string? endpoint)
  {
    ServiceCollection services = new();
    ReportsClientSettings settings = new()
    {
      Endpoint = endpoint is null ? null : new Uri(endpoint, UriKind.Relative),
    };

    await Assert.That(() => services.AddReportsClient(settings)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Timeouts_outside_what_the_pipeline_accepts_are_rejected()
  {
    ServiceCollection services = new();
    ReportsClientSettings settings = new()
    {
      Endpoint = StubServer.Endpoint,
      AttemptTimeout = TimeSpan.Zero,
    };

    await Assert
      .That(() => services.AddReportsClient(settings))
      .Throws<ArgumentException>()
      .WithMessageContaining("AttemptTimeout");
  }

  [Test]
  public async Task Registering_the_client_twice_fails()
  {
    ServiceCollection services = new();
    services.AddReportsClient(new ReportsClientSettings { Endpoint = StubServer.Endpoint });

    await Assert
      .That(() =>
        services.AddReportsClient(new ReportsClientSettings { Endpoint = StubServer.Endpoint })
      )
      .Throws<InvalidOperationException>();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task The_client_replaces_the_engine_and_its_warm_up_in_either_order(bool engineFirst)
  {
    ServiceCollection services = new();
    if (engineFirst)
    {
      services.AddBlazorReports();
      services.AddReportsEngine(options => options.Browser.WarmUpOnStartup = true);
    }

    services.AddReportsClient(new ReportsClientSettings { Endpoint = StubServer.Endpoint });

    if (!engineFirst)
    {
      services.AddBlazorReports();
      services.AddReportsEngine(options => options.Browser.WarmUpOnStartup = true);
    }

    await using var provider = services.BuildServiceProvider(
      new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
    );
    var converters = provider.GetServices<IHtmlToPdfConverter>().ToArray();
    await Assert.That(converters.Length).IsEqualTo(1);
    await Assert
      .That(converters[0].GetType().Assembly)
      .IsEqualTo(typeof(ReportsClientSettings).Assembly);
    await Assert
      .That(
        provider.GetRequiredService<IOptions<ReportsEngineOptions>>().Value.Browser.WarmUpOnStartup
      )
      .IsFalse();
  }

  [Test]
  public async Task The_health_check_reports_the_servers_readiness()
  {
    var ready = HttpStatusCode.OK;
    await using var server = StubServer.Start(
      (request, _) =>
        Task.FromResult(
          request.Uri.AbsolutePath == "/health/ready"
            ? new HttpResponseMessage(ready)
            : throw new InvalidOperationException($"Unexpected request to {request.Uri}.")
        )
    );

    var healthy = await server.HealthChecks.CheckHealthAsync(TestToken);
    ready = HttpStatusCode.ServiceUnavailable;
    var unready = await server.HealthChecks.CheckHealthAsync(TestToken);

    await Assert
      .That(healthy.Entries[ReportsClientExtensions.HealthCheckName].Status)
      .IsEqualTo(HealthStatus.Healthy);
    await Assert
      .That(unready.Entries[ReportsClientExtensions.HealthCheckName].Status)
      .IsEqualTo(HealthStatus.Unhealthy);
    await Assert
      .That(unready.Entries[ReportsClientExtensions.HealthCheckName].Tags)
      .Contains("ready");
    // The 503 was not retried: one probe per check.
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task An_unreachable_server_is_unhealthy()
  {
    await using var server = StubServer.Start(
      (_, _) => throw new HttpRequestException("Connection refused")
    );

    var report = await server.HealthChecks.CheckHealthAsync(TestToken);

    await Assert
      .That(report.Entries[ReportsClientExtensions.HealthCheckName].Status)
      .IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(server.Handler.Requests.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Health_checks_can_be_disabled()
  {
    await using var server = StubServer.Start(
      (_, _) => throw new InvalidOperationException("unreachable"),
      settings => settings.DisableHealthChecks = true
    );

    await Assert.That(server.Services.GetService<HealthCheckService>()).IsNull();
  }

  private static IHost Build(
    Dictionary<string, string?> configuration,
    Action<ReportsClientSettings>? configure = null
  )
  {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Configuration.AddInMemoryCollection(configuration);
    builder.AddReportsClient("reports", configure);
    return builder.Build();
  }

  private static Uri? BaseAddress(IHost host) =>
    host
      .Services.GetRequiredService<IHttpClientFactory>()
      .CreateClient("Atli.Reports.Client")
      .BaseAddress;

  private static ReportsClientSettings Copy(ReportsClientSettings settings) =>
    new()
    {
      Endpoint = settings.Endpoint,
      AttemptTimeout = settings.AttemptTimeout,
      TotalTimeout = settings.TotalTimeout,
      MaxRetryAttempts = settings.MaxRetryAttempts,
      DisableHealthChecks = settings.DisableHealthChecks,
      HealthCheckTimeout = settings.HealthCheckTimeout,
    };
}
