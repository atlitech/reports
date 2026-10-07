#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

/// <summary>Builds sandbox renderer disks as part of the Aspire Azure deployment pipeline.</summary>
public static class ReportsRendererImageExtensions
{
  /// <summary>
  /// Builds a renderer disk from the tracked server sources in a Reports checkout. Relative paths
  /// are resolved against the AppHost directory. Deploy needs Git and the Azure Sandboxes ACA CLI;
  /// publishing only writes metadata and does not build or contact Azure.
  /// </summary>
  public static IResourceBuilder<ReportsRendererImageResource> AddReportsRendererImage(
    this IDistributedApplicationBuilder builder,
    string name,
    IResourceBuilder<AzureReportsEnvironmentResource> environment,
    string repositoryRoot
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
    var resource = new ReportsRendererImageResource(
      name,
      environment.Resource,
      Path.GetFullPath(repositoryRoot, builder.AppHostDirectory)
    );
    var image = builder
      .AddResource(resource)
      .WithManifestPublishingCallback(context =>
      {
        context.Writer.WriteString("type", "atli.reports.renderer-image.v0");
        context.Writer.WriteString("source", resource.RepositoryRoot);
        context.Writer.WriteString(
          "sandboxGroup",
          resource.Environment.SandboxGroupName.ValueExpression
        );
        return Task.CompletedTask;
      });

    image.WithPipelineStepFactory(_ => new PipelineStep
    {
      Name = $"check-reports-renderer-tools-{name}",
      Description =
        "Checks Git, the Azure Sandboxes CLI and the renderer source before provisioning.",
      RequiredBySteps = [WellKnownPipelineSteps.DeployPrereq],
      Action = context =>
        ReportsRendererImageBuilder.CheckPrerequisitesAsync(resource, context.CancellationToken),
    });
    image.WithPipelineStepFactory(_ => new PipelineStep
    {
      Name = resource.BuildStepName,
      Description = "Builds or reuses the renderer disk in the isolated sandbox group.",
      DependsOnSteps =
      [
        $"provision-{environment.Resource.Name}",
        WellKnownPipelineSteps.DeployPrereq,
      ],
      RequiredBySteps = [WellKnownPipelineSteps.Deploy],
      Action = async context =>
      {
        resource.ResolvedImageId = await ReportsRendererImageBuilder
          .BuildAsync(resource, context.CancellationToken)
          .ConfigureAwait(false);
        context.Summary.Add($"Renderer disk ({name})", resource.ResolvedImageId);
      },
    });
    return image;
  }

  /// <summary>Wires the provisioning service and waits for its renderer disk before deploying it.</summary>
  public static IResourceBuilder<AzureReportsEnvironmentResource> WithReportsProvisioner(
    this IResourceBuilder<AzureReportsEnvironmentResource> environment,
    IResourceBuilder<ReportsProvisionerResource> provisioner,
    IResourceBuilder<ReportsRendererImageResource> image
  )
  {
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentNullException.ThrowIfNull(provisioner);
    ArgumentNullException.ThrowIfNull(image);
    if (!ReferenceEquals(environment.Resource, image.Resource.Environment))
    {
      throw new ArgumentException(
        "The renderer image and provisioner must use the same Reports environment.",
        nameof(image)
      );
    }
    environment.WithReportsProvisioner(provisioner, image.Resource.ImageId);
    provisioner.WithReportsRendererImage(image);
    return environment;
  }

  /// <summary>Sets the renderer disk and orders its build before the consuming service's infrastructure deployment.</summary>
  public static IResourceBuilder<T> WithReportsRendererImage<T>(
    this IResourceBuilder<T> builder,
    IResourceBuilder<ReportsRendererImageResource> image
  )
    where T : IResourceWithEnvironment
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(image);
    builder.WithEnvironment(context =>
      context.EnvironmentVariables["Provisioner__DiskImageId"] = image.Resource.ImageId
    );
    builder.WithPipelineConfiguration(context =>
    {
      // ACA applies environment variables during its Bicep provisioning step. Waiting only on
      // deploy-{name} is too late: that step merely summarizes an already-provisioned revision.
      var target = builder.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget;
      if (target is not null)
      {
        context
          .GetSteps(target, WellKnownPipelineTags.ProvisionInfrastructure)
          .DependsOn(image.Resource.BuildStepName);
      }
    });
    return builder;
  }
}
