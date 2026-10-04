namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as one JSON file per tenant in a directory, written atomically (write, then rename) and
/// readable by their owner only. For development, tests, and single-machine deployments.
/// </summary>
/// <remarks>
/// A record is <c>&lt;directory&gt;/&lt;tenant&gt;.json</c>. On Unix the directory is created
/// <c>0700</c> and every record file <c>0600</c>, since a record holds the raw credential its
/// renderer admits. A replacement is written to a temporary file in the same directory and renamed
/// over the old record, so a reader opens the old file or the new one, never a partial write.
/// </remarks>
public sealed class FileRendererRecordStore : IRendererRecordStore
{
  private const string Extension = ".json";

  private readonly string _directory;

  /// <summary>Creates the store.</summary>
  /// <param name="directory">The directory; created when missing.</param>
  public FileRendererRecordStore(string directory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    _directory = Path.GetFullPath(directory);
  }

  /// <inheritdoc />
  public async Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    return await ReadAsync(tenantId, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>Files that are not records, such as a write in progress, are skipped.</remarks>
  public async Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken)
  {
    if (!Directory.Exists(_directory))
    {
      return [];
    }

    List<RendererRecord> records = [];
    foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension))
    {
      var tenantId = Path.GetFileNameWithoutExtension(file);
      if (!TenantId.IsValid(tenantId))
      {
        continue;
      }

      // Null when the record was deleted since the directory was listed.
      if (await ReadAsync(tenantId, cancellationToken) is { } record)
      {
        records.Add(record);
      }
    }

    return [.. records.OrderBy(record => record.TenantId, StringComparer.Ordinal)];
  }

  /// <inheritdoc />
  public async Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    RendererRecordJson.Validate(record);
    CreateDirectory();

    // Starts with a dot and ends in .tmp, so no reader takes it for a record.
    var temporary = Path.Combine(_directory, $".{record.TenantId}.{Guid.NewGuid():N}.tmp");
    try
    {
      FileStreamOptions options = new()
      {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous,
      };
      if (!OperatingSystem.IsWindows())
      {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      }

      await using (FileStream stream = new(temporary, options))
      {
        await RendererRecordJson.SerializeAsync(stream, record, cancellationToken);
        // On disk before the rename, so a crash cannot leave the new name on an empty file.
        stream.Flush(flushToDisk: true);
      }

      File.Move(temporary, PathOf(record.TenantId), overwrite: true);
    }
    catch
    {
      File.Delete(temporary);
      throw;
    }
  }

  /// <inheritdoc />
  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      File.Delete(PathOf(tenantId));
    }
    catch (DirectoryNotFoundException)
    {
      // No directory, no record: deleting it succeeds.
    }

    return Task.CompletedTask;
  }

  private string PathOf(string tenantId) => Path.Combine(_directory, tenantId + Extension);

  private async Task<RendererRecord?> ReadAsync(
    string tenantId,
    CancellationToken cancellationToken
  )
  {
    var path = PathOf(tenantId);
    FileStream stream;
    try
    {
      stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 4096,
        FileOptions.Asynchronous
      );
    }
    catch (Exception exception)
      when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      return null;
    }

    await using (stream)
    {
      return await RendererRecordJson.DeserializeAsync(
        stream,
        tenantId,
        $"The renderer record file {path}",
        cancellationToken
      );
    }
  }

  private void CreateDirectory()
  {
    if (OperatingSystem.IsWindows())
    {
      Directory.CreateDirectory(_directory);
    }
    else
    {
      // Applies only to directories it creates: an operator's existing directory keeps its mode.
      Directory.CreateDirectory(
        _directory,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
      );
    }
  }
}
