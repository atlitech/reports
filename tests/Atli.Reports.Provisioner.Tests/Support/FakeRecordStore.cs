using System.Collections.Concurrent;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>Renderer records in memory.</summary>
internal sealed class FakeRecordStore(Journal journal) : IRendererRecordStore
{
  private readonly ConcurrentDictionary<string, RendererRecord> _records = new(
    StringComparer.Ordinal
  );

  /// <summary>Fails every put with this exception, when set.</summary>
  public Exception? FailPut { get; set; }

  public int Count => _records.Count;

  public RendererRecord? this[string tenantId] => _records.GetValueOrDefault(tenantId);

  /// <summary>Adds a record that exists before the test runs.</summary>
  public void Add(RendererRecord record) => _records[record.TenantId] = record;

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken) =>
    Task.FromResult(this[tenantId]);

  public Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<RendererRecord>>([.. _records.Values]);

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    journal.Add($"put {record.TenantId} -> {record.SandboxId}");
    if (FailPut is not null)
    {
      throw FailPut;
    }

    _records[record.TenantId] = record;
    return Task.CompletedTask;
  }

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    journal.Add($"delete record {tenantId}");
    _records.TryRemove(tenantId, out _);
    return Task.CompletedTask;
  }
}
