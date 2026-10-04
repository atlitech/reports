namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// The Azure Container Apps Sandboxes data plane of one sandbox group: the calls the provisioner
/// and the gateway make. Errors other than those documented throw <see cref="SandboxesException"/>.
/// </summary>
/// <remarks>
/// The gateway only ever needs <see cref="GetAsync"/> and <see cref="ResumeAsync"/>, which a custom
/// role with <c>Microsoft.App/sandboxGroups/sandboxes/read</c> and
/// <c>Microsoft.App/sandboxGroups/sandboxes/resume/action</c> allows. Everything else belongs to the
/// provisioner.
/// </remarks>
public interface ISandboxesClient
{
  /// <summary>Creates a sandbox and returns it once the platform reports it.</summary>
  Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);

  /// <summary>Returns the sandbox, or <see langword="null"/> when it does not exist.</summary>
  Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Returns every sandbox in the group.</summary>
  Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken);

  /// <summary>Deletes the sandbox. Deleting a sandbox that does not exist succeeds.</summary>
  Task DeleteAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Suspends the sandbox, keeping its memory and disk.</summary>
  Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>Resumes a suspended sandbox. Resuming a running sandbox succeeds.</summary>
  Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken);

  /// <summary>
  /// Exposes a port through the platform's proxy and returns the sandbox with the port's URL.
  /// </summary>
  Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    bool anonymous,
    CancellationToken cancellationToken
  );

  /// <summary>
  /// Exposes a port through the platform's proxy with the given <paramref name="activation"/>, and
  /// returns the sandbox with the port's URL. With <see cref="SandboxPortActivation.OnDemand"/> a
  /// request to the port resumes a stopped sandbox, so its callers need no resume permission.
  /// </summary>
  /// <remarks>
  /// Clients written before activation modes existed implement only the other overload, which
  /// exposes a <see cref="SandboxPortActivation.Manual"/> port: this one delegates to it for
  /// <see cref="SandboxPortActivation.Manual"/> and fails with
  /// <see cref="NotSupportedException"/> otherwise.
  /// </remarks>
  Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    bool anonymous,
    SandboxPortActivation activation,
    CancellationToken cancellationToken
  ) =>
    activation == SandboxPortActivation.Manual
      ? AddPortAsync(sandboxId, port, anonymous, cancellationToken)
      : Task.FromException<SandboxView>(
        new NotSupportedException(
          $"This Sandboxes client cannot expose a port with {activation} activation."
        )
      );
}
