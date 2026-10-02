namespace Atli.Reports.Server.Security;

/// <summary>
/// In-flight limits for this replica only. Entries disappear with the last request; the global cap
/// also bounds the number of retained caller identities. No tenant/header can create a partition.
/// </summary>
internal sealed class CallerAdmission(ReportsSecurityOptions settings)
{
  private readonly object _gate = new();
  private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
  private int _total;

  public AdmissionResult TryAcquire(string partition, int callerLimit, out IDisposable? lease)
  {
    lock (_gate)
    {
      lease = null;
      var count = _active.GetValueOrDefault(partition);
      if (count >= callerLimit)
      {
        return AdmissionResult.CallerBusy;
      }
      if (_total >= settings.MaxConcurrentRequests)
      {
        return AdmissionResult.ServerBusy;
      }

      _active[partition] = count + 1;
      _total++;
      lease = new Lease(this, partition);
      return AdmissionResult.Accepted;
    }
  }

  private void Release(string partition)
  {
    lock (_gate)
    {
      var count = _active[partition];
      if (count == 1)
      {
        _active.Remove(partition);
      }
      else
      {
        _active[partition] = count - 1;
      }
      _total--;
    }
  }

  private sealed class Lease(CallerAdmission owner, string partition) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) == 0)
      {
        owner.Release(partition);
      }
    }
  }
}

internal enum AdmissionResult
{
  Accepted,
  CallerBusy,
  ServerBusy,
}
