using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// The renderers of tenants under the managed prefixes, as the service ensures and deletes them. A
/// renderer that exists is found without counting toward any limit, and so is a replacement for a
/// tenant's sandbox that is gone. A new tenant's renderer is created within its prefix's
/// <c>MaxTenants</c> and <c>MaxCreatesPerMinute</c> and the service's <c>MaxCreatesPerMinute</c>,
/// each refused before the tenant's record is read when the census does not list it. One creation
/// runs at a time per tenant.
/// </summary>
/// <remarks>
/// <para>
/// A creation runs apart from the requests that wait for it: a request that gives up leaves it
/// running for the others, and a later request finds its renderer. Stopping the service cancels
/// the creations, and each deletes the sandbox it made, as a canceled <c>create</c> does.
/// </para>
/// <para>
/// A tenant's creation, delete, and retirement never overlap (see <see cref="TenantGate"/>): a
/// delete waits for the creation in flight, and requests for the tenant that arrive during a delete
/// wait for it, then start afresh.
/// </para>
/// <para>
/// Sharing a creation covers one process. Replicas that create the same tenant's renderer at once
/// are kept apart by <see cref="RendererProvisioner.EnsureAsync"/>: the one that writes the record
/// second discards its sandbox and finds the other's.
/// </para>
/// </remarks>
internal sealed partial class ManagedRenderers : IDisposable
{
  /// <summary>
  /// Deletes run at most this many at once, across tenants: each lists the whole sandbox group, and
  /// the gateways' own limits are per caller and replica.
  /// </summary>
  public const int MaxConcurrentDeletes = 4;

  private readonly SemaphoreSlim _deletes = new(MaxConcurrentDeletes, MaxConcurrentDeletes);
  private readonly ProvisionerOptions _options;
  private readonly RendererProvisioner _provisioner;
  private readonly TenantCensus _census;
  private readonly TenantGate _gate;
  private readonly TimeProvider _time;

  // The service's MaxCreatesPerMinute, a ceiling over each prefix's own.
  private readonly CreationRateLimit _rateLimit;
  private readonly Dictionary<string, CreationRateLimit> _prefixRateLimits;
  private readonly ILogger _logger;
  private readonly CancellationToken _stopping;

  // Taken before the gate's own lock, never after: checking the limits and starting a creation are
  // one step.
  private readonly Lock _lock = new();

  // By prefix: the creations in flight for tenants without a record, which the census does not
  // count yet.
  private readonly Dictionary<string, int> _creatingNew = new(StringComparer.Ordinal);

  public ManagedRenderers(
    ProvisionerOptions options,
    RendererProvisioner provisioner,
    TenantCensus census,
    TenantGate gate,
    TimeProvider time,
    ILogger<ManagedRenderers> logger,
    IHostApplicationLifetime lifetime
  )
  {
    _options = options;
    _provisioner = provisioner;
    _census = census;
    _gate = gate;
    _time = time;
    _rateLimit = new CreationRateLimit(options.Service.MaxCreatesPerMinute, time);
    _prefixRateLimits = options.Service.TenantPrefixes.ToDictionary(
      prefix => prefix.Prefix,
      prefix => new CreationRateLimit(prefix.MaxCreatesPerMinute, time),
      StringComparer.Ordinal
    );
    _logger = logger;
    _stopping = lifetime.ApplicationStopping;
  }

  /// <summary>
  /// Makes sure the tenant has a ready renderer and record: finds it, waits for the creation in
  /// flight, or creates one, after the delete in flight if there is one. Failures are logged here,
  /// with why; the answer only says which.
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

    while (true)
    {
      var (creation, removal) = _gate.InFlight(tenantId);
      if (creation is not null)
      {
        LogJoined(_logger, tenantId);
        return await Answer(creation, cancellationToken);
      }

      if (removal is not null)
      {
        LogWaitsForDelete(_logger, tenantId);
        await removal.WaitAsync(cancellationToken);
        continue;
      }

      // Whether the tenant has a record. A current census that does not list it answers without
      // reading the store, so a new tenant is refused, if it is, before any read; a record written
      // since the listing is still found by the creation, which reads the record first.
      var recorded = false;
      if (_census.HasRecord(tenantId) != false)
      {
        try
        {
          var (record, found) = await _provisioner.LookUpAsync(tenantId, cancellationToken);
          if (found)
          {
            _census.Recorded(tenantId);
            return new RendererOutcome(RendererAnswer.Found);
          }

          recorded = record is not null;
          if (!recorded)
          {
            _census.Deleted(tenantId);
          }
        }
        catch (Exception exception)
          when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested
          )
        {
          LogEnsureFailed(_logger, tenantId, exception);
          return new RendererOutcome(RendererAnswer.Failed);
        }
      }

      // A new tenant counts toward its prefix's quota, which needs a listing; at the start, the
      // first may not have ended yet.
      if (!recorded && !_census.HasListed)
      {
        await _census.FirstListing.WaitAsync(cancellationToken);
        if (!_census.HasListed)
        {
          var failure = _census.LastFailure;
          var reason =
            $"The renderers under prefix {prefix.Prefix} cannot be counted: the record store's "
            + "tenants have not been listed"
            + (failure is null ? "." : $" ({failure.Message}).");
          LogEnsureFailed(
            _logger,
            tenantId,
            failure is null
              ? new ProvisioningException(reason)
              : new ProvisioningException(reason, failure)
          );
          return new RendererOutcome(RendererAnswer.Failed);
        }
      }

      lock (_lock)
      {
        (creation, removal) = _gate.InFlight(tenantId);
        if (creation is null && removal is null)
        {
          var now = _time.GetTimestamp();
          // A tenant with a record is never refused: replacing its missing sandbox adds no
          // renderer, and creates no workspace.
          if (!recorded && Refuse(tenantId, prefix, now) is { } refusal)
          {
            return refusal;
          }

          if (
            _gate.TryStartCreation(
              tenantId,
              () => Start(tenantId, prefix, counted: !recorded),
              out creation,
              out removal
            ) && !recorded
          )
          {
            _prefixRateLimits[prefix.Prefix].Take(now);
            _rateLimit.Take(now);
            _creatingNew[prefix.Prefix] = _creatingNew.GetValueOrDefault(prefix.Prefix) + 1;
          }
        }
      }

      // The creation this request started, or one that started since the lookup.
      if (creation is not null)
      {
        return await Answer(creation, cancellationToken);
      }

      LogWaitsForDelete(_logger, tenantId);
      await removal!.WaitAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Deletes the tenant's renderer and record at once, without a drain, after the creation in
  /// flight for it, if any, has finished; requests for the tenant meanwhile wait for the delete. A
  /// tenant with a disabled sandbox is refused, and nothing is deleted.
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

    // Before the tenant's gate, so that a delete waiting its turn holds up no creation.
    await _deletes.WaitAsync(cancellationToken);
    using var turn = new Releaser(_deletes);
    // Otherwise the creation could write its record after the delete, or lose its sandbox to it.
    using var hold = await _gate.HoldAsync(
      tenantId,
      () => LogDeleteWaits(_logger, tenantId),
      cancellationToken
    );
    try
    {
      await _provisioner.DeleteAsync(
        tenantId,
        TimeSpan.Zero,
        refuseDisabled: true,
        listed: null,
        cancellationToken
      );
      _census.Deleted(tenantId);
      LogDeleted(_logger, tenantId);
      return new RendererOutcome(RendererAnswer.Deleted);
    }
    catch (RendererDisabledException exception)
    {
      LogDeleteRefused(_logger, tenantId, exception.Message);
      return new RendererOutcome(RendererAnswer.Disabled);
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

  public void Dispose() => _deletes.Dispose();

  /// <summary>Releases a turn of <see cref="MaxConcurrentDeletes"/> once.</summary>
  private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
  {
    public void Dispose() => semaphore.Release();
  }

  /// <summary>Waits until no creation is in flight, as after stopping, when each cleans up.</summary>
  public async Task WhenIdleAsync() =>
    await Task.WhenAll(_gate.Creations()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

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

  /// <summary>
  /// Refuses a new tenant whose prefix has its most renderers, counting the creations in flight
  /// for new tenants, or when the prefix's or the service's creates per minute are used up; a
  /// create refused by either budget takes from neither. <see langword="null"/> when the create
  /// may start. Called under the lock.
  /// </summary>
  private RendererOutcome? Refuse(string tenantId, ManagedTenantPrefix prefix, long now)
  {
    if (
      _census.CountUnder(prefix.Prefix, except: tenantId)
        + _creatingNew.GetValueOrDefault(prefix.Prefix)
      >= prefix.MaxTenants
    )
    {
      LogQuotaExceeded(_logger, tenantId, prefix.Prefix, prefix.MaxTenants);
      return new RendererOutcome(RendererAnswer.QuotaExceeded);
    }

    var prefixHasRoom = _prefixRateLimits[prefix.Prefix].HasRoom(now, out var prefixRetryAfter);
    var serviceHasRoom = _rateLimit.HasRoom(now, out var serviceRetryAfter);
    if (prefixHasRoom && serviceHasRoom)
    {
      return null;
    }

    if (!prefixHasRoom)
    {
      LogRateLimited(_logger, tenantId, $"prefix {prefix.Prefix}", prefix.MaxCreatesPerMinute);
    }

    if (!serviceHasRoom)
    {
      LogRateLimited(_logger, tenantId, "the service", _rateLimit.PerMinute);
    }

    return new RendererOutcome(
      RendererAnswer.RateLimited,
      prefixRetryAfter > serviceRetryAfter ? prefixRetryAfter : serviceRetryAfter
    );
  }

  /// <summary>Starts creating the tenant's renderer; the gate calls it under its lock.</summary>
  /// <param name="tenantId">The tenant.</param>
  /// <param name="prefix">The tenant's prefix.</param>
  /// <param name="counted">Whether the creation counts toward the prefix's quota while in flight.</param>
  private Task<EnsureResult> Start(string tenantId, ManagedTenantPrefix prefix, bool counted)
  {
    var size = prefix.Size.Length > 0 ? RendererSize.Parse(prefix.Size) : _options.RendererSize;
    // Without the starting request's execution context: the creation is everyone's, so neither
    // that request's logging scopes nor its trace may follow it.
    using (ExecutionContext.SuppressFlow())
    {
      return Task.Run(
        () => CreateAsync(tenantId, prefix.Prefix, size, counted),
        CancellationToken.None
      );
    }
  }

  private async Task<EnsureResult> CreateAsync(
    string tenantId,
    string prefix,
    RendererSize size,
    bool counted
  )
  {
    try
    {
      var result = await _provisioner.EnsureAsync(tenantId, size, _options.DiskImageId, _stopping);
      // The census counts the record from now on, and the creation stops counting as in flight, in
      // one step under the lock the quota is checked under: otherwise a check in between would
      // count the tenant twice, and refuse a new tenant while its prefix still has room.
      lock (_lock)
      {
        _census.Recorded(tenantId);
        if (counted)
        {
          _creatingNew[prefix]--;
          counted = false;
        }
      }

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
      // A creation that failed or was canceled stops counting as in flight; a record it may still
      // have written counts from the census's next listing.
      if (counted)
      {
        lock (_lock)
        {
          _creatingNew[prefix]--;
        }
      }

      _gate.EndCreation(tenantId);
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
    Message = "Refused tenant {TenantId} for now: {Limited} created its most renderers ({MaxCreatesPerMinute}) in the last minute."
  )]
  private static partial void LogRateLimited(
    ILogger logger,
    string tenantId,
    string limited,
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

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Debug,
    Message = "Tenant {TenantId} waits for the delete of its renderer in flight."
  )]
  private static partial void LogWaitsForDelete(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Debug,
    Message = "The delete of tenant {TenantId} waits for the creation of its renderer in flight."
  )]
  private static partial void LogDeleteWaits(ILogger logger, string tenantId);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Warning,
    Message = "Refused to delete the renderer of tenant {TenantId}: {Reason}"
  )]
  private static partial void LogDeleteRefused(ILogger logger, string tenantId, string reason);
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

  /// <summary><c>409</c>: a sandbox of the tenant is disabled, so nothing was deleted.</summary>
  Disabled,

  /// <summary><c>503</c>: something failed, and was logged.</summary>
  Failed,
}

/// <param name="Answer">The answer.</param>
/// <param name="RetryAfter">For <see cref="RendererAnswer.RateLimited"/>: when a create may start.</param>
internal readonly record struct RendererOutcome(
  RendererAnswer Answer,
  TimeSpan RetryAfter = default
);
