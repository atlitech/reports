namespace Aspire.Hosting.ApplicationModel;

/// <summary>A renderer disk built from a Reports source checkout during Azure deployment.</summary>
public sealed class ReportsRendererImageResource : Resource
{
  internal ReportsRendererImageResource(
    string name,
    AzureReportsEnvironmentResource environment,
    string repositoryRoot
  )
    : base(name)
  {
    Environment = environment;
    RepositoryRoot = repositoryRoot;
    ImageId = new ReportsRendererImageReference(this);
  }

  /// <summary>The Azure environment whose sandbox group owns the disk.</summary>
  public AzureReportsEnvironmentResource Environment { get; }

  /// <summary>The absolute path of the Reports source checkout.</summary>
  public string RepositoryRoot { get; }

  /// <summary>The disk identifier resolved after the deployment pipeline builds or reuses it.</summary>
  public ReportsRendererImageReference ImageId { get; }

  /// <summary>The deployment step that makes the disk available.</summary>
  public string BuildStepName => $"build-reports-renderer-{Name}";

  internal string? ResolvedImageId { get; set; }
}

/// <summary>A deployment output; publishing preserves its expression without contacting Azure.</summary>
public sealed class ReportsRendererImageReference : IManifestExpressionProvider, IValueProvider
{
  private readonly ReportsRendererImageResource resource;

  internal ReportsRendererImageReference(ReportsRendererImageResource resource) =>
    this.resource = resource;

  /// <inheritdoc />
  public string ValueExpression => $"{{{resource.Name}.outputs.imageId}}";

  /// <inheritdoc />
  public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<string?>(
      resource.ResolvedImageId
        ?? throw new InvalidOperationException(
          $"Renderer image '{resource.Name}' is not built. Wire the provisioner with WithReportsProvisioner(provisioner, image) so its deployment waits for the disk build."
        )
    );
  }
}
