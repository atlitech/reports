using Atli.Reports.Hosting.Renderers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// Which tenants under the managed prefixes have a record, and how many under each prefix, for
/// <c>MaxTenants</c>: the record store's last listing that succeeded
/// (<see cref="IRendererRecordStore.ListTenantIdsAsync"/>, names only), with the records the
/// service itself wrote or deleted since that listing started on top. Others' changes (a
/// <c>delete</c> or <c>create</c> from the command line, another replica) count from the next
/// listing.
/// </summary>
/// <remarks>
/// The store is listed in the background, at the start and then <see cref="RefreshInterval"/>
/// after each listing ends, so a listing that takes long, such as Key Vault's at thousands of
/// records, never holds up a request or a readiness probe. Only a request that needs a count
/// before the first listing has ended waits for it.
/// </remarks>
internal sealed partial class TenantCensus(
  IRendererRecordStore records,
  ProvisioningServiceOptions service,
  TimeProvider time,
  ILogger<TenantCensus> logger
) : BackgroundService
{
  /// <summary>How long after a listing ends the next one starts.</summary>
  public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long the last listing that succeeded stays current, on top of twice the time it took: a
  /// few listings may fail in a row before the census is out of date.
  /// </summary>
  public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

  private readonly Lock _lock = new();
  private readonly HashSet<string> _tenants = new(StringComparer.Ordinal);
  private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

  // By tenant: whether the service last wrote (true) or deleted (false) its record, and when.
  private readonly Dictionary<string, (bool Exists, long At)> _changes = new(
    StringComparer.Ordinal
  );

  private readonly TaskCompletionSource _firstListing = new(
    TaskCreationOptions.RunContinuationsAsynchronously
  );

  private long? _listedAt;
  private TimeSpan _listingTook;
  private Exception? _lastFailure;

  /// <summary>Completes when the first listing has ended, whether it succeeded or not.</summary>
  public Task FirstListing => _firstListing.Task;

  /// <summary>Whether a listing has ever succeeded, so that prefixes' tenants can be counted.</summary>
  public bool HasListed
  {
    get
    {
      lock (_lock)
      {
        return _listedAt is not null;
      }
    }
  }

  /// <summary>
  /// Whether the last listing that succeeded ended less than <see cref="MaxAge"/>, plus twice the
  /// time it took, ago.
  /// </summary>
  public bool IsCurrent
  {
    get
    {
      lock (_lock)
      {
        return IsCurrentUnderLock();
      }
    }
  }

  /// <summary>Why the last listing failed, or <see langword="null"/> when it succeeded.</summary>
  public Exception? LastFailure
  {
    get
    {
      lock (_lock)
      {
        return _lastFailure;
      }
    }
  }

  /// <summary>
  /// Whether the tenant has a record, as far as the census knows; <see langword="null"/> while it
  /// is not <see cref="IsCurrent"/>. A record written elsewhere since the last listing started is
  /// not known until the next.
  /// </summary>
  public bool? HasRecord(string tenantId)
  {
    lock (_lock)
    {
      return IsCurrentUnderLock() ? _tenants.Contains(tenantId) : null;
    }
  }

  /// <summary>
  /// How many tenants under <paramref name="prefix"/>, a managed prefix, other than
  /// <paramref name="except"/> have a record, as far as the census knows; call once
  /// <see cref="HasListed"/>.
  /// </summary>
  public int CountUnder(string prefix, string except)
  {
    lock (_lock)
    {
      return _counts.GetValueOrDefault(prefix) - (_tenants.Contains(except) ? 1 : 0);
    }
  }

  /// <summary>The service wrote, or found, the tenant's record.</summary>
  public void Recorded(string tenantId) => Change(tenantId, exists: true);

  /// <summary>The service deleted the tenant's record, or found none.</summary>
  public void Deleted(string tenantId) => Change(tenantId, exists: false);

  /// <summary>
  /// Lists the store once, and takes the listing if it succeeds. Never throws but when
  /// <paramref name="cancellationToken"/> stops it.
  /// </summary>
  internal async Task ListAsync(CancellationToken cancellationToken)
  {
    var startedAt = time.GetTimestamp();
    try
    {
      var tenantIds = await records.ListTenantIdsAsync(cancellationToken);
      var endedAt = time.GetTimestamp();
      var took = time.GetElapsedTime(startedAt, endedAt);
      int count;
      lock (_lock)
      {
        _tenants.Clear();
        foreach (var tenantId in tenantIds)
        {
          if (service.PrefixOf(tenantId) is not null)
          {
            _tenants.Add(tenantId);
          }
        }

        // A change made before the listing started is in it; a later one may not be.
        foreach (var (tenantId, change) in _changes.ToArray())
        {
          if (change.At < startedAt)
          {
            _changes.Remove(tenantId);
          }
          else if (change.Exists)
          {
            _tenants.Add(tenantId);
          }
          else
          {
            _tenants.Remove(tenantId);
          }
        }

        _counts.Clear();
        foreach (var tenantId in _tenants)
        {
          var prefix = service.PrefixOf(tenantId)!.Prefix;
          _counts[prefix] = _counts.GetValueOrDefault(prefix) + 1;
        }

        _listedAt = endedAt;
        _listingTook = took;
        _lastFailure = null;
        count = _tenants.Count;
      }

      LogListed(logger, count, took);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      lock (_lock)
      {
        _lastFailure = exception;
      }

      LogListingFailed(logger, exception, RefreshInterval);
    }
    finally
    {
      _firstListing.TrySetResult();
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (true)
    {
      await ListAsync(stoppingToken);
      await Task.Delay(RefreshInterval, time, stoppingToken);
    }
  }

  private bool IsCurrentUnderLock() =>
    _listedAt is { } listedAt && time.GetElapsedTime(listedAt) < MaxAge + 2 * _listingTook;

  private void Change(string tenantId, bool exists)
  {
    if (service.PrefixOf(tenantId) is not { } prefix)
    {
      return;
    }

    lock (_lock)
    {
      _changes[tenantId] = (exists, time.GetTimestamp());
      if (exists ? _tenants.Add(tenantId) : _tenants.Remove(tenantId))
      {
        _counts[prefix.Prefix] = _counts.GetValueOrDefault(prefix.Prefix) + (exists ? 1 : -1);
      }
    }
  }

  [LoggerMessage(
    EventId = 40,
    Level = LogLevel.Debug,
    Message = "Listed the record store: {Count} tenants under the managed prefixes have a record ({Elapsed})."
  )]
  private static partial void LogListed(ILogger logger, int count, TimeSpan elapsed);

  [LoggerMessage(
    EventId = 41,
    Level = LogLevel.Warning,
    Message = "Could not list the record store's tenants; the next attempt is in {Interval}."
  )]
  private static partial void LogListingFailed(
    ILogger logger,
    Exception exception,
    TimeSpan interval
  );
}

/// <summary>
/// Readiness: whether the record store answered a listing lately, as <see cref="TenantCensus.IsCurrent"/>
/// says. It never waits for a listing in progress.
/// </summary>
internal sealed class RecordStoreHealthCheck(TenantCensus census) : IHealthCheck
{
  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  ) =>
    Task.FromResult(
      census.IsCurrent
        ? HealthCheckResult.Healthy("The renderer record store answered lately.")
        : HealthCheckResult.Unhealthy(
          "The renderer record store has not answered lately.",
          census.LastFailure
        )
    );
}
