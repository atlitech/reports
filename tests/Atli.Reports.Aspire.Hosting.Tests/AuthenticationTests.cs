using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Aspire.Hosting.Tests;

public class AuthenticationTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Production_parameters_remain_secret_references_in_published_configuration()
  {
    var builder = AppModel.CreatePublishBuilder();
    var key = builder.AddParameter("reports-key", "caller.full-secret-value", secret: true);
    var hash = builder.AddParameter("reports-hash", "hashed-value", secret: true);
    var server = builder
      .AddReportsServer("reports")
      .WithApiKeyAuthentication("caller", key, hash, "my-app");

    var environment = await AppModel.PublishedEnvironmentAsync(server.Resource);

    await Assert.That(environment["ReportsServer__Authentication__Mode"]).IsEqualTo("ApiKey");
    await Assert
      .That(environment["ReportsServer__Authentication__ApiKeys__0__Id"])
      .IsEqualTo("caller");
    await Assert
      .That(environment["ReportsServer__Authentication__ApiKeys__0__CallerId"])
      .IsEqualTo("my-app");
    await Assert
      .That(environment["ReportsServer__Authentication__ApiKeys__0__Hash"])
      .IsEqualTo("{reports-hash.value}");
    await Assert
      .That(environment["ReportsServer__Authentication__ApiKeys__0__Permissions__0"])
      .IsEqualTo("reports.convert");
    await Assert
      .That(server.Resource.ConnectionStringExpression.ValueExpression)
      .IsEqualTo("Endpoint={reports.bindings.http.url};ApiKey={reports-key.value}");
    await Assert.That(string.Join(";", environment.Values)).DoesNotContain("full-secret-value");
    await Assert.That(key.Resource.Secret).IsTrue();
    await Assert.That(hash.Resource.Secret).IsTrue();
  }

  [Test]
  public async Task References_supply_the_credential_to_clients_but_only_the_verifier_to_the_server()
  {
    var builder = AppModel.CreateBuilder();
    var key = builder.AddParameter("reports-key", "caller.full-secret-value", secret: true);
    var hash = builder.AddParameter("reports-hash", "hashed-value", secret: true);
    var server = builder.AddReportsServer("reports").WithApiKeyAuthentication("caller", key, hash);
    AppModel.Allocate(server.Resource, "http", 5123);
    var client = builder.AddProject(
      "api",
      "../Api/Api.csproj",
      options => options.ExcludeLaunchProfile = true
    );

    var environment = await AppModel.EnvironmentAddedByAsync(
      client,
      app => app.WithReference(server)
    );

    await Assert
      .That(environment["ConnectionStrings__reports"])
      .IsEqualTo("Endpoint=http://localhost:5123;ApiKey=caller.full-secret-value");
    await Assert.That(environment["REPORTS_APIKEY"]).IsEqualTo("caller.full-secret-value");
    await Assert.That(environment["REPORTS_URI"]).IsEqualTo("http://localhost:5123");
  }

  [Test]
  public async Task Development_credentials_are_random_matched_secret_parameters_without_published_defaults()
  {
    var builder = AppModel.CreateBuilder();
    var first = builder.AddReportsServer("first").WithDevelopmentApiKey();
    var second = builder.AddReportsServer("second").WithDevelopmentApiKey();
    var keys = builder
      .Resources.OfType<ParameterResource>()
      .ToDictionary(parameter => parameter.Name);
    var firstKey = await keys["first-api-key"].GetValueAsync(TestToken);
    var secondKey = await keys["second-api-key"].GetValueAsync(TestToken);

    await Assert.That(firstKey).StartsWith("development.");
    await Assert.That(firstKey!.Length).IsEqualTo("development.".Length + 64);
    await Assert.That(firstKey).IsNotEqualTo(secondKey);
    await Assert
      .That(await keys["first-api-key-hash"].GetValueAsync(TestToken))
      .IsEqualTo(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(firstKey))));
    foreach (var parameter in keys.Values)
    {
      await Assert.That(parameter.Secret).IsTrue();
      await Assert.That(parameter.Default).IsNull();
    }
    await Assert
      .That(first.Resource.ConnectionStringExpression.ValueExpression)
      .DoesNotContain(firstKey);
    await Assert
      .That(second.Resource.ConnectionStringExpression.ValueExpression)
      .DoesNotContain(secondKey!);
  }

  [Test]
  public async Task Development_credentials_cannot_be_published()
  {
    var server = AppModel.CreatePublishBuilder().AddReportsServer("reports");
    await Assert.That(() => server.WithDevelopmentApiKey()).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Credential_parameters_must_be_marked_secret()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports");
    var key = builder.AddParameter("key", "caller.secret");
    var hash = builder.AddParameter("hash", "hash", secret: true);
    await Assert
      .That(() => server.WithApiKeyAuthentication("caller", key, hash))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Anonymous_access_is_an_explicit_mode_without_a_connection_credential()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports").WithAnonymousAccess();

    var environment = await AppModel.PublishedEnvironmentAsync(server.Resource);

    await Assert.That(environment["ReportsServer__Authentication__Mode"]).IsEqualTo("None");
    await Assert
      .That(server.Resource.ConnectionStringExpression.ValueExpression)
      .DoesNotContain("ApiKey");
  }

  [Test]
  public async Task The_dashboard_command_sends_its_configured_api_key()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports").WithDevelopmentApiKey();
    AppModel.Allocate(server.Resource, "http", 5123);
    CommandHandler handler = new();
    builder.Services.AddSingleton<IHttpClientFactory>(new CommandClientFactory(handler));
    await using var app = builder.Build();
    var command = server
      .Resource.Annotations.OfType<ResourceCommandAnnotation>()
      .Single(annotation => annotation.Name == "convert-test-page");

    var result = await command.ExecuteCommand(
      new ExecuteCommandContext
      {
        ServiceProvider = app.Services,
        ResourceName = server.Resource.Name,
        CancellationToken = TestToken,
      }
    );

    var key = builder
      .Resources.OfType<ParameterResource>()
      .Single(parameter => parameter.Name == "reports-api-key");
    await Assert.That(result.Success).IsTrue();
    await Assert.That(handler.Credential).IsEqualTo(await key.GetValueAsync(TestToken));
    await Assert.That(handler.Body).DoesNotContain(handler.Credential!);
  }

  [Test]
  public async Task The_dashboard_command_explains_missing_credentials_without_sending_a_request()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports");
    await using var app = builder.Build();
    var command = server
      .Resource.Annotations.OfType<ResourceCommandAnnotation>()
      .Single(annotation => annotation.Name == "convert-test-page");

    var result = await command.ExecuteCommand(
      new ExecuteCommandContext
      {
        ServiceProvider = app.Services,
        ResourceName = server.Resource.Name,
        CancellationToken = TestToken,
      }
    );

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorMessage).Contains("No dashboard conversion credential");
  }

  private sealed class CommandClientFactory(CommandHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private sealed class CommandHandler : HttpMessageHandler
  {
    internal string? Credential { get; private set; }
    internal string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken
    )
    {
      Credential = request.Headers.GetValues("X-Reports-Api-Key").Single();
      Body = await request.Content!.ReadAsStringAsync(cancellationToken);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent("%PDF-1.7 %%EOF"),
      };
    }
  }
}
