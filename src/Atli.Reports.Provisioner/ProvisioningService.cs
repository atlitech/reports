namespace Atli.Reports.Provisioner;

/// <summary>
/// <c>serve</c>: the provisioning service's HTTP API (see
/// <see cref="Atli.Reports.Hosting.Provisioning.ProvisioningApi"/>) and its retirement loop, until
/// <c>cancellationToken</c> stops it.
/// </summary>
internal static class ProvisioningService
{
  /// <summary>Runs the service; returns the process exit code once it has stopped.</summary>
  public static Task<int> RunAsync(
    ProvisionerOptions options,
    ProvisionerServices services,
    RendererProvisioner provisioner,
    TimeProvider time,
    TextWriter output,
    CancellationToken cancellationToken
  ) => throw new NotImplementedException();
}
