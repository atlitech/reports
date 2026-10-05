namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// The Azure Container Apps Sandboxes data plane of one sandbox group, used by the provisioner.
/// Errors other than those documented throw <see cref="SandboxesException"/>.
/// </summary>
public interface ISandboxesClient
{
  /// <summary>Creates a sandbox and returns it once the platform reports it.</summary>
  Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);

  /// <summary>Returns the sandbox, or <see langword="null"/> when it does not exist.</summary>
  Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>
  /// Returns every sandbox in the group, from every page of the data plane's listing. Callers
  /// decide from it which sandboxes a tenant has (to delete, disable, retire, or prune them), so a
  /// listing that cannot be read whole fails rather than returning part.
  /// </summary>
  Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken);

  /// <summary>Deletes the sandbox. Deleting a sandbox that does not exist succeeds.</summary>
  Task DeleteAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Suspends the sandbox, keeping its memory and disk.</summary>
  Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Resumes a suspended sandbox. Resuming a running sandbox succeeds.</summary>
  Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>
  /// Exposes a port through the platform's proxy as <paramref name="options"/> say, and returns
  /// the sandbox with the port's URL. With <see cref="SandboxPortActivation.OnDemand"/> a request to
  /// the port resumes a stopped sandbox, so its callers need no resume permission;
  /// <see cref="SandboxPortOptions.AllowedSourceCidrs"/> limits who can make one.
  /// </summary>
  Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    SandboxPortOptions options,
    CancellationToken cancellationToken
  );

  /// <summary>
  /// Disables the sandbox: stops it, and refuses to start it again, whether by a request to an
  /// on-demand port (<c>403</c>) or by a resume (<c>409 SandboxAdminDisabled</c>), until
  /// <see cref="EnableAsync"/>. The kill switch for a compromised renderer; its disk and record stay
  /// for investigation.
  /// </summary>
  Task<SandboxView> DisableAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Lets a disabled sandbox be started again; it stays stopped until it is.</summary>
  Task<SandboxView> EnableAsync(string sandboxId, CancellationToken cancellationToken);
}
