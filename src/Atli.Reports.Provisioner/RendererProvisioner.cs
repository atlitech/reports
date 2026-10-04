using System.Collections.Concurrent;
using System.Globalization;
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

  public RendererProvisioner(
    ISandboxesClient sandboxes,
    IRendererRecordStore records,
    IReadinessProbe readiness,
    TimeProvider time,
    TextWriter output,
    ProvisionerOptions options
  )
  {
    _sandboxes = sandboxes;
    _records = records;
    _readiness = readiness;
    _time = time;
    // A rollout replaces several tenants' renderers at once.
    _output = TextWriter.Synchronized(output);
    _options = options;
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
  /// <param name="drain">How long an old renderer stays after its record moves.</param>
  /// <param name="cancellationToken">Cancels the rollout; replacements not yet recorded are deleted.</param>
  public async Task<RolloutResult> RolloutAsync(
    string diskImageId,
    string? tenantId,
    int maxParallel,
    TimeSpan drain,
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

    var existing = (await _sandboxes.ListAsync(cancellationToken))
      .Select(sandbox => sandbox.Id)
      .ToHashSet(StringComparer.Ordinal);
    List<RendererRecord> outdated = [];
    foreach (var record in records.OrderBy(record => record.TenantId, StringComparer.Ordinal))
    {
      if (!string.Equals(record.DiskImageId, diskImageId, StringComparison.Ordinal))
      {
        outdated.Add(record);
      }
      else if (record.SandboxId is { } sandboxId && !existing.Contains(sandboxId))
      {
        // On the image, but pointing at a sandbox that is gone: replacing it repairs the tenant.
        Log(record.TenantId, $"Sandbox {sandboxId} of the record no longer exists.");
        outdated.Add(record);
      }
    }

    var current = records.Count - outdated.Count;
    _output.WriteLine(
      $"Rollout of disk image {diskImageId}: {outdated.Count} to replace, {current} already on it"
        + (failures.IsEmpty ? ". " : $", {failures.Count} unreadable. ")
        + $"{maxParallel} at a time; old sandboxes are deleted {Format(drain)} after their record moves."
    );

    ConcurrentQueue<string> replaced = new();
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
      current,
      new SortedDictionary<string, string>(failures, StringComparer.Ordinal),
      pruned
    );
    _output.WriteLine(
      $"Replaced {result.Replaced.Count}, already on the image {result.AlreadyCurrent}, "
        + $"failed {result.Failures.Count}."
    );
    foreach (var (tenant, reason) in result.Failures)
    {
      _output.WriteLine($"  {tenant}: {reason}");
    }

    return result;
  }

  /// <summary>
  /// Deletes the tenant's record first, so the gateway stops routing to its renderer, then, after
  /// <paramref name="drain"/>, its sandboxes: every one labeled for the tenant, such as one an
  /// earlier delete or a failed rollout left behind. Deleting a tenant that has neither succeeds.
  /// </summary>
  /// <remarks>
  /// A record that cannot be read, or that the store disabled, is deleted all the same: destroying a
  /// compromised renderer must not depend on its record. A sandbox the record names that is labeled
  /// for another tenant is never deleted; the command fails after the rest is done.
  /// </remarks>
  public async Task DeleteAsync(
    string tenantId,
    TimeSpan drain,
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

    var sandboxes = await _sandboxes.ListAsync(cancellationToken);
    SortedSet<string> sandboxIds = new(StringComparer.Ordinal);
    foreach (var sandbox in sandboxes)
    {
      if (RendererLabels.TenantOf(sandbox) == tenantId)
      {
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
  private async Task<(RendererRecord Record, SandboxView Sandbox)> LaunchAsync(
    string tenantId,
    RendererSize size,
    string diskImageId,
    RendererRecord? replacing,
    CancellationToken cancellationToken
  )
  {
    var credential = RendererCredential.Generate();
    var launchId = Guid.NewGuid().ToString("N");
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
      await WaitUntilReadyAsync(url, cancellationToken);
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
  /// <c>503</c> until the browser is up.
  /// </summary>
  private async Task WaitUntilReadyAsync(Uri url, CancellationToken cancellationToken)
  {
    var started = _time.GetTimestamp();
    while (true)
    {
      var answer = await _readiness.ProbeAsync(url, cancellationToken);
      if (answer.IsReady)
      {
        return;
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
/// <param name="AlreadyCurrent">How many renderers were already on the disk image.</param>
/// <param name="Failures">Each tenant that failed, with the reason.</param>
/// <param name="Pruned">The leftover renderer sandboxes the rollout deleted at its end.</param>
internal sealed record RolloutResult(
  IReadOnlyList<string> Replaced,
  int AlreadyCurrent,
  IReadOnlyDictionary<string, string> Failures,
  IReadOnlyList<string> Pruned
);

/// <summary>What a prune did.</summary>
/// <param name="Deleted">The leftover renderer sandboxes deleted.</param>
/// <param name="Failures">Each tenant whose leftover could not be deleted, with the reason.</param>
internal sealed record PruneResult(
  IReadOnlyList<string> Deleted,
  IReadOnlyDictionary<string, string> Failures
);
