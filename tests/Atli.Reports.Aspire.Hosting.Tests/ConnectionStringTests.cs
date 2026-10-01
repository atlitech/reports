using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Atli.Reports.Aspire.Hosting.Tests.Support;

namespace Atli.Reports.Aspire.Hosting.Tests;

/// <summary>
/// The connection string and connection properties, which <c>Atli.Reports.Client</c> and apps in
/// other languages read.
/// </summary>
public class ConnectionStringTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task The_connection_string_is_the_endpoint_url()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");
    AppModel.Allocate(server.Resource, "http", 5123);

    IResourceWithConnectionString resource = server.Resource;
    var connectionString = await resource.GetConnectionStringAsync(TestToken);

    await Assert.That(connectionString).IsEqualTo("Endpoint=http://localhost:5123");
  }

  [Test]
  public async Task A_publisher_resolves_the_endpoint_url_itself()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");

    await Assert
      .That(server.Resource.ConnectionStringExpression.ValueExpression)
      .IsEqualTo("Endpoint={reports-server.bindings.http.url}");
  }

  [Test]
  public async Task The_connection_properties_are_host_port_and_uri()
  {
    var server = AppModel.CreateBuilder().AddReportsServer("reports-server");
    AppModel.Allocate(server.Resource, "http", 5123);

    Dictionary<string, string?> properties = [];
    foreach (
      var (name, value) in (
        (IResourceWithConnectionString)server.Resource
      ).GetConnectionProperties()
    )
    {
      properties[name] = await value.GetValueAsync(TestToken);
    }

    await Assert
      .That(properties)
      .IsEquivalentTo(
        new Dictionary<string, string?>
        {
          ["Host"] = "localhost",
          ["Port"] = "5123",
          ["Uri"] = "http://localhost:5123",
        }
      );
  }

  [Test]
  public async Task WithReference_gives_a_project_the_connection_string_the_client_reads()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports");
    AppModel.Allocate(server.Resource, "http", 5123);
    var project = AddApi(builder);

    var environment = await AppModel.EnvironmentAddedByAsync(
      project,
      api => api.WithReference(server)
    );

    // builder.AddReportsClient("reports") reads ConnectionStrings:reports.
    await Assert
      .That(environment["ConnectionStrings__reports"])
      .IsEqualTo("Endpoint=http://localhost:5123");
    await Assert.That(environment["REPORTS_HOST"]).IsEqualTo("localhost");
    await Assert.That(environment["REPORTS_PORT"]).IsEqualTo("5123");
    await Assert.That(environment["REPORTS_URI"]).IsEqualTo("http://localhost:5123");
  }

  [Test]
  public async Task A_publisher_gives_a_project_a_reference_to_the_connection_string()
  {
    var builder = AppModel.CreateBuilder();
    var server = builder.AddReportsServer("reports");
    var project = AddApi(builder);

    var environment = await AppModel.EnvironmentAddedByAsync(
      project,
      api => api.WithReference(server),
      DistributedApplicationOperation.Publish
    );

    await Assert
      .That(environment["ConnectionStrings__reports"])
      .IsEqualTo("{reports.connectionString}");
  }

  // A project the model only refers to; without its launch profile, it is never read from disk.
  private static IResourceBuilder<ProjectResource> AddApi(IDistributedApplicationBuilder builder) =>
    builder.AddProject("api", "../Api/Api.csproj", options => options.ExcludeLaunchProfile = true);
}
