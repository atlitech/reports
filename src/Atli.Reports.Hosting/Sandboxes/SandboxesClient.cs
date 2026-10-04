// Stub: the implementation replaces this file, and this pragma with it.
#pragma warning disable CS9113
using Azure.Core;

namespace Atli.Reports.Hosting.Sandboxes;

/// <summary>
/// <see cref="ISandboxesClient"/> over the data plane's REST API, authenticated with Microsoft Entra
/// tokens for <see cref="SandboxesOptions.TokenScope"/>, cached until shortly before they expire.
/// </summary>
/// <param name="httpClient">The client to send requests with; its base address is ignored.</param>
/// <param name="credential">The credential tokens come from.</param>
/// <param name="options">The sandbox group.</param>
public sealed class SandboxesClient(
  HttpClient httpClient,
  TokenCredential credential,
  SandboxesOptions options
) : ISandboxesClient
{
  /// <inheritdoc />
  public Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task DeleteAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    bool anonymous,
    CancellationToken cancellationToken
  ) => throw new NotImplementedException();
}
