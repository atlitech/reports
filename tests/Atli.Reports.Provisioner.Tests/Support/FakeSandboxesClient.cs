using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// A sandbox group's data plane in memory. Created sandboxes are numbered (<c>sandbox-1</c>, …),
/// run at once, and are created at the clock's now; a port's URL is
/// <c>https://{id}-{port}.example.test/</c>. Stopping, disabling, and enabling set the stop's reason
/// and time as the data plane does.
/// </summary>
internal sealed class FakeSandboxesClient(Journal journal, TimeProvider clock) : ISandboxesClient
{
  private readonly Lock _lock = new();
  private readonly Dictionary<string, SandboxView> _sandboxes = new(StringComparer.Ordinal);
  private readonly List<SandboxSpec> _created = [];
  private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
  private int _count;

  /// <summary>Returns the exception to fail adding a port to a sandbox with, or <see langword="null"/>.</summary>
  public Func<SandboxView, Exception?>? FailAddPort { get; set; }

  /// <summary>
  /// Returns the exception to fail a create with after it made the sandbox, as a create whose
  /// answer was lost, or <see langword="null"/>.
  /// </summary>
  public Func<SandboxSpec, Exception?>? FailCreateAfterCreating { get; set; }

  /// <summary>
  /// Runs after each list is taken, before it is returned, when set: something that happens between
  /// a command's scan and what the command does next, such as a request waking a renderer.
  /// </summary>
  public Func<Task>? AfterList { get; set; }

  /// <summary>Sandboxes whose deletion fails.</summary>
  public HashSet<string> FailDelete { get; } = new(StringComparer.Ordinal);

  /// <summary>Sandboxes that cannot be disabled.</summary>
  public HashSet<string> FailDisable { get; } = new(StringComparer.Ordinal);

  /// <summary>The IDs of the disabled sandboxes.</summary>
  public IReadOnlyList<string> Disabled
  {
    get
    {
      lock (_lock)
      {
        return [.. _disabled.Order(StringComparer.Ordinal)];
      }
    }
  }

  /// <summary>What each create asked for, in order.</summary>
  public IReadOnlyList<SandboxSpec> Created
  {
    get
    {
      lock (_lock)
      {
        return [.. _created];
      }
    }
  }

  /// <summary>The IDs of the sandboxes that exist.</summary>
  public IReadOnlyList<string> Ids
  {
    get
    {
      lock (_lock)
      {
        return [.. _sandboxes.Keys.Order(StringComparer.Ordinal)];
      }
    }
  }

  /// <summary>
  /// Adds a sandbox that exists before the test runs, created <paramref name="age"/> ago (a week
  /// by default).
  /// </summary>
  public SandboxView Add(
    string id,
    IReadOnlyDictionary<string, string> labels,
    string state = SandboxStates.Running,
    TimeSpan? age = null
  )
  {
    SandboxView sandbox = new()
    {
      Id = id,
      State = state,
      Labels = labels,
      CreatedAt = clock.GetUtcNow() - (age ?? TimeSpan.FromDays(7)),
    };
    lock (_lock)
    {
      _sandboxes[id] = sandbox;
    }

    return sandbox;
  }

  /// <summary>
  /// Stops a sandbox behind the provisioner's back, as its auto-suspend does: <paramref name="ago"/>
  /// before the clock's now, or at a time the data plane does not report when <see langword="null"/>.
  /// </summary>
  public SandboxView Suspend(
    string id,
    TimeSpan? ago,
    string reason = SandboxStoppedReasons.Idle
  ) =>
    Update(
      id,
      sandbox =>
        sandbox with
        {
          State = SandboxStates.Stopped,
          StoppedReason = reason,
          StoppedAt = clock.GetUtcNow() - ago,
        }
    );

  /// <summary>
  /// Starts a sandbox behind the provisioner's back, as a request to its on-demand port does. The
  /// stop's reason and time stay, as the data plane keeps them.
  /// </summary>
  public SandboxView Wake(string id) =>
    Update(id, sandbox => sandbox with { State = SandboxStates.Running });

  /// <summary>Removes a sandbox behind the provisioner's back.</summary>
  public void Remove(string id)
  {
    lock (_lock)
    {
      _sandboxes.Remove(id);
    }
  }

  public Task<SandboxView> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    lock (_lock)
    {
      var id = $"sandbox-{++_count}";
      _created.Add(spec);
      SandboxView sandbox = new()
      {
        Id = id,
        State = SandboxStates.Running,
        Labels = spec.Labels,
        CreatedAt = clock.GetUtcNow(),
      };
      _sandboxes[id] = sandbox;
      journal.Add($"create {id} ({spec.Labels.GetValueOrDefault("tenant")})");
      if (FailCreateAfterCreating?.Invoke(spec) is { } failure)
      {
        throw failure;
      }

      return Task.FromResult(sandbox);
    }
  }

  public Task<SandboxView?> GetAsync(string sandboxId, CancellationToken cancellationToken)
  {
    lock (_lock)
    {
      return Task.FromResult(_sandboxes.GetValueOrDefault(sandboxId));
    }
  }

  public async Task<IReadOnlyList<SandboxView>> ListAsync(CancellationToken cancellationToken)
  {
    IReadOnlyList<SandboxView> sandboxes;
    lock (_lock)
    {
      sandboxes = [.. _sandboxes.Values];
    }

    if (AfterList is { } afterList)
    {
      await afterList();
    }

    return sandboxes;
  }

  public Task DeleteAsync(string sandboxId, CancellationToken cancellationToken)
  {
    journal.Add($"delete {sandboxId}");
    if (FailDelete.Contains(sandboxId))
    {
      throw new SandboxesException($"Deleting {sandboxId} failed.");
    }

    Remove(sandboxId);
    return Task.CompletedTask;
  }

  public Task<SandboxView> StopAsync(string sandboxId, CancellationToken cancellationToken)
  {
    journal.Add($"stop {sandboxId}");
    return Task.FromResult(Stop(sandboxId, SandboxStoppedReasons.UserStopped));
  }

  public Task<SandboxView> ResumeAsync(string sandboxId, CancellationToken cancellationToken)
  {
    journal.Add($"resume {sandboxId}");
    return Task.FromResult(
      Update(sandboxId, sandbox => sandbox with { State = SandboxStates.Running })
    );
  }

  public Task<SandboxView> AddPortAsync(
    string sandboxId,
    int port,
    SandboxPortOptions options,
    CancellationToken cancellationToken
  )
  {
    var anonymous = options.Anonymous;
    var activation = options.Activation;
    journal.Add(
      $"port {sandboxId} {port}{(anonymous ? " anonymous" : "")} {activation}"
        + (
          options.AllowedSourceCidrs.Count == 0
            ? ""
            : " from " + string.Join(",", options.AllowedSourceCidrs)
        )
    );
    lock (_lock)
    {
      if (FailAddPort?.Invoke(_sandboxes[sandboxId]) is { } failure)
      {
        throw failure;
      }
    }

    return Task.FromResult(
      Update(
        sandboxId,
        sandbox =>
          sandbox with
          {
            Ports =
            [
              .. sandbox.Ports,
              new SandboxPort(port, new Uri($"https://{sandboxId}-{port}.example.test/"), anonymous)
              {
                Activation = activation,
                AllowedSourceCidrs = options.AllowedSourceCidrs,
              },
            ],
          }
      )
    );
  }

  public Task<SandboxView> DisableAsync(string sandboxId, CancellationToken cancellationToken)
  {
    journal.Add($"disable {sandboxId}");
    if (FailDisable.Contains(sandboxId))
    {
      throw new SandboxesException($"Disabling {sandboxId} failed.");
    }

    lock (_lock)
    {
      _disabled.Add(sandboxId);
    }

    return Task.FromResult(Stop(sandboxId, SandboxStoppedReasons.Disabled));
  }

  public Task<SandboxView> EnableAsync(string sandboxId, CancellationToken cancellationToken)
  {
    journal.Add($"enable {sandboxId}");
    lock (_lock)
    {
      _disabled.Remove(sandboxId);
    }

    // Still stopped; it reads as stopped by the user from now on.
    return Task.FromResult(
      Update(
        sandboxId,
        sandbox =>
          sandbox.IsDisabled
            ? sandbox with
            {
              StoppedReason = SandboxStoppedReasons.UserStopped,
            }
            : sandbox
      )
    );
  }

  /// <summary>Stops a sandbox now for <paramref name="reason"/>; a stopped one keeps its stop time.</summary>
  private SandboxView Stop(string sandboxId, string reason) =>
    Update(
      sandboxId,
      sandbox =>
        sandbox with
        {
          State = SandboxStates.Stopped,
          StoppedReason = reason,
          StoppedAt =
            sandbox.State == SandboxStates.Stopped ? sandbox.StoppedAt : clock.GetUtcNow(),
        }
    );

  private SandboxView Update(string sandboxId, Func<SandboxView, SandboxView> update)
  {
    lock (_lock)
    {
      var sandbox = update(_sandboxes[sandboxId]);
      _sandboxes[sandboxId] = sandbox;
      return sandbox;
    }
  }
}
