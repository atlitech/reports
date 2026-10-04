using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// The renderers of tenants under the managed prefixes, as the service ensures and deletes them. A
/// renderer that exists is found without counting toward any limit. A new one, or one replacing a
/// sandbox that is gone, is created once at a time per tenant, within its prefix's
/// <c>MaxTenants</c> and the service's <c>MaxCreatesPerMinute</c>.
/// </summary>
/// <remarks>
/// <para>
/// A creation runs apart from the requests that wait for it: a request that gives up leaves it
/// running for the others, and a later request finds its renderer. Stopping the service cancels
/// the creations, and each deletes the sandbox it made, as a canceled <c>create</c> does.
/// </para>
/// <para>
/// Sharing a creation covers one process. Replicas that create the same tenant's renderer at once
/// are kept apart by <see cref="RendererProvisioner.EnsureAsync"/>: the one that writes the record
/// second discards its sandbox and finds the other's.
/// </para>
/// </remarks>
internal sealed partial class ManagedRenderers
{
  private readonly ProvisionerOptions _options;
  private readonly RendererProvisioner _provisioner;
  private readonly TenantCensus _census;
  private readonly CreationRateLimit _rateLimit;
  private readonly ILogger _logger;
  private readonly CancellationToken _stopping;
  private readonly Lock _lock = new();
  private readonly Dictionary<string, Task<EnsureResult>> _creating = new(StringComparer.Ordinal);

  public ManagedRenderers(
    ProvisionerOptions options,
    RendererProvisioner provisioner,
    TenantCensus census,
    TimeProvider time,
    ILogger<ManagedRenderers> logger,
    IHostApplicationLifetime lifetime
  )
  {
    _options = options;
    _provisioner = provisioner;
    _census = census;
    _rateLimit = new CreationRateLimit(options.Service.MaxCreatesPerMinute, time);
    _logger = logger;
    _stopping = lifetime.ApplicationStopping;
  }

  /// <summary>
  /// Makes sure the tenant has a ready renderer and record: finds it, waits for the creation in
  /// flight, or creates one. Failures are logged here, with why; the answer only says which.
  /// </summary>
  /// <param name="tenantId">The tenant, as the caller sent it.</param>
  /// <param name="cancellationToken">The request's; canceling it stops the wait, not a creation.</param>
  public async Task<RendererOutcome> EnsureAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    if (Admit(tenantId) is not { } prefix)
    {
      return new RendererOutcome(
        TenantId.IsValid(tenantId) ? RendererAnswer.NotAllowed : RendererAnswer.InvalidTenantId
      );
    }

    Task<EnsureResult>? creation;
    lock (_lock)
    {
      _creating.TryGetValue(tenantId, out creation);
    }

    if (creation is not null)
    {
      LogJoined(_logger, tenantId);
      return await Answer(creation, cancellationToken);
    }

    try
    {
      if (await _provisioner.FindAsync(tenantId, cancellationToken) is not null)
      {
        _census.Recorded(tenantId);
        return new RendererOutcome(RendererAnswer.Found);
      }

      await _census.RefreshAsync(cancellationToken);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      LogEnsureFailed(_logger, tenantId, exception);
      return new RendererOutcome(RendererAnswer.Failed);
    }

    var joined = true;
    lock (_lock)
    {
      if (!_creating.TryGetValue(tenantId, out creation))
      {
        // The tenant itself is not counted: replacing its missing sandbox adds no renderer.
        if (_census.CountUnder(prefix.Prefix, tenantId, _creating.Keys) >= prefix.MaxTenants)
        {
          LogQuotaExceeded(_logger, tenantId, prefix.Prefix, prefix.MaxTenants);
          return new RendererOutcome(RendererAnswer.QuotaExceeded);
        }

        if (!_rateLimit.TryAcquire(out var retryAfter))
        {
          LogRateLimited(_logger, tenantId, _options.Service.MaxCreatesPerMinute);
          return new RendererOutcome(RendererAnswer.RateLimited, retryAfter);
        }

        creation = Start(tenantId, prefix);
        joined = false;
      }
    }

    if (joined)
    {
      LogJoined(_logger, tenantId);
    }

    return await Answer(creation, cancellationToken);
  }

  /// <summary>
  /// Deletes the tenant's renderer and record at once, without a drain, after the creation in
  /// flight for it, if any, has finished.
  /// </summary>
  public async Task<RendererOutcome> DeleteAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    if (Admit(tenantId) is null)
    {
      return new RendererOutcome(
        TenantId.IsValid(tenantId) ? RendererAnswer.NotAllowed : RendererAnswer.InvalidTenantId
      );
    }

    Task? creation;
    lock (_lock)
    {
      creation = _creating.GetValueOrDefault(tenantId);
    }

    // Otherwise the creation could write its record after the delete. Its failure is its own.
    if (creation is not null)
    {
      await creation
        .WaitAsync(cancellationToken)
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    try
    {
      await _provisioner.DeleteAsync(tenantId, TimeSpan.Zero, cancellationToken);
      _census.Deleted(tenantId);
      LogDeleted(_logger, tenantId);
      return new RendererOutcome(RendererAnswer.Deleted);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      LogDeleteFailed(_logger, tenantId, exception);
      return new RendererOutcome(RendererAnswer.Failed);
    }
  }

  /// <summary>Waits until no creation is in flight, as after stopping, when each cleans up.</summary>
  public async Task WhenIdleAsync()
  {
    Task[] creations;
    lock (_lock)
    {
      creations = [.. _creating.Values];
    }

    await Task.WhenAll(creations).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
  }

  /// <summary>
  /// The managed prefix the tenant falls under, or <see langword="null"/> for a tenant ID that is
  /// not one or that no prefix owns; the latter is logged, as a sign that the gateway's and the
  /// service's prefixes disagree.
  /// </summary>
  private ManagedTenantPrefix? Admit(string tenantId)
  {
    if (!TenantId.IsValid(tenantId))
    {
      return null;
    }

    var prefix = _options.Service.PrefixOf(tenantId);
    if (prefix is null)
    {
      LogNotAllowed(_logger, tenantId);
    }

    return prefix;
  }

  /// <summary>Starts creating the tenant's renderer; called under the lock.</summary>
  private Task<EnsureResult> Start(string tenantId, ManagedTenantPrefix prefix)
  {
    var size = prefix.Size.Length > 0 ? RendererSize.Parse(prefix.Size) : _options.RendererSize;
    Task<EnsureResult> creation;
    // Without the starting request's execution context: the creation is everyone's, so neither
    // that request's logging scopes nor its trace may follow it.
    using (ExecutionContext.SuppressFlow())
    {
      creation = Task.Run(() => CreateAsync(tenantId, size), CancellationToken.None);
    }

    _creating[tenantId] = creation;
    return creation;
  }

  private async Task<EnsureResult> CreateAsync(string tenantId, RendererSize size)
  {
    try
    {
      var result = await _provisioner.EnsureAsync(tenantId, size, _options.DiskImageId, _stopping);
      _census.Recorded(tenantId);
      if (result.Created)
      {
        LogCreated(_logger, tenantId, result.Record.SandboxId, size.Name);
      }

      return result;
    }
    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
    {
      LogCreateCanceled(_logger, tenantId);
      throw;
    }
    catch (Exception exception)
    {
      LogEnsureFailed(_logger, tenantId, exception);
      throw;
    }
    finally
    {
      lock (_lock)
      {
        _creating.Remove(tenantId);
      }
    }
  }

  /// <summary>Waits for a creation; its failure was logged where it happened.</summary>
  private static async Task<RendererOutcome> Answer(
    Task<EnsureResult> creation,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await creation.WaitAsync(cancellationToken);
      return new RendererOutcome(result.Created ? RendererAnswer.Created : RendererAnswer.Found);
    }
    catch (Exception) when (!cancellationToken.IsCancellationRequested)
    {
      return new RendererOutcome(RendererAnswer.Failed);
    }
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Created the renderer of tenant {TenantId}: sandbox {SandboxId}, size {Size}."
  )]
  private static partial void LogCreated(
    ILogger logger,
    string tenantId,
    string? sandboxId,
    string size
  );

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Error,
    Message = "Could not make the renderer of tenant {TenantId} ready."
  )]
  private static partial void LogEnsureFailed(ILogger logger, string tenantId, Exception exception);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Warning,
    Message = "Stopped creating the renderer of tenant {TenantId}: the service is stopping."
  )]
  private static partial void LogCreateCanceled(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Debug,
    Message = "Tenant {TenantId} waits for the creation of its renderer in flight."
  )]
  private static partial void LogJoined(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Warning,
    Message = "Refused tenant {TenantId}: prefix {Prefix} already has its most renderers ({MaxTenants})."
  )]
  private static partial void LogQuotaExceeded(
    ILogger logger,
    string tenantId,
    string prefix,
    int maxTenants
  );

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Warning,
    Message = "Refused tenant {TenantId} for now: {MaxCreatesPerMinute} renderers were created in the last minute."
  )]
  private static partial void LogRateLimited(
    ILogger logger,
    string tenantId,
    int maxCreatesPerMinute
  );

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Warning,
    Message = "Refused tenant {TenantId}: it is under none of Provisioner:Service:TenantPrefixes. Do the gateway's TenantPrefixes match?"
  )]
  private static partial void LogNotAllowed(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Information,
    Message = "Deleted the renderer of tenant {TenantId}, if it had one."
  )]
  private static partial void LogDeleted(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Error,
    Message = "Could not delete the renderer of tenant {TenantId}."
  )]
  private static partial void LogDeleteFailed(ILogger logger, string tenantId, Exception exception);
}

/// <summary>How the service answers a request for a tenant's renderer.</summary>
internal enum RendererAnswer
{
  /// <summary><c>200</c>, <c>created: false</c>: the renderer exists.</summary>
  Found,

  /// <summary><c>200</c>, <c>created: true</c>.</summary>
  Created,

  /// <summary><c>204</c>: the renderer and record are gone, or never were.</summary>
  Deleted,

  /// <summary><c>400</c>: the tenant ID is not one.</summary>
  InvalidTenantId,

  /// <summary><c>403</c>: no managed prefix owns the tenant.</summary>
  NotAllowed,

  /// <summary><c>429</c> without <c>Retry-After</c>: the prefix has its most renderers.</summary>
  QuotaExceeded,

  /// <summary><c>429</c> with <c>Retry-After</c>: too many creates in the last minute.</summary>
  RateLimited,

  /// <summary><c>503</c>: something failed, and was logged.</summary>
  Failed,
}

/// <param name="Answer">The answer.</param>
/// <param name="RetryAfter">For <see cref="RendererAnswer.RateLimited"/>: when a create may start.</param>
internal readonly record struct RendererOutcome(
  RendererAnswer Answer,
  TimeSpan RetryAfter = default
);
