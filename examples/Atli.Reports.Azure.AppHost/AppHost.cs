using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// This AppHost deploys the hosted service. The sibling Atli.Reports.AppHost runs local examples.
if (builder.ExecutionContext.IsRunMode)
{
  throw new InvalidOperationException(
    "Use aspire publish or aspire deploy with this AppHost. For local development, use examples/Atli.Reports.AppHost."
  );
}

var reportsKey = builder.AddParameter("reportsKey", secret: true);
var reportsKeyHash = builder.AddParameter("reportsKeyHash", secret: true);
var provisionerKey = builder.AddParameter("provisionerKey", secret: true);
var provisionerKeyHash = builder.AddParameter("provisionerKeyHash", secret: true);

var environment = builder.AddAzureReportsEnvironment("reports");
var rendererImage = builder.AddReportsRendererImage("renderer-image", environment, "../..");

var provisioner = builder
  .AddReportsProvisioner("reports-provisioner")
  .WithDockerfile("../..", "src/Atli.Reports.Provisioner/Dockerfile")
  .WithApiKeyAuthentication("gateway", provisionerKeyHash)
  .WithTenantPrefix("app-", maxTenants: 100, maxCreatesPerMinute: 20)
  .WithReportsRendererImage(rendererImage);

var gateway = builder
  .AddReportsGateway("reports-gateway")
  .WithDockerfile("../..", "src/Atli.Reports.Server/Dockerfile")
  .WithApiKeyAuthentication("application", reportsKey, reportsKeyHash, "application")
  .WithTenantPrefix("application", "app-")
  .WithProvisioner(provisioner, provisionerKey)
  .WithExternalHttpEndpoints();

environment
  .WithReportsProvisioner(provisioner, rendererImage.Resource.ImageId)
  .WithReportsGateway(gateway);

builder.Build().Run();
