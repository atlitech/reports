using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Provisioner;

/// <summary>
/// Creates, replaces, and deletes per-customer renderers, and keeps the records the gateway routes
/// by in step with them. Every renderer is a sandbox of its own, created from the server's disk
/// image with a credential of its own. None is ever started from another renderer's snapshot, which
/// would share that renderer's memory layout and environment (docs/hosted-renderers.md).
/// </summary>
/// <remarks>
/// <para>
/// Progress goes to the output, prefixed with the tenant. It names tenants, sandboxes, URLs, and disk
/// images; credentials and verifiers never reach it.
/// </para>
/// <para>
/// Only one command should run per sandbox group at a time. Each command still checks, just before
/// it writes a tenant's record, that the record is what the command read, so a concurrent command
/// costs at most a discarded replacement, not a record pointing at a deleted sandbox.
/// </para>
/// </remarks>
internal sealed class RendererProvisioner
{
  /// <summary>The port the server image listens on.</summary>
  public const int RendererPort = 8080;

  /// <summary>How often a new renderer is asked whether it is ready.</summary>
  public static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

  /// <summary>
  /// How long cleaning up after a failed or canceled launch may take: finding and deleting the
  /// sandbox it made, or reading the record back after a failed write. Cleanup runs even when the
  /// command was canceled, so it has a bound of its own.
  /// </summary>
  public static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);

  private readonly ISandboxesClient _sandboxes;
  private readonly IRendererRecordStore _records;
  private readonly IReadinessProbe _readiness;
  private readonly TimeProvider _time;
  private readonly TextWriter _output;
  private readonly ProvisionerOptions _options;
  private readonly TenantGate _gate;

  /// <param name="sandboxes">The renderer sandbox group's data plane.</param>
  /// <param name="records">The records the gateway routes by.</param>
  /// <param name="readiness">Asks new renderers whether they are ready.</param>
  /// <param name="time">The clock of the drains, the readiness polls, and idle times.</param>
  /// <param name="output">Where progress goes.</param>
  /// <param name="options">The provisioner's settings.</param>
  /// <param name="gate">
  /// The process's gate, which retirements and prunes take each tenant through; the provisioning
  /// service shares its own, and a command makes one of its own when <see langword="null"/>.
  /// </param>
  public RendererProvisioner(
    ISandboxesClient sandboxes,
    IRendererRecordStore records,
    IReadinessProbe readiness,
    TimeProvider time,
    TextWriter output,
    ProvisionerOptions options,
    TenantGate? gate = null
  )
  {
    _sandboxes = sandboxes;
    _records = records;
    _readiness = readiness;
    _time = time;
    // A rollout replaces several tenants' renderers at once.
    _output = TextWriter.Synchronized(output);
    _options = options;
    _gate = gate ?? new TenantGate();
  }

  /// <summary>
  /// Creates a renderer for a tenant that has none, and its record. A tenant with a record is
  /// refused: <see cref="RolloutAsync"/> replaces renderers.
  /// </summary>
  public async Task<RendererRecord> CreateAsync(
    string tenantId,
    RendererSize size,
    string diskImageId,
    CancellationToken cancellationToken
  )
  {
    RendererRecord? existing;
    try
    {
      existing = await _records.GetAsync(tenantId, cancellationToken);
    }
    catch (InvalidDataException exception)
    {
      throw new ProvisioningException(
        $"Tenant {tenantId} has a record that cannot be read ({exception.Message}). "
          + "Delete the tenant first.",
        exception
      );
    }

    if (existing is not null)
    {
      throw new ProvisioningException(
        $"Tenant {tenantId} already has a renderer (sandbox {existing.SandboxId ?? "none"}). "
          + "Replace it with rollout, or delete it first."
      );
    }

    var (record, sandbox) = await LaunchAsync(
      tenantId,
      size,
      diskImageId,
      replacing: null,
      NewLaunchId(),
      cancellationToken
    );
    _output.WriteLine($"Tenant:      {tenantId}");
    _output.WriteLine($"Sandbox:     {sandbox.Id}");
    _output.WriteLine($"URL:         {record.Url}");
    _output.WriteLine($"State:       {sandbox.State}");
    _output.WriteLine($"Size:        {size.Name}");
    _output.WriteLine($"Disk image:  {diskImageId}");
    return record;
  }

  /// <summary>
  /// Makes sure <paramref name="tenantId"/> has a ready renderer and record, for the provisioning
  /// service: returns the existing record when it names a sandbox that exists (disabled or not), and
  /// otherwise creates a renderer as <see cref="CreateAsync"/> does, or replaces the record's missing
  /// sandbox as a rollout does. A create that another command or replica finished first counts as
  /// found, not failed. A record that cannot be read, or that names a sandbox labeled for another
  /// tenant, is a <see cref="ProvisioningException"/>.
  /// </summary>
  public async Task<EnsureResult> EnsureAsync(
    string tenantId,
    RendererSize size,
    string diskImageId,
    CancellationToken cancellationToken
  )
  {
    var (record, found) = await LookUpAsync(tenantId, cancellationToken);
    if (found)
    {
      return new EnsureResult(record!, Created: false);
    }

    if (record is not null)
    {
      Log(tenantId, $"Sandbox {record.SandboxId} of the record no longer exists; replacing it.");
    }

    var launchId = NewLaunchId();
    try
    {
      // A missing sandbox is replaced as a rollout replaces one, but there is nothing to retire.
      var (created, _) = await LaunchAsync(
        tenantId,
        size,
        diskImageId,
        replacing: record,
        launchId,
        cancellationToken
      );
      Log(tenantId, $"The record now points to sandbox {created.SandboxId}.");
      return new EnsureResult(created, Created: true);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      // The launch deleted its own sandbox, unless it could not tell whether its record write took
      // effect. If the record points to a renderer now, that renderer is the answer: another
      // command's or replica's (the record's compare-and-swap is what discarded this launch), or
      // this launch's own after all.
      if (await LookUpWinnerAsync(tenantId, record, cancellationToken) is var (winner, sandbox))
      {
        if (sandbox is not null && RendererLabels.IsFromLaunch(sandbox, launchId))
        {
          Log(tenantId, $"The record points to this launch's sandbox {sandbox.Id} after all.");
          return new EnsureResult(winner, Created: true);
        }

        Log(
          tenantId,
          $"Another command or replica gave the tenant sandbox {winner.SandboxId} meanwhile; "
            + "this launch was discarded."
        );
        return new EnsureResult(winner, Created: false);
      }

      throw;
    }
  }

  /// <summary>
  /// Reads the tenant's record and, when it names a sandbox, whether that sandbox exists: a
  /// data-plane read that does not answer <c>404</c>. <c>Record</c> is the record, or
  /// <see langword="null"/> when the tenant has none. <c>Found</c> is whether
  /// <see cref="EnsureAsync"/> would return it as found: it names a sandbox that exists (disabled or
  /// not), or no sandbox at all, as a renderer that is not a sandbox; not when there is no record,
  /// or its sandbox is gone. Throws <see cref="ProvisioningException"/> for a record that cannot be
  /// read, or that names a sandbox labeled for another tenant.
  /// </summary>
  public async Task<(RendererRecord? Record, bool Found)> LookUpAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    var (record, sandbox) = await ReadRendererAsync(tenantId, cancellationToken);
    return (record, record is not null && (record.SandboxId is null || sandbox is not null));
  }

  /// <summary>
  /// Reads the tenant's record and the sandbox it names, <see langword="null"/> when there is no
  /// record, or it names no sandbox or one that is gone. Throws <see cref="ProvisioningException"/>
  /// for a record that cannot be read, and for one that names a sandbox labeled for another tenant
  /// or for none: a record is never trusted to route to a sandbox that is not the tenant's own.
  /// </summary>
  private async Task<(RendererRecord? Record, SandboxView? Sandbox)> ReadRendererAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    RendererRecord? record;
    try
    {
      record = await _records.GetAsync(tenantId, cancellationToken);
    }
    catch (InvalidDataException exception)
    {
      throw new ProvisioningException(
        $"Tenant {tenantId} has a record that cannot be read ({exception.Message}); fix it, or "
          + "delete the tenant.",
        exception
      );
    }

    if (record?.SandboxId is not { } sandboxId)
    {
      return (record, null);
    }

    var sandbox = await _sandboxes.GetAsync(sandboxId, cancellationToken);
    if (sandbox is not null && RendererLabels.TenantOf(sandbox) is var owner && owner != tenantId)
    {
      throw new ProvisioningException(
        $"The record of tenant {tenantId} names sandbox {sandboxId}, which is labeled for "
          + $"{(owner is null ? "no tenant" : "tenant " + owner)}; fix the record, or delete the "
          + "tenant."
      );
    }

    return (record, sandbox);
  }

  /// <summary>
  /// After a launch for <see cref="EnsureAsync"/> failed: the renderer recorded for the tenant now,
  /// with its sandbox, or <see langword="null"/> when there is none, its sandbox is gone, it is still
  /// <paramref name="read"/>, the record the launch meant to replace, or it cannot be read.
  /// </summary>
  private async Task<(RendererRecord Record, SandboxView? Sandbox)?> LookUpWinnerAsync(
    string tenantId,
    RendererRecord? read,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var (current, sandbox) = await ReadRendererAsync(tenantId, cancellationToken);
      return
        current is not null
        && (current.SandboxId is null || sandbox is not null)
        && (
          read is null
          || !string.Equals(current.SandboxId, read.SandboxId, StringComparison.Ordinal)
        )
        ? (current, sandbox)
        : null;
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      // The launch's own failure is the one to report.
      return null;
    }
  }

  /// <summary>
  /// Replaces every renderer, or <paramref name="tenantId"/>'s, that runs another disk image than
  /// <paramref name="diskImageId"/> or whose sandbox no longer exists, suspended ones included. Each
  /// replacement is created as <see cref="CreateAsync"/> creates one, with a new credential and the
  /// old renderer's size; once it is ready its record replaces the old one in one step, and the old
  /// sandbox is deleted <paramref name="drain"/> later. Then the renderer sandboxes no record points
  /// to are deleted, as <see cref="PruneAsync"/> does. Renderers already on the image are skipped,
  /// so a rollout that failed for some tenants can simply run again.
  /// </summary>
  /// <param name="diskImageId">The disk image to move every renderer to.</param>
  /// <param name="tenantId">The only tenant to replace, or <see langword="null"/> for all.</param>
  /// <param name="maxParallel">
  /// How many replacements are created at once. An old renderer's drain does not hold a place, so
  /// the time to replace every renderer does not grow with the drain.
  /// </param>
  /// <param name="retireStopped">
  /// The prefixes whose tenants' stopped renderers are retired instead of replaced, however long
  /// they have been stopped, as <see cref="RetireIdleAsync"/> retires idle ones; each comes back
  /// from the provisioning service's disk image on its tenant's next conversion. A renderer that
  /// starts before it is retired is replaced after all. <see langword="null"/> replaces stopped
  /// renderers too.
  /// </param>
  /// <param name="drain">How long an old renderer stays after its record moves.</param>
  /// <param name="cancellationToken">Cancels the rollout; replacements not yet recorded are deleted.</param>
  public async Task<RolloutResult> RolloutAsync(
    string diskImageId,
    string? tenantId,
    int maxParallel,
    TimeSpan drain,
    ProvisioningServiceOptions? retireStopped,
    CancellationToken cancellationToken
  )
  {
    ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    IReadOnlyList<RendererRecord> records;
    if (tenantId is null)
    {
      var listing = await _records.ListWithUnreadableAsync(cancellationToken);
      records = listing.Records;
      // Neither its renderer nor its size is known, so it is left alone and reported.
      foreach (var unreadable in listing.Unreadable)
      {
        failures[unreadable.TenantId] =
          $"the record cannot be read ({unreadable.Reason}); fix it, or delete the tenant.";
      }
    }
    else
    {
      records =
      [
        await _records.GetAsync(tenantId, cancellationToken)
          ?? throw new ProvisioningException($"Tenant {tenantId} has no renderer; create one."),
      ];
    }

    var existing = await ListSandboxesAsync(cancellationToken);
    List<RendererRecord> outdated = [];
    // The outdated tenants whose renderers are retired rather than replaced.
    HashSet<string> stopped = new(StringComparer.Ordinal);
    foreach (var record in records.OrderBy(record => record.TenantId, StringComparer.Ordinal))
    {
      var sandbox = record.SandboxId is null ? null : existing.GetValueOrDefault(record.SandboxId);
      if (!string.Equals(record.DiskImageId, diskImageId, StringComparison.Ordinal))
      {
        outdated.Add(record);
        if (
          retireStopped?.PrefixOf(record.TenantId) is not null
          && sandbox is not null
          && IsRetirable(sandbox, record.TenantId, idle: null)
        )
        {
          stopped.Add(record.TenantId);
        }
      }
      else if (record.SandboxId is { } sandboxId && sandbox is null)
      {
        // On the image, but pointing at a sandbox that is gone: replacing it repairs the tenant.
        Log(record.TenantId, $"Sandbox {sandboxId} of the record no longer exists.");
        outdated.Add(record);
      }
    }

    var current = records.Count - outdated.Count;
    _output.WriteLine(
      $"Rollout of disk image {diskImageId}: {outdated.Count - stopped.Count} to replace, "
        + (retireStopped is null ? "" : $"{stopped.Count} stopped to retire, ")
        + $"{current} already on it"
        + (failures.IsEmpty ? ". " : $", {failures.Count} unreadable. ")
        + $"{maxParallel} at a time; old sandboxes are deleted {Format(drain)} after their record moves."
    );

    ConcurrentQueue<string> replaced = new();
    ConcurrentQueue<string> retiredStopped = new();
    ConcurrentQueue<Task> retiring = new();
    try
    {
      await Parallel.ForEachAsync(
        outdated,
        new ParallelOptions
        {
          MaxDegreeOfParallelism = maxParallel,
          CancellationToken = cancellationToken,
        },
        async (old, token) =>
        {
          try
          {
            if (stopped.Contains(old.TenantId))
            {
              switch (await RetireHeldAsync(old, idle: null, existing.Values, token))
              {
                case Retirement.Retired:
                  retiredStopped.Enqueue(old.TenantId);
                  return;
                case Retirement.RecordChanged:
                  failures[old.TenantId] =
                    "the record changed since the rollout read it; run rollout again.";
                  return;
                case Retirement.Busy:
                  failures[old.TenantId] =
                    "a creation or delete of the tenant's renderer is in flight; run rollout again.";
                  return;
                default:
                  // Started, or disabled, since it was read: replaced as any other.
                  break;
              }
            }

            var size = await ReadSizeAsync(old, token);
            Log(
              old.TenantId,
              $"Replacing sandbox {old.SandboxId ?? "(none)"} "
                + $"(disk image {old.DiskImageId ?? "(unknown)"}, size {size.Name})."
            );
            var (record, _) = await LaunchAsync(
              old.TenantId,
              size,
              diskImageId,
              replacing: old,
              NewLaunchId(),
              token
            );
            Log(old.TenantId, $"The record now points to sandbox {record.SandboxId}.");
            retiring.Enqueue(RetireAsync(old, drain, replaced, failures, cancellationToken));
          }
          catch (Exception exception)
            when (exception is not OperationCanceledException
              || !cancellationToken.IsCancellationRequested
            )
          {
            failures[old.TenantId] = exception.Message;
            Log(old.TenantId, $"Failed: {exception.Message}");
          }
        }
      );
    }
    finally
    {
      // Never throws: an old sandbox that could not be deleted is a failure of its own.
      await Task.WhenAll(retiring);
    }

    // The old sandboxes this run retired are done with, deleted or reported; the rest of what no
    // record points to is left over from earlier commands. A canceled rollout leaves them to the
    // next run, and still reports what it did.
    var retired = outdated
      .Select(record => record.SandboxId)
      .OfType<string>()
      .ToHashSet(StringComparer.Ordinal);
    var pruned = cancellationToken.IsCancellationRequested
      ? []
      : await PruneLeftoversAsync(tenantId, drain, retired, failures, cancellationToken);

    RolloutResult result = new(
      [.. replaced.Order(StringComparer.Ordinal)],
      [.. retiredStopped.Order(StringComparer.Ordinal)],
      current,
      new SortedDictionary<string, string>(failures, StringComparer.Ordinal),
      pruned
    );
    _output.WriteLine(
      $"Replaced {result.Replaced.Count}, "
        + (retireStopped is null ? "" : $"retired {result.Retired.Count}, ")
        + $"already on the image {result.AlreadyCurrent}, failed {result.Failures.Count}."
    );
    foreach (var (tenant, reason) in result.Failures)
    {
      _output.WriteLine($"  {tenant}: {reason}");
    }

    if (result.Retired.Count > 0)
    {
      _output.WriteLine("Retired, to be created again on their next conversion:");
      foreach (var tenant in result.Retired)
      {
        _output.WriteLine($"  {tenant}");
      }
    }

    return result;
  }

  /// <summary>
  /// Replaces every renderer, or <paramref name="tenantId"/>'s, as the overload with
  /// <c>retireStopped</c> does, stopped ones included.
  /// </summary>
  public Task<RolloutResult> RolloutAsync(
    string diskImageId,
    string? tenantId,
    int maxParallel,
    TimeSpan drain,
    CancellationToken cancellationToken
  ) =>
    RolloutAsync(diskImageId, tenantId, maxParallel, drain, retireStopped: null, cancellationToken);

  /// <summary>
  /// Deletes the tenant's record first, so the gateway stops routing to its renderer, then, after
  /// <paramref name="drain"/>, its sandboxes: every one labeled for the tenant when the delete began,
  /// such as one an earlier delete or a failed rollout left behind. Deleting a tenant that has
  /// neither succeeds.
  /// </summary>
  /// <remarks>
  /// A record that cannot be read, or that the store disabled, is deleted all the same: destroying a
  /// compromised renderer must not depend on its record, nor on the data plane answering. A sandbox
  /// the record names that is labeled for another tenant is never deleted; the command fails after
  /// the rest is done.
  /// </remarks>
  public Task DeleteAsync(string tenantId, TimeSpan drain, CancellationToken cancellationToken) =>
    DeleteAsync(tenantId, drain, refuseDisabled: false, listed: null, cancellationToken);

  /// <summary>
  /// Deletes the tenant's record, then the sandboxes it decided on before that: those labeled for
  /// the tenant in <paramref name="listed"/>, or in a listing taken now, which include the record's
  /// own if it still exists. A sandbox created after the decision, such as by a creation that
  /// starts once the record is gone, is never deleted.
  /// </summary>
  /// <param name="tenantId">The tenant.</param>
  /// <param name="drain">How long to wait between the record and the sandboxes.</param>
  /// <param name="refuseDisabled">
  /// Whether to delete nothing, and throw <see cref="RendererDisabledException"/>, when a sandbox
  /// labeled for the tenant is disabled: an operator keeps it for investigation, which a delete
  /// from the provisioning service must not destroy.
  /// </param>
  /// <param name="listed">The group's sandboxes as listed before, or <see langword="null"/> to list them.</param>
  /// <param name="cancellationToken">Cancels the delete.</param>
  internal async Task DeleteAsync(
    string tenantId,
    TimeSpan drain,
    bool refuseDisabled,
    IReadOnlyCollection<SandboxView>? listed,
    CancellationToken cancellationToken
  )
  {
    RendererRecord? record = null;
    var unreadable = false;
    try
    {
      record = await _records.GetAsync(tenantId, cancellationToken);
    }
    catch (InvalidDataException exception)
    {
      unreadable = true;
      Log(tenantId, $"The record cannot be read ({exception.Message}); deleting it all the same.");
    }

    // Listed after the record was read: the sandbox a record names exists before the record does.
    IReadOnlyCollection<SandboxView> sandboxes;
    try
    {
      sandboxes = listed ?? await _sandboxes.ListAsync(cancellationToken);
    }
    catch (Exception exception)
      when (!refuseDisabled
        && (
          exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested
        )
      )
    {
      // The record still goes, so the gateway stops routing to the renderer; deleting again
      // deletes the sandboxes once the data plane answers.
      Log(
        tenantId,
        $"Could not list the sandboxes ({exception.Message}); deleting the record all the same."
      );
      await _records.DeleteAsync(tenantId, cancellationToken);
      throw;
    }

    SortedSet<string> sandboxIds = new(StringComparer.Ordinal);
    foreach (var sandbox in sandboxes.OrderBy(sandbox => sandbox.Id, StringComparer.Ordinal))
    {
      if (RendererLabels.TenantOf(sandbox) == tenantId)
      {
        if (refuseDisabled && sandbox.IsDisabled)
        {
          throw new RendererDisabledException(
            $"Sandbox {sandbox.Id} of tenant {tenantId} is disabled, so nothing was deleted. Enable "
              + "it, or delete the tenant from the command line."
          );
        }

        sandboxIds.Add(sandbox.Id);
      }
    }

    // The record's sandbox ID is not trusted on its own: a sandbox it names that is not labeled for
    // this tenant belongs to someone else, or to no renderer at all.
    string? refused = null;
    if (
      record?.SandboxId is { } recorded
      && !sandboxIds.Contains(recorded)
      && sandboxes.FirstOrDefault(sandbox => sandbox.Id == recorded) is { } named
    )
    {
      refused =
        $"The record of tenant {tenantId} named sandbox {recorded}, which is labeled for "
        + $"{(RendererLabels.TenantOf(named) is { } other ? "tenant " + other : "no tenant")}; "
        + "it was not deleted.";
      Log(tenantId, refused);
    }

    // Even with no record read: a disabled one reads as none, and is still there.
    await _records.DeleteAsync(tenantId, cancellationToken);
    if (record is not null || unreadable)
    {
      Log(
        tenantId,
        "Deleted the record; the gateway stops routing to the renderer once its cached copy expires."
      );
    }

    // An unreadable record routes nothing, so only a readable one needs the drain.
    if (record is not null && drain > TimeSpan.Zero)
    {
      Log(tenantId, $"Waiting {Format(drain)} before deleting the sandbox.");
      await Task.Delay(drain, _time, cancellationToken);
    }

    foreach (var sandboxId in sandboxIds)
    {
      await _sandboxes.DeleteAsync(sandboxId, cancellationToken);
      Log(tenantId, $"Deleted sandbox {sandboxId}.");
    }

    if (refused is not null)
    {
      throw new ProvisioningException(refused);
    }

    if (record is null && !unreadable && sandboxIds.Count == 0)
    {
      Log(tenantId, "No renderer to delete.");
    }
  }

  /// <summary>
  /// Disables every sandbox labeled for the tenant: the kill switch for a compromised renderer. The
  /// platform stops it and refuses to start it again, on a request to its on-demand port as on a
  /// resume, until <see cref="EnableAsync"/>. The record and the disk stay, for investigation; the
  /// gateway's conversions for the tenant fail meanwhile.
  /// </summary>
  /// <remarks>
  /// Every sandbox is tried even when one fails; the command fails after. A record that cannot be
  /// read does not stop it, and a sandbox the record names that is labeled for another tenant is not
  /// touched, as in <see cref="DeleteAsync"/>.
  /// </remarks>
  public Task DisableAsync(string tenantId, CancellationToken cancellationToken) =>
    SwitchAsync(tenantId, disable: true, cancellationToken);

  /// <summary>Lets the tenant's disabled sandboxes start again, as <see cref="DisableAsync"/> undoes.</summary>
  public Task EnableAsync(string tenantId, CancellationToken cancellationToken) =>
    SwitchAsync(tenantId, disable: false, cancellationToken);

  private async Task SwitchAsync(string tenantId, bool disable, CancellationToken cancellationToken)
  {
    var verb = disable ? "disable" : "enable";
    RendererRecord? record = null;
    try
    {
      record = await _records.GetAsync(tenantId, cancellationToken);
    }
    catch (InvalidDataException exception)
    {
      Log(tenantId, $"The record cannot be read ({exception.Message}); going by labels alone.");
    }

    var sandboxes = await _sandboxes.ListAsync(cancellationToken);
    var sandboxIds = sandboxes
      .Where(sandbox => RendererLabels.TenantOf(sandbox) == tenantId)
      .Select(sandbox => sandbox.Id)
      .Order(StringComparer.Ordinal)
      .ToArray();
    List<string> problems = [];
    if (
      record?.SandboxId is { } recorded
      && !sandboxIds.Contains(recorded, StringComparer.Ordinal)
      && sandboxes.FirstOrDefault(sandbox => sandbox.Id == recorded) is { } named
    )
    {
      var refused =
        $"The record of tenant {tenantId} names sandbox {recorded}, which is labeled for "
        + $"{(RendererLabels.TenantOf(named) is { } other ? "tenant " + other : "no tenant")}; "
        + $"it was not {verb}d.";
      Log(tenantId, refused);
      problems.Add(refused);
    }

    foreach (var sandboxId in sandboxIds)
    {
      try
      {
        var sandbox = disable
          ? await _sandboxes.DisableAsync(sandboxId, cancellationToken)
          : await _sandboxes.EnableAsync(sandboxId, cancellationToken);
        Log(
          tenantId,
          $"{(disable ? "Disabled" : "Enabled")} sandbox {sandboxId} ({sandbox.State})."
        );
      }
      catch (Exception exception)
        when (exception is not OperationCanceledException
          || !cancellationToken.IsCancellationRequested
        )
      {
        var failure = $"Could not {verb} sandbox {sandboxId}: {exception.Message}";
        Log(tenantId, failure);
        problems.Add(failure);
      }
    }

    if (sandboxIds.Length == 0)
    {
      Log(tenantId, $"No sandbox to {verb}.");
    }

    if (problems.Count > 0)
    {
      throw new ProvisioningException(string.Join(" ", problems));
    }
  }

  /// <summary>
  /// Writes every record with the current state of its sandbox, then the records that cannot be
  /// read, then any renderer sandbox no record points to: one being created, or one a failed or
  /// canceled command left behind.
  /// </summary>
  public async Task ListAsync(CancellationToken cancellationToken)
  {
    var listing = await _records.ListWithUnreadableAsync(cancellationToken);
    var records = listing
      .Records.OrderBy(record => record.TenantId, StringComparer.Ordinal)
      .ToArray();
    Dictionary<string, SandboxView> sandboxes = new(StringComparer.Ordinal);
    foreach (var sandbox in await _sandboxes.ListAsync(cancellationToken))
    {
      sandboxes[sandbox.Id] = sandbox;
    }

    if (records.Length == 0)
    {
      _output.WriteLine("No renderers.");
    }
    else
    {
      WriteTable(
        ["TENANT", "SANDBOX", "STATE", "SIZE", "DISK IMAGE", "CREATED"],
        records.Select(record =>
        {
          var sandbox = record.SandboxId is null
            ? null
            : sandboxes.GetValueOrDefault(record.SandboxId);
          return (IReadOnlyList<string>)
            [
              record.TenantId,
              record.SandboxId ?? "-",
              sandbox?.State ?? (record.SandboxId is null ? "-" : "missing"),
              (sandbox is null ? null : RendererLabels.SizeOf(sandbox)) ?? "-",
              record.DiskImageId ?? "-",
              record.CreatedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
            ];
        })
      );
    }

    if (listing.Unreadable.Count > 0)
    {
      _output.WriteLine();
      _output.WriteLine("Records that cannot be read (fix them, or delete the tenant):");
      WriteTable(
        ["TENANT", "REASON"],
        listing
          .Unreadable.OrderBy(record => record.TenantId, StringComparer.Ordinal)
          .Select(record => (IReadOnlyList<string>)[record.TenantId, record.Reason])
      );
    }

    var recorded = records
      .Select(record => record.SandboxId)
      .OfType<string>()
      .ToHashSet(StringComparer.Ordinal);
    var unrecorded = sandboxes
      .Values.Where(sandbox => RendererLabels.IsRenderer(sandbox) && !recorded.Contains(sandbox.Id))
      .OrderBy(sandbox => sandbox.Id, StringComparer.Ordinal)
      .ToArray();
    if (unrecorded.Length > 0)
    {
      _output.WriteLine();
      _output.WriteLine(
        "Renderer sandboxes no record points to (being created, or left behind by a failed or "
          + "canceled command):"
      );
      WriteTable(
        ["SANDBOX", "TENANT", "STATE"],
        unrecorded.Select(sandbox =>
          (IReadOnlyList<string>)
            [sandbox.Id, RendererLabels.TenantOf(sandbox) ?? "-", sandbox.State]
        )
      );
    }
  }

  /// <summary>
  /// Retires the renderers of tenants under <paramref name="service"/>'s prefixes whose sandboxes
  /// have been stopped (not disabled) for longer than its <c>RetireAfterIdle</c>: deletes each record,
  /// then its sandboxes, as <see cref="DeleteAsync"/> does. Does nothing when <c>RetireAfterIdle</c>
  /// is zero.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A record whose sandbox no longer exists is retired too: the record alone is deleted. Kept are
  /// running and disabled renderers, those whose stop time the data plane does not report, records
  /// that cannot be read, and every tenant outside the prefixes. A record that names a sandbox
  /// labeled for another tenant is kept and reported as a failure.
  /// </para>
  /// <para>
  /// Just before deleting, the record and the sandbox are read again: a record another command
  /// changed meanwhile, or a sandbox a request is waking, is kept until the next run.
  /// </para>
  /// </remarks>
  public async Task<RetireResult> RetireIdleAsync(
    ProvisioningServiceOptions service,
    CancellationToken cancellationToken
  )
  {
    ArgumentNullException.ThrowIfNull(service);
    List<string> retired = [];
    SortedDictionary<string, string> failures = new(StringComparer.Ordinal);
    var idle = service.RetireAfterIdle;
    if (idle <= TimeSpan.Zero)
    {
      _output.WriteLine(
        $"Retiring is off: {ProvisioningServiceOptions.SectionName}:RetireAfterIdle is 00:00:00."
      );
      return new RetireResult(retired, failures);
    }

    // Records first: a renderer's sandbox exists before its record, so the sandboxes listed after
    // include every one a listed record names, unless it is gone.
    var listing = await _records.ListWithUnreadableAsync(cancellationToken);
    var sandboxes = await ListSandboxesAsync(cancellationToken);
    foreach (var unreadable in listing.Unreadable)
    {
      if (service.PrefixOf(unreadable.TenantId) is not null)
      {
        Log(unreadable.TenantId, $"Not retired: the record cannot be read ({unreadable.Reason}).");
      }
    }

    var now = _time.GetUtcNow();
    foreach (
      var record in listing
        .Records.Where(record => service.PrefixOf(record.TenantId) is not null)
        .OrderBy(record => record.TenantId, StringComparer.Ordinal)
    )
    {
      var tenant = record.TenantId;
      if (record.SandboxId is not { } sandboxId)
      {
        Log(tenant, "Not retired: the record names no sandbox.");
        continue;
      }

      if (sandboxes.GetValueOrDefault(sandboxId) is { } sandbox)
      {
        if (sandbox.State != SandboxStates.Stopped || sandbox.IsDisabled)
        {
          continue;
        }

        if (RendererLabels.TenantOf(sandbox) is var owner && owner != tenant)
        {
          failures[tenant] =
            $"the record names sandbox {sandboxId}, which is labeled for "
            + $"{(owner is null ? "no tenant" : "tenant " + owner)}; it was not retired.";
          Log(tenant, $"Not retired: {failures[tenant]}");
          continue;
        }

        if (sandbox.StoppedAt is not { } stoppedAt)
        {
          Log(tenant, $"Not retired: when sandbox {sandboxId} stopped is unknown.");
          continue;
        }

        if (now - stoppedAt <= idle)
        {
          continue;
        }
      }

      try
      {
        if (
          await RetireHeldAsync(record, idle, sandboxes.Values, cancellationToken)
          == Retirement.Retired
        )
        {
          retired.Add(tenant);
        }
      }
      catch (Exception exception)
        when (exception is not OperationCanceledException
          || !cancellationToken.IsCancellationRequested
        )
      {
        failures[tenant] = exception.Message;
        Log(tenant, $"Failed: {exception.Message}");
      }
    }

    _output.WriteLine($"Retired {retired.Count}, failed {failures.Count}.");
    foreach (var (tenant, reason) in failures)
    {
      _output.WriteLine($"  {tenant}: {reason}");
    }

    return new RetireResult(retired, failures);
  }

  /// <summary>
  /// Retires the tenant's renderer as <see cref="RetireRendererAsync"/> does, holding the tenant in
  /// the process's gate meanwhile; a tenant whose renderer is being created or deleted is left alone.
  /// </summary>
  private async Task<Retirement> RetireHeldAsync(
    RendererRecord scanned,
    TimeSpan? idle,
    IReadOnlyCollection<SandboxView> listed,
    CancellationToken cancellationToken
  )
  {
    using var hold = _gate.TryHold(scanned.TenantId);
    if (hold is null)
    {
      Log(scanned.TenantId, "Not retired: its renderer is being created or deleted.");
      return Retirement.Busy;
    }

    return await RetireRendererAsync(scanned, idle, listed, cancellationToken);
  }

  /// <summary>
  /// Retires the tenant's renderer, which a scan found stopped or gone: deletes its record, then its
  /// sandboxes in <paramref name="listed"/>, as <see cref="DeleteAsync"/> does, unless the record or
  /// the sandbox changed since, or a sandbox of the tenant is disabled.
  /// </summary>
  /// <param name="scanned">The record as the scan read it.</param>
  /// <param name="idle">
  /// How long the sandbox must have been stopped, or <see langword="null"/> for a rollout, which
  /// retires stopped renderers however long they have been stopped.
  /// </param>
  /// <param name="listed">The group's sandboxes as the scan listed them; only these are deleted.</param>
  /// <param name="cancellationToken">Cancels the retirement.</param>
  private async Task<Retirement> RetireRendererAsync(
    RendererRecord scanned,
    TimeSpan? idle,
    IReadOnlyCollection<SandboxView> listed,
    CancellationToken cancellationToken
  )
  {
    var tenant = scanned.TenantId;
    RendererRecord? current;
    try
    {
      current = await _records.GetAsync(tenant, cancellationToken);
    }
    catch (InvalidDataException)
    {
      current = null;
    }

    if (current != scanned)
    {
      Log(tenant, "Not retired: the record changed since it was read; another command is at work.");
      return Retirement.RecordChanged;
    }

    // Read last, just before the delete: a request to its on-demand port may be waking it.
    var sandboxId = scanned.SandboxId!;
    var sandbox = await _sandboxes.GetAsync(sandboxId, cancellationToken);
    if (sandbox is null)
    {
      Log(tenant, $"Retiring: sandbox {sandboxId} of the record no longer exists.");
    }
    else if (IsRetirable(sandbox, tenant, idle))
    {
      Log(
        tenant,
        idle is { } after
          ? $"Retiring: sandbox {sandboxId} has been stopped since "
            + sandbox.StoppedAt!.Value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + $", longer than {Format(after)}."
          : $"Retiring the stopped sandbox {sandboxId}; the tenant's next conversion creates a "
            + "new renderer."
      );
    }
    else
    {
      Log(
        tenant,
        sandbox.State != SandboxStates.Stopped
          ? $"Not retired: sandbox {sandboxId} is {sandbox.State} now; a request may be waking it."
          : $"Not retired: sandbox {sandboxId} was "
            + (sandbox.IsDisabled ? "disabled" : "stopped again")
            + " meanwhile."
      );
      return Retirement.SandboxChanged;
    }

    try
    {
      await DeleteAsync(tenant, TimeSpan.Zero, refuseDisabled: true, listed, cancellationToken);
    }
    catch (RendererDisabledException exception)
    {
      Log(tenant, $"Not retired: {exception.Message}");
      return Retirement.SandboxChanged;
    }

    return Retirement.Retired;
  }

  /// <summary>
  /// Whether <paramref name="sandbox"/> is the tenant's renderer, stopped but not disabled, and with
  /// <paramref name="idle"/>, stopped for longer than that at a known time.
  /// </summary>
  private bool IsRetirable(SandboxView sandbox, string tenantId, TimeSpan? idle) =>
    sandbox.State == SandboxStates.Stopped
    && !sandbox.IsDisabled
    && RendererLabels.TenantOf(sandbox) == tenantId
    && (
      idle is not { } after
      || (sandbox.StoppedAt is { } stoppedAt && _time.GetUtcNow() - stoppedAt > after)
    );

  /// <summary>Every sandbox of the group, by ID.</summary>
  private async Task<Dictionary<string, SandboxView>> ListSandboxesAsync(
    CancellationToken cancellationToken
  )
  {
    Dictionary<string, SandboxView> sandboxes = new(StringComparer.Ordinal);
    foreach (var sandbox in await _sandboxes.ListAsync(cancellationToken))
    {
      sandboxes[sandbox.Id] = sandbox;
    }

    return sandboxes;
  }

  /// <summary>What <see cref="RetireRendererAsync"/> did.</summary>
  private enum Retirement
  {
    /// <summary>Deleted the record and the sandboxes.</summary>
    Retired,

    /// <summary>Kept the renderer: another command changed the record since it was read.</summary>
    RecordChanged,

    /// <summary>Kept the renderer: its sandbox is running, waking, or disabled now.</summary>
    SandboxChanged,

    /// <summary>Kept the renderer: it is being created or deleted in this process.</summary>
    Busy,
  }

  /// <summary>
  /// Deletes the renderer sandboxes, of every tenant or of <paramref name="tenantId"/>, that no
  /// record points to: the old sandboxes of a rollout canceled during its drain, a sandbox whose
  /// tenant was deleted while its delete was interrupted, or one a killed create left behind.
  /// </summary>
  /// <param name="tenantId">The only tenant whose leftovers to delete, or <see langword="null"/>.</param>
  /// <param name="drain">
  /// How long to wait between finding the leftovers and deleting them, in case the gateway still
  /// sends them work: an old sandbox's record may have moved moments ago.
  /// </param>
  /// <param name="cancellationToken">Cancels the prune.</param>
  public async Task<PruneResult> PruneAsync(
    string? tenantId,
    TimeSpan drain,
    CancellationToken cancellationToken
  )
  {
    ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    var deleted = await PruneLeftoversAsync(
      tenantId,
      drain,
      new HashSet<string>(StringComparer.Ordinal),
      failures,
      cancellationToken
    );
    PruneResult result = new(
      deleted,
      new SortedDictionary<string, string>(failures, StringComparer.Ordinal)
    );
    _output.WriteLine($"Deleted {result.Deleted.Count}, failed {result.Failures.Count}.");
    foreach (var (tenant, reason) in result.Failures)
    {
      _output.WriteLine($"  {tenant}: {reason}");
    }

    return result;
  }

  /// <summary>
  /// Finds the leftover renderer sandboxes, waits <paramref name="drain"/>, and deletes those that
  /// are still leftovers. Failures go to <paramref name="failures"/> by tenant.
  /// </summary>
  /// <param name="exclude">Sandboxes to leave alone, such as the ones a rollout just retired.</param>
  private async Task<IReadOnlyList<string>> PruneLeftoversAsync(
    string? tenantId,
    TimeSpan drain,
    IReadOnlySet<string> exclude,
    ConcurrentDictionary<string, string> failures,
    CancellationToken cancellationToken
  )
  {
    var found = await FindLeftoversAsync(tenantId, drain, exclude, report: true, cancellationToken);
    if (found.Count == 0)
    {
      _output.WriteLine(
        tenantId is null
          ? "No renderer sandboxes are left over."
          : $"[{tenantId}] No renderer sandboxes are left over."
      );
      return [];
    }

    if (drain > TimeSpan.Zero)
    {
      _output.WriteLine(
        $"Deleting {found.Count} leftover renderer sandbox(es) in {Format(drain)}, unless a record "
          + "points to them by then."
      );
      await Task.Delay(drain, _time, cancellationToken);
    }

    // Read again: a record may point to one of them now.
    var still = (
      await FindLeftoversAsync(tenantId, drain, exclude, report: false, cancellationToken)
    )
      .Select(sandbox => sandbox.Id)
      .ToHashSet(StringComparer.Ordinal);
    List<string> deleted = [];
    foreach (var sandbox in found)
    {
      var tenant = RendererLabels.TenantOf(sandbox)!;
      if (!still.Contains(sandbox.Id))
      {
        Log(tenant, $"Kept sandbox {sandbox.Id}: a record points to it now, or it is gone.");
        continue;
      }

      try
      {
        await _sandboxes.DeleteAsync(sandbox.Id, cancellationToken);
        Log(tenant, $"Deleted the leftover sandbox {sandbox.Id}.");
        deleted.Add(sandbox.Id);
      }
      catch (Exception exception)
        when (exception is not OperationCanceledException
          || !cancellationToken.IsCancellationRequested
        )
      {
        failures.TryAdd(
          tenant,
          $"the leftover sandbox {sandbox.Id} was not deleted ({exception.Message})."
        );
        Log(tenant, $"The leftover sandbox {sandbox.Id} was not deleted: {exception.Message}");
      }
    }

    return [.. deleted.Order(StringComparer.Ordinal)];
  }

  /// <summary>
  /// The renderer sandboxes labeled for a tenant (or <paramref name="tenantId"/>) that no readable
  /// record points to. Kept, and with <paramref name="report"/> written: those of a tenant whose
  /// record cannot be read, since which sandbox it names is unknown, and those created less than
  /// the drain and the ready timeout ago, which may belong to a launch that has not written its
  /// record yet.
  /// </summary>
  private async Task<IReadOnlyList<SandboxView>> FindLeftoversAsync(
    string? tenantId,
    TimeSpan drain,
    IReadOnlySet<string> exclude,
    bool report,
    CancellationToken cancellationToken
  )
  {
    var listing = await _records.ListWithUnreadableAsync(cancellationToken);
    var referenced = listing
      .Records.Select(record => record.SandboxId)
      .OfType<string>()
      .ToHashSet(StringComparer.Ordinal);
    var unreadable = listing
      .Unreadable.Select(record => record.TenantId)
      .ToHashSet(StringComparer.Ordinal);
    var youngest = _time.GetUtcNow() - drain - _options.ReadyTimeout;
    List<SandboxView> leftovers = [];
    foreach (
      var sandbox in (await _sandboxes.ListAsync(cancellationToken)).OrderBy(
        sandbox => sandbox.Id,
        StringComparer.Ordinal
      )
    )
    {
      if (
        RendererLabels.TenantOf(sandbox) is not { } tenant
        || !TenantId.IsValid(tenant)
        || (tenantId is not null && tenant != tenantId)
        || referenced.Contains(sandbox.Id)
        || exclude.Contains(sandbox.Id)
      )
      {
        continue;
      }

      if (unreadable.Contains(tenant))
      {
        if (report)
        {
          Log(
            tenant,
            $"Kept sandbox {sandbox.Id}: the tenant's record cannot be read, so it may point to it."
          );
        }

        continue;
      }

      if (sandbox.CreatedAt is not { } createdAt || createdAt > youngest)
      {
        if (report)
        {
          Log(
            tenant,
            $"Kept sandbox {sandbox.Id}: it may still be being created "
              + (
                sandbox.CreatedAt is { } at
                  ? $"(created {at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)})."
                  : "(its creation time is unknown)."
              )
          );
        }

        continue;
      }

      if (report)
      {
        Log(tenant, $"Sandbox {sandbox.Id} is a renderer no record points to.");
      }

      leftovers.Add(sandbox);
    }

    return leftovers;
  }

  /// <summary>
  /// Starts a renderer for the tenant and points its record at it: a sandbox with a new credential,
  /// its port exposed, <c>/health/ready</c> answering <c>200</c>, and then the record written in one
  /// step. If any of that fails or is canceled, the sandbox is deleted again and the record is left
  /// as it was.
  /// </summary>
  /// <param name="replacing">
  /// The record the command read and means to replace, or <see langword="null"/> for a tenant that
  /// had none. If the record is no longer that when the renderer is ready, another command got
  /// there first, and this launch is discarded.
  /// </param>
  /// <param name="launchId">The launch's own label value, from <see cref="NewLaunchId"/>.</param>
  private async Task<(RendererRecord Record, SandboxView Sandbox)> LaunchAsync(
    string tenantId,
    RendererSize size,
    string diskImageId,
    RendererRecord? replacing,
    string launchId,
    CancellationToken cancellationToken
  )
  {
    var credential = RendererCredential.Generate();
    SandboxSpec spec = new()
    {
      DiskImageId = diskImageId,
      Cpu = size.Cpu,
      Memory = size.Memory,
      Entrypoint = RendererServerEnvironment.Entrypoint,
      // Only the credential's verifier enters the renderer; the record keeps the credential itself.
      Environment = RendererServerEnvironment.Create(credential, size),
      Labels = RendererLabels.For(tenantId, size, launchId),
      EgressDefaultAction = "Deny",
      AutoSuspendAfter = _options.AutoSuspendAfter,
      NetworkConnectionName = _options.NetworkConnectionName,
    };
    Log(
      tenantId,
      $"Creating a sandbox from disk image {diskImageId}, size {size.Name} "
        + $"({size.Cpu} CPU, {size.Memory} memory)."
    );
    var started = _time.GetTimestamp();
    SandboxView created;
    try
    {
      created = await _sandboxes.CreateAsync(spec, cancellationToken);
    }
    catch
    {
      // A create that failed or was canceled on the way back may still have made the sandbox.
      await DeleteLaunchAsync(tenantId, launchId);
      throw;
    }

    RendererRecord record;
    SandboxView sandbox;
    try
    {
      var createdAt = _time.GetUtcNow();
      Log(tenantId, $"Created sandbox {created.Id}; exposing port {RendererPort}.");
      sandbox = await _sandboxes.AddPortAsync(
        created.Id,
        RendererPort,
        _options.PortOptions,
        cancellationToken
      );
      var url =
        sandbox.Ports.FirstOrDefault(port => port.Port == RendererPort)?.Url
        ?? throw new ProvisioningException(
          $"Sandbox {created.Id} reported no URL for port {RendererPort}."
        );
      Log(tenantId, $"Waiting for {url} to be ready.");
      await WaitUntilReadyAsync(url, created.Id, cancellationToken);
      Log(tenantId, $"Ready {Seconds(_time.GetElapsedTime(started))} after the create call.");
      record = new RendererRecord
      {
        TenantId = tenantId,
        Url = url,
        ApiKey = credential.Credential,
        SandboxId = created.Id,
        DiskImageId = diskImageId,
        CreatedAt = createdAt,
        // What the renderer's environment admits, so the gateway sends no more.
        MaxConcurrentRequests = RendererServerEnvironment.MaxConcurrentRequests(size),
      };
      await EnsureRecordUnchangedAsync(tenantId, replacing, cancellationToken);
    }
    catch
    {
      await DeleteLeftoverAsync(tenantId, created.Id);
      throw;
    }

    try
    {
      await _records.PutAsync(record, cancellationToken);
    }
    catch (Exception exception)
    {
      // The write may have taken effect and only its answer failed: the sandbox is deleted only
      // when the record is known not to point to it.
      switch (await RecordPointsToAsync(tenantId, created.Id))
      {
        case true:
          Log(
            tenantId,
            $"Writing the record reported a failure ({exception.Message}), but the record points "
              + $"to sandbox {created.Id}."
          );
          return (record, sandbox);
        case false:
          await DeleteLeftoverAsync(tenantId, created.Id);
          break;
        default:
          Log(
            tenantId,
            $"Could not tell whether the record points to sandbox {created.Id}, so it is kept; "
              + "prune deletes it if no record does."
          );
          break;
      }

      throw;
    }

    return (record, sandbox);
  }

  /// <summary>
  /// Throws <see cref="ProvisioningException"/> unless the tenant's record is still
  /// <paramref name="expected"/>, by sandbox: another command (a concurrent create, rollout, or
  /// delete) may have changed it while this one waited for its renderer.
  /// </summary>
  private async Task EnsureRecordUnchangedAsync(
    string tenantId,
    RendererRecord? expected,
    CancellationToken cancellationToken
  )
  {
    var current = await _records.GetAsync(tenantId, cancellationToken);
    if (expected is null)
    {
      if (current is not null)
      {
        throw new ProvisioningException(
          $"Tenant {tenantId} got a renderer (sandbox {current.SandboxId ?? "none"}) from another "
            + "command meanwhile; this one is discarded."
        );
      }
    }
    else if (current is null)
    {
      throw new ProvisioningException(
        $"The record of tenant {tenantId} was deleted meanwhile; the replacement is discarded."
      );
    }
    else if (!string.Equals(current.SandboxId, expected.SandboxId, StringComparison.Ordinal))
    {
      throw new ProvisioningException(
        $"The record of tenant {tenantId} moved to sandbox {current.SandboxId ?? "none"} meanwhile; "
          + "the replacement is discarded."
      );
    }
  }

  /// <summary>
  /// Whether the tenant's record points to <paramref name="sandboxId"/>, after a write that failed;
  /// <see langword="null"/> when the store cannot say within <see cref="CleanupTimeout"/>.
  /// </summary>
  private async Task<bool?> RecordPointsToAsync(string tenantId, string sandboxId)
  {
    using CancellationTokenSource timeout = new(CleanupTimeout, _time);
    try
    {
      var record = await _records.GetAsync(tenantId, timeout.Token);
      return string.Equals(record?.SandboxId, sandboxId, StringComparison.Ordinal);
    }
    catch (Exception exception)
    {
      Log(tenantId, $"Could not read the record back ({exception.Message}).");
      return null;
    }
  }

  /// <summary>
  /// Asks the renderer whether it is ready until it answers <c>200</c>, for up to the ready timeout.
  /// For its first seconds a new sandbox's proxy answers <c>403</c> or <c>502</c>, and its server
  /// <c>503</c> until the browser is up. The proxy answers <c>404</c> for a sandbox that no longer
  /// exists, so a <c>404</c> asks the data plane whether <paramref name="sandboxId"/> is gone, and a
  /// sandbox that is gone fails the wait at once rather than at the timeout.
  /// </summary>
  private async Task WaitUntilReadyAsync(
    Uri url,
    string sandboxId,
    CancellationToken cancellationToken
  )
  {
    var started = _time.GetTimestamp();
    while (true)
    {
      var answer = await _readiness.ProbeAsync(url, cancellationToken);
      if (answer.IsReady)
      {
        return;
      }

      if (
        answer.StatusCode == (int)HttpStatusCode.NotFound
        && await _sandboxes.GetAsync(sandboxId, cancellationToken) is null
      )
      {
        throw new ProvisioningException(
          $"Sandbox {sandboxId} was deleted while it started; its port answered {answer.Status}."
        );
      }

      var left = _options.ReadyTimeout - _time.GetElapsedTime(started);
      if (left <= TimeSpan.Zero)
      {
        throw new ProvisioningException(
          $"The renderer was not ready within {Format(_options.ReadyTimeout)}; "
            + $"it last answered {answer.Status}."
        );
      }

      await Task.Delay(
        left < ReadyPollInterval ? left : ReadyPollInterval,
        _time,
        cancellationToken
      );
    }
  }

  /// <summary>
  /// The size of the renderer being replaced, from its sandbox's label, so a rollout keeps sizes;
  /// <see cref="RendererSize.Medium"/> when the sandbox or the label is gone.
  /// </summary>
  private async Task<RendererSize> ReadSizeAsync(
    RendererRecord old,
    CancellationToken cancellationToken
  )
  {
    var sandbox = old.SandboxId is null
      ? null
      : await _sandboxes.GetAsync(old.SandboxId, cancellationToken);
    if (sandbox is not null && RendererSizes.TryParse(RendererLabels.SizeOf(sandbox), out var size))
    {
      return size;
    }

    Log(
      old.TenantId,
      sandbox is null
        ? $"Sandbox {old.SandboxId ?? "(none)"} is gone; the replacement is size M."
        : $"Sandbox {sandbox.Id} has no size label; the replacement is size M."
    );
    return RendererSize.Medium;
  }

  /// <summary>
  /// Deletes a replaced renderer's sandbox once the gateway no longer sends it work: after the
  /// drain, which outlasts the gateway's record cache and its longest request. Only a sandbox
  /// labeled for the tenant is deleted: the old record's sandbox ID alone is not trusted. Never
  /// throws.
  /// </summary>
  private async Task RetireAsync(
    RendererRecord old,
    TimeSpan drain,
    ConcurrentQueue<string> replaced,
    ConcurrentDictionary<string, string> failures,
    CancellationToken cancellationToken
  )
  {
    if (old.SandboxId is null)
    {
      replaced.Enqueue(old.TenantId);
      return;
    }

    try
    {
      await Task.Delay(drain, _time, cancellationToken);
      var sandbox = await _sandboxes.GetAsync(old.SandboxId, cancellationToken);
      if (sandbox is null)
      {
        Log(old.TenantId, $"The old sandbox {old.SandboxId} is already gone.");
        replaced.Enqueue(old.TenantId);
        return;
      }

      if (RendererLabels.TenantOf(sandbox) is var owner && owner != old.TenantId)
      {
        var reason =
          $"replaced, but the old record named sandbox {old.SandboxId}, which is labeled for "
          + $"{(owner is null ? "no tenant" : "tenant " + owner)}; it was not deleted.";
        failures[old.TenantId] = reason;
        Log(old.TenantId, reason);
        return;
      }

      await _sandboxes.DeleteAsync(old.SandboxId, cancellationToken);
      Log(old.TenantId, $"Deleted the old sandbox {old.SandboxId}.");
      replaced.Enqueue(old.TenantId);
    }
    catch (Exception exception)
    {
      var reason =
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
          ? "canceled"
          : exception.Message;
      failures[old.TenantId] =
        $"replaced, but the old sandbox {old.SandboxId} was not deleted ({reason}); "
        + "run rollout or prune again to delete it.";
      Log(old.TenantId, $"The old sandbox {old.SandboxId} was not deleted: {reason}");
    }
  }

  /// <summary>
  /// Deletes a sandbox a failed command created, even when the command was canceled, for at most
  /// <see cref="CleanupTimeout"/>. A failure here is written, not thrown: the command's own failure
  /// is the one to report.
  /// </summary>
  private async Task DeleteLeftoverAsync(string tenantId, string sandboxId)
  {
    using CancellationTokenSource timeout = new(CleanupTimeout, _time);
    try
    {
      await _sandboxes.DeleteAsync(sandboxId, timeout.Token);
      Log(tenantId, $"Deleted sandbox {sandboxId} again.");
    }
    catch (Exception exception)
    {
      Log(
        tenantId,
        $"Could not delete sandbox {sandboxId} ({exception.Message}); prune deletes it."
      );
    }
  }

  /// <summary>
  /// Deletes the sandbox a create that failed may have made, found by its launch label, even when
  /// the command was canceled, for at most <see cref="CleanupTimeout"/>. Never throws.
  /// </summary>
  private async Task DeleteLaunchAsync(string tenantId, string launchId)
  {
    using CancellationTokenSource timeout = new(CleanupTimeout, _time);
    try
    {
      foreach (var sandbox in await _sandboxes.ListAsync(timeout.Token))
      {
        if (RendererLabels.IsFromLaunch(sandbox, launchId))
        {
          await _sandboxes.DeleteAsync(sandbox.Id, timeout.Token);
          Log(tenantId, $"Deleted sandbox {sandbox.Id}, which the failed create made.");
        }
      }
    }
    catch (Exception exception)
    {
      Log(
        tenantId,
        $"Could not look for a sandbox the failed create made ({exception.Message}); "
          + "prune deletes one."
      );
    }
  }

  /// <summary>A new value of the <c>launch</c> label, unique to one create call.</summary>
  private static string NewLaunchId() => Guid.NewGuid().ToString("N");

  private void Log(string tenantId, string message) => _output.WriteLine($"[{tenantId}] {message}");

  private void WriteTable(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
  {
    var lines = rows.Prepend(header).ToArray();
    var widths = Enumerable
      .Range(0, header.Count)
      .Select(column => lines.Max(line => line[column].Length))
      .ToArray();
    foreach (var line in lines)
    {
      _output.WriteLine(
        string.Join(
            "  ",
            line.Select(
              (cell, column) => column == line.Count - 1 ? cell : cell.PadRight(widths[column])
            )
          )
          .TrimEnd()
      );
    }
  }

  private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

  private static string Seconds(TimeSpan value) =>
    value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

/// <summary>What a rollout did.</summary>
/// <param name="Replaced">Tenants whose renderer was replaced and whose old sandbox is gone.</param>
/// <param name="Retired">
/// Tenants whose stopped renderer was retired instead, with <c>--stopped retire</c>.
/// </param>
/// <param name="AlreadyCurrent">How many renderers were already on the disk image.</param>
/// <param name="Failures">Each tenant that failed, with the reason.</param>
/// <param name="Pruned">The leftover renderer sandboxes the rollout deleted at its end.</param>
internal sealed record RolloutResult(
  IReadOnlyList<string> Replaced,
  IReadOnlyList<string> Retired,
  int AlreadyCurrent,
  IReadOnlyDictionary<string, string> Failures,
  IReadOnlyList<string> Pruned
);

/// <summary>What ensuring a tenant's renderer did.</summary>
/// <param name="Record">The tenant's record.</param>
/// <param name="Created">Whether the call created the renderer rather than finding it.</param>
internal sealed record EnsureResult(RendererRecord Record, bool Created);

/// <param name="Retired">Tenants whose renderers were retired.</param>
/// <param name="Failures">Tenants whose retirement failed, with why.</param>
internal sealed record RetireResult(
  IReadOnlyList<string> Retired,
  IReadOnlyDictionary<string, string> Failures
);

/// <summary>What a prune did.</summary>
/// <param name="Deleted">The leftover renderer sandboxes deleted.</param>
/// <param name="Failures">Each tenant whose leftover could not be deleted, with the reason.</param>
internal sealed record PruneResult(
  IReadOnlyList<string> Deleted,
  IReadOnlyDictionary<string, string> Failures
);
