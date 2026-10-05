using System.Collections.Concurrent;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>Renderer records in memory.</summary>
internal sealed class FakeRecordStore(Journal journal) : IRendererRecordStore
{
  private readonly ConcurrentDictionary<string, RendererRecord> _records = new(
    StringComparer.Ordinal
  );

  private readonly ConcurrentDictionary<string, string> _unreadable = new(StringComparer.Ordinal);

  /// <summary>Fails every put with this exception, when set.</summary>
  public Exception? FailPut { get; set; }

  /// <summary>
  /// Fails the next put with this exception after the record is written, as a write whose answer
  /// was lost; cleared once thrown.
  /// </summary>
  public Exception? FailPutAfterWriting { get; set; }

  /// <summary>Fails every get with this exception, when set.</summary>
  public Exception? FailGet { get; set; }

  /// <summary>Fails the next get with this exception; cleared once thrown.</summary>
  public Exception? FailNextGet { get; set; }

  /// <summary>Runs before each put, when set.</summary>
  public Action? BeforePut { get; set; }

  public int Count => _records.Count;

  public RendererRecord? this[string tenantId] => _records.GetValueOrDefault(tenantId);

  /// <summary>Adds a record that exists before the test runs.</summary>
  public void Add(RendererRecord record) => _records[record.TenantId] = record;

  /// <summary>Stores a record for <paramref name="tenantId"/> that cannot be read.</summary>
  public void AddUnreadable(string tenantId) =>
    _unreadable[tenantId] = $"The record of {tenantId} is damaged.";

  /// <summary>Whether a record that cannot be read is stored for <paramref name="tenantId"/>.</summary>
  public bool HasUnreadable(string tenantId) => _unreadable.ContainsKey(tenantId);

  public Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    if (FailGet is { } failure)
    {
      return Task.FromException<RendererRecord?>(failure);
    }

    if (FailNextGet is { } once)
    {
      FailNextGet = null;
      return Task.FromException<RendererRecord?>(once);
    }

    return _unreadable.TryGetValue(tenantId, out var reason)
      ? Task.FromException<RendererRecord?>(new InvalidDataException(reason))
      : Task.FromResult(this[tenantId]);
  }

  /// <summary>Every tenant with a record, unreadable ones included, as the real stores list them.</summary>
  public Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<string>>([
      .. _records.Keys.Union(_unreadable.Keys).Order(StringComparer.Ordinal),
    ]);

  public Task<RendererRecordListing> ListWithUnreadableAsync(CancellationToken cancellationToken) =>
    Task.FromResult(
      new RendererRecordListing(
        [.. _records.Values.OrderBy(record => record.TenantId, StringComparer.Ordinal)],
        [
          .. _unreadable
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UnreadableRendererRecord(pair.Key, pair.Value)),
        ]
      )
    );

  public Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    journal.Add($"put {record.TenantId} -> {record.SandboxId}");
    BeforePut?.Invoke();
    if (FailPut is not null)
    {
      throw FailPut;
    }

    _unreadable.TryRemove(record.TenantId, out _);
    _records[record.TenantId] = record;
    if (FailPutAfterWriting is { } lost)
    {
      FailPutAfterWriting = null;
      throw lost;
    }

    return Task.CompletedTask;
  }

  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    journal.Add($"delete record {tenantId}");
    _records.TryRemove(tenantId, out _);
    _unreadable.TryRemove(tenantId, out _);
    return Task.CompletedTask;
  }
}
