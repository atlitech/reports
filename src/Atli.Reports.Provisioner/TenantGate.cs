namespace Atli.Reports.Provisioner;

/// <summary>
/// Keeps a tenant's renderer from being created and removed at the same time within one process. A
/// creation is the provisioning service's, shared by the requests for the tenant; a removal is a
/// delete, a retirement, or a prune, each of which deletes the tenant's record or sandboxes.
/// </summary>
/// <remarks>
/// <para>
/// A delete (<see cref="HoldAsync"/>) takes the tenant at once, so creations asked for from then on
/// wait for it and then start afresh; it first waits for the creation already in flight, which could
/// otherwise write its record after the delete, or lose its sandbox to it. A retirement or prune
/// (<see cref="TryHold"/>) takes the tenant only when nothing is in flight for it, and leaves it
/// for the next run otherwise.
/// </para>
/// <para>
/// One gate covers one process. Commands in other processes, from the command line or another
/// replica, are kept apart by running one at a time, and by the record check every command makes
/// before it writes a record.
/// </para>
/// </remarks>
internal sealed class TenantGate
{
  private readonly Lock _lock = new();
  private readonly Dictionary<string, Task<EnsureResult>> _creating = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Task> _removing = new(StringComparer.Ordinal);

  /// <summary>
  /// What is in flight for the tenant: the creation to join, or the removal to wait for before
  /// starting one; both <see langword="null"/> when nothing is.
  /// </summary>
  public (Task<EnsureResult>? Creation, Task? Removal) InFlight(string tenantId)
  {
    lock (_lock)
    {
      return (_creating.GetValueOrDefault(tenantId), _removing.GetValueOrDefault(tenantId));
    }
  }

  /// <summary>
  /// Starts the tenant's creation with <paramref name="start"/> and returns <see langword="true"/>,
  /// unless a creation or a removal is in flight for it: then returns <see langword="false"/> with
  /// the one in flight. The creation calls <see cref="EndCreation"/> once it is over, success or not,
  /// before its task completes.
  /// </summary>
  /// <param name="start">Starts the creation without running it on the caller's thread.</param>
  public bool TryStartCreation(
    string tenantId,
    Func<Task<EnsureResult>> start,
    out Task<EnsureResult>? creation,
    out Task? removal
  )
  {
    ArgumentNullException.ThrowIfNull(start);
    lock (_lock)
    {
      creation = _creating.GetValueOrDefault(tenantId);
      removal = _removing.GetValueOrDefault(tenantId);
      if (creation is not null || removal is not null)
      {
        return false;
      }

      // Under the lock: a creation that ends at once waits here to take itself off the books.
      creation = start();
      _creating[tenantId] = creation;
      return true;
    }
  }

  /// <summary>Takes the tenant's creation off the books; the creation calls it as it ends.</summary>
  public void EndCreation(string tenantId)
  {
    lock (_lock)
    {
      _creating.Remove(tenantId);
    }
  }

  /// <summary>How many creations are in flight for tenants that <paramref name="counts"/> picks.</summary>
  public int CountCreating(Func<string, bool> counts)
  {
    ArgumentNullException.ThrowIfNull(counts);
    lock (_lock)
    {
      return _creating.Keys.Count(counts);
    }
  }

  /// <summary>The tenants whose creations are in flight.</summary>
  public IReadOnlyList<string> Creating()
  {
    lock (_lock)
    {
      return [.. _creating.Keys];
    }
  }

  /// <summary>Every creation in flight, as after stopping, when each cleans up.</summary>
  public IReadOnlyList<Task> Creations()
  {
    lock (_lock)
    {
      return [.. _creating.Values];
    }
  }

  /// <summary>
  /// Takes the tenant for a delete, until the returned hold is disposed: creations started from now
  /// on wait for it. Waits first for another removal in flight, and for the creation in flight,
  /// whose failure is its own.
  /// </summary>
  /// <param name="tenantId">The tenant.</param>
  /// <param name="waiting">Called when the hold waits for a creation in flight.</param>
  /// <param name="cancellationToken">Gives up the wait, and the hold with it.</param>
  public async Task<IDisposable> HoldAsync(
    string tenantId,
    Action? waiting,
    CancellationToken cancellationToken
  )
  {
    while (true)
    {
      Task? removal;
      // Its result is the requests' to see; a delete only waits for it to end.
      Task? creation = null;
      Hold? hold = null;
      lock (_lock)
      {
        removal = _removing.GetValueOrDefault(tenantId);
        if (removal is null)
        {
          hold = new Hold(this, tenantId);
          _removing[tenantId] = hold.Released;
          creation = _creating.GetValueOrDefault(tenantId);
        }
      }

      if (hold is null)
      {
        await removal!.WaitAsync(cancellationToken);
        continue;
      }

      if (creation is not null)
      {
        waiting?.Invoke();
        await creation
          .WaitAsync(cancellationToken)
          .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (cancellationToken.IsCancellationRequested)
        {
          hold.Dispose();
          cancellationToken.ThrowIfCancellationRequested();
        }
      }

      return hold;
    }
  }

  /// <summary>
  /// Takes the tenant for a retirement or a prune, until the returned hold is disposed, or returns
  /// <see langword="null"/> when a creation or a removal is in flight for it.
  /// </summary>
  public IDisposable? TryHold(string tenantId)
  {
    lock (_lock)
    {
      if (_creating.ContainsKey(tenantId) || _removing.ContainsKey(tenantId))
      {
        return null;
      }

      Hold hold = new(this, tenantId);
      _removing[tenantId] = hold.Released;
      return hold;
    }
  }

  private void Release(string tenantId, Task released)
  {
    lock (_lock)
    {
      if (_removing.GetValueOrDefault(tenantId) == released)
      {
        _removing.Remove(tenantId);
      }
    }
  }

  /// <summary>A removal's hold on a tenant; disposing it lets the creations waiting for it start.</summary>
  private sealed class Hold(TenantGate gate, string tenantId) : IDisposable
  {
    private readonly TaskCompletionSource _released = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task Released => _released.Task;

    public void Dispose()
    {
      gate.Release(tenantId, _released.Task);
      _released.TrySetResult();
    }
  }
}
