namespace Atli.Reports.Provisioner.Tests.Support;

/// <summary>
/// What the fakes were asked to do, in order, across the data plane, the record store, and the
/// readiness probe: <c>create sandbox-1 (a)</c>, <c>put a -> sandbox-1</c>, <c>delete old-a</c>.
/// </summary>
internal sealed class Journal
{
  private readonly Lock _lock = new();
  private readonly List<string> _entries = [];

  public IReadOnlyList<string> Entries
  {
    get
    {
      lock (_lock)
      {
        return [.. _entries];
      }
    }
  }

  public void Add(string entry)
  {
    lock (_lock)
    {
      _entries.Add(entry);
    }
  }

  /// <summary>The entries that start with any of <paramref name="prefixes"/>, in order.</summary>
  public IReadOnlyList<string> Matching(params string[] prefixes) =>
    [
      .. Entries.Where(entry =>
        prefixes.Any(prefix => entry.StartsWith(prefix, StringComparison.Ordinal))
      ),
    ];
}
