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
/// Progress goes to the output, prefixed with the tenant. It names tenants, sandboxes, URLs, and disk
/// images; credentials and verifiers never reach it.
/// </remarks>
internal sealed class RendererProvisioner
{
  /// <summary>The port the server image listens on.</summary>
  public const int RendererPort = 8080;

  /// <summary>How often a new renderer is asked whether it is ready.</summary>
  public static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

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
    var existing = await _records.GetAsync(tenantId, cancellationToken);
    if (existing is not null)
    {
      throw new ProvisioningException(
        $"Tenant {tenantId} already has a renderer (sandbox {existing.SandboxId ?? "none"}). "
          + "Replace it with rollout, or delete it first."
      );
    }

    var (record, sandbox) = await LaunchAsync(tenantId, size, diskImageId, cancellationToken);
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
  /// <paramref name="diskImageId"/>, suspended ones included. Each replacement is created as
  /// <see cref="CreateAsync"/> creates one, with a new credential and the old renderer's size; once
  /// it is ready its record replaces the old one in one step, and the old sandbox is deleted
  /// <paramref name="drain"/> later. Renderers already on the image are skipped, so a rollout that
  /// failed for some tenants can simply run again.
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
    IReadOnlyList<RendererRecord> records = tenantId is null
      ? await _records.ListAsync(cancellationToken)
      :
      [
        await _records.GetAsync(tenantId, cancellationToken)
          ?? throw new ProvisioningException($"Tenant {tenantId} has no renderer; create one."),
      ];
    var outdated = records
      .Where(record => !string.Equals(record.DiskImageId, diskImageId, StringComparison.Ordinal))
      .OrderBy(record => record.TenantId, StringComparer.Ordinal)
      .ToArray();
    var current = records.Count - outdated.Length;
    _output.WriteLine(
      $"Rollout of disk image {diskImageId}: {outdated.Length} to replace, {current} already on it. "
        + $"{maxParallel} at a time; old sandboxes are deleted {Format(drain)} after their record moves."
    );

    ConcurrentQueue<string> replaced = new();
    ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
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
            var (record, _) = await LaunchAsync(old.TenantId, size, diskImageId, token);
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

    RolloutResult result = new(
      [.. replaced.Order(StringComparer.Ordinal)],
      current,
      new SortedDictionary<string, string>(failures, StringComparer.Ordinal)
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
  /// <paramref name="drain"/>, its sandboxes: the record's, and any other labeled for the tenant,
  /// such as one an earlier delete or a failed rollout left behind. Deleting a tenant that has
  /// neither succeeds.
  /// </summary>
  public async Task DeleteAsync(
    string tenantId,
    TimeSpan drain,
    CancellationToken cancellationToken
  )
  {
    var record = await _records.GetAsync(tenantId, cancellationToken);
    if (record is not null)
    {
      await _records.DeleteAsync(tenantId, cancellationToken);
      Log(
        tenantId,
        "Deleted the record; the gateway stops routing to the renderer once its cached copy expires."
      );
      if (drain > TimeSpan.Zero)
      {
        Log(tenantId, $"Waiting {Format(drain)} before deleting the sandbox.");
        await Task.Delay(drain, _time, cancellationToken);
      }
    }

    SortedSet<string> sandboxIds = new(StringComparer.Ordinal);
    if (record?.SandboxId is { } recorded)
    {
      sandboxIds.Add(recorded);
    }

    foreach (var sandbox in await _sandboxes.ListAsync(cancellationToken))
    {
      if (RendererLabels.TenantOf(sandbox) == tenantId)
      {
        sandboxIds.Add(sandbox.Id);
      }
    }

    foreach (var sandboxId in sandboxIds)
    {
      await _sandboxes.DeleteAsync(sandboxId, cancellationToken);
      Log(tenantId, $"Deleted sandbox {sandboxId}.");
    }

    if (record is null && sandboxIds.Count == 0)
    {
      Log(tenantId, "No renderer to delete.");
    }
  }

  /// <summary>
  /// Writes every record with the current state of its sandbox, then any renderer sandbox no record
  /// points to: one being created, or one a failed or canceled command left behind.
  /// </summary>
  public async Task ListAsync(CancellationToken cancellationToken)
  {
    var records = (await _records.ListAsync(cancellationToken))
      .OrderBy(record => record.TenantId, StringComparer.Ordinal)
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
  /// Starts a renderer for the tenant and points its record at it: a sandbox with a new credential,
  /// its port exposed, <c>/health/ready</c> answering <c>200</c>, and then the record written in one
  /// step. If any of that fails or is canceled, the sandbox is deleted again and the record is left
  /// as it was.
  /// </summary>
  private async Task<(RendererRecord Record, SandboxView Sandbox)> LaunchAsync(
    string tenantId,
    RendererSize size,
    string diskImageId,
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
      Labels = RendererLabels.For(tenantId, size),
      EgressDefaultAction = "Deny",
      AutoSuspendAfter = _options.AutoSuspendAfter,
    };
    Log(
      tenantId,
      $"Creating a sandbox from disk image {diskImageId}, size {size.Name} "
        + $"({size.Cpu} CPU, {size.Memory} memory)."
    );
    var started = _time.GetTimestamp();
    var created = await _sandboxes.CreateAsync(spec, cancellationToken);
    try
    {
      var createdAt = _time.GetUtcNow();
      Log(tenantId, $"Created sandbox {created.Id}; exposing port {RendererPort}.");
      var sandbox = await _sandboxes.AddPortAsync(
        created.Id,
        RendererPort,
        // The port URL is public; the renderer admits only the gateway's credential.
        anonymous: true,
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
      RendererRecord record = new()
      {
        TenantId = tenantId,
        Url = url,
        ApiKey = credential.Credential,
        SandboxId = created.Id,
        DiskImageId = diskImageId,
        CreatedAt = createdAt,
      };
      await _records.PutAsync(record, cancellationToken);
      return (record, sandbox);
    }
    catch
    {
      await DeleteLeftoverAsync(tenantId, created.Id);
      throw;
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
  /// drain, which outlasts the gateway's record cache and its longest request. Never throws.
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
        $"replaced, but the old sandbox {old.SandboxId} was not deleted ({reason}); delete it by hand.";
      Log(old.TenantId, $"The old sandbox {old.SandboxId} was not deleted: {reason}");
    }
  }

  /// <summary>
  /// Deletes a sandbox a failed command created, even when the command was canceled. A failure here
  /// is written, not thrown: the command's own failure is the one to report.
  /// </summary>
  private async Task DeleteLeftoverAsync(string tenantId, string sandboxId)
  {
    try
    {
      await _sandboxes.DeleteAsync(sandboxId, CancellationToken.None);
      Log(tenantId, $"Deleted sandbox {sandboxId} again.");
    }
    catch (Exception exception)
    {
      Log(
        tenantId,
        $"Could not delete sandbox {sandboxId}; delete it by hand ({exception.Message})."
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
internal sealed record RolloutResult(
  IReadOnlyList<string> Replaced,
  int AlreadyCurrent,
  IReadOnlyDictionary<string, string> Failures
);
