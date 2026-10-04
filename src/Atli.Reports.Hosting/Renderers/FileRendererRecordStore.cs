using Microsoft.Win32.SafeHandles;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// Records as one JSON file per tenant in a directory, written atomically (write, then rename) and
/// readable by their owner only. For development, tests, and single-machine deployments.
/// </summary>
/// <remarks>
/// <para>
/// A record is <c>&lt;directory&gt;/&lt;tenant&gt;.json</c>. On Unix the directory is created
/// <c>0700</c> and every record file <c>0600</c>, since a record holds the raw credential its
/// renderer admits. A replacement is written to a temporary file in the same directory and renamed
/// over the old record, so a reader opens the old file or the new one, never a partial write.
/// </para>
/// <para>
/// Whoever can write to the directory can route a tenant anywhere, so on Unix the store refuses a
/// directory that is writable by its group or by others or that belongs to another user, and a
/// record file that belongs to another user. Ownership is tested by setting the file's mode to what
/// it already is, which only its owner (or root, who is not refused) may do: .NET exposes no owner.
/// </para>
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
  /// <remarks>
  /// Throws <see cref="UnauthorizedAccessException"/> for a directory or record file another user
  /// could have written.
  /// </remarks>
  public async Task<RendererRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    CheckDirectory();
    return await ReadAsync(tenantId, cancellationToken);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<RendererRecord>> ListAsync(CancellationToken cancellationToken) =>
    (await ListWithUnreadableAsync(cancellationToken)).Records;

  /// <inheritdoc />
  /// <remarks>From the file names alone; files that are not records are skipped.</remarks>
  public Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    CheckDirectory();
    return Task.FromResult<IReadOnlyList<string>>([.. TenantFiles()]);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Files that are not records by name, such as a write in progress, are skipped. A record file
  /// that cannot be parsed, holds another tenant's record, or belongs to another user is reported.
  /// </remarks>
  public async Task<RendererRecordListing> ListWithUnreadableAsync(
    CancellationToken cancellationToken
  )
  {
    CheckDirectory();
    List<RendererRecord> records = [];
    List<UnreadableRendererRecord> unreadable = [];
    foreach (var tenantId in TenantFiles())
    {
      try
      {
        // Null when the record was deleted since the directory was listed.
        if (await ReadAsync(tenantId, cancellationToken) is { } record)
        {
          records.Add(record);
        }
      }
      catch (Exception exception)
        when (exception is InvalidDataException or UnauthorizedAccessException)
      {
        unreadable.Add(new UnreadableRendererRecord(tenantId, exception.Message));
      }
    }

    return new RendererRecordListing(records, unreadable);
  }

  /// <inheritdoc />
  public async Task PutAsync(RendererRecord record, CancellationToken cancellationToken)
  {
    RendererRecordJson.Validate(record);
    CreateDirectory();
    // An operator's existing directory keeps its mode, so it is checked like any other.
    CheckDirectory();

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
  /// <remarks>Deletes the file whatever it holds, so a damaged record can always be deleted.</remarks>
  public Task DeleteAsync(string tenantId, CancellationToken cancellationToken)
  {
    TenantId.Validate(tenantId);
    cancellationToken.ThrowIfCancellationRequested();
    CheckDirectory();
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

  /// <summary>The tenants that have a record file, by name, in ordinal order.</summary>
  private IEnumerable<string> TenantFiles() =>
    Directory.Exists(_directory)
      ? Directory
        .EnumerateFiles(_directory, "*" + Extension)
        .Select(Path.GetFileNameWithoutExtension)
        .OfType<string>()
        .Where(TenantId.IsValid)
        .Order(StringComparer.Ordinal)
      : [];

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
      // The file opened, not the name: a rename since cannot swap in another file.
      if (!OperatingSystem.IsWindows() && !IsOwnedByCurrentUser(stream.SafeFileHandle))
      {
        throw new UnauthorizedAccessException(
          $"The renderer record file {path} belongs to another user."
        );
      }

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

  /// <summary>
  /// Throws <see cref="UnauthorizedAccessException"/> for a directory another user could write
  /// records into; a missing one is fine. Not on Windows, whose access control lists are the
  /// operator's to set.
  /// </summary>
  private void CheckDirectory()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    UnixFileMode mode;
    try
    {
      mode = File.GetUnixFileMode(_directory);
    }
    catch (Exception exception)
      when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      return;
    }

    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
    {
      throw new UnauthorizedAccessException(
        $"The renderer record directory {_directory} is writable by other users; "
          + "only its owner may write to it (chmod 700)."
      );
    }

    // Setting the mode it has changes nothing, and only its owner and root may: see
    // IsOwnedByCurrentUser.
    try
    {
      File.SetUnixFileMode(_directory, mode);
    }
    catch (UnauthorizedAccessException)
    {
      throw new UnauthorizedAccessException(
        $"The renderer record directory {_directory} belongs to another user."
      );
    }
    catch (IOException)
    {
      // Mounted read-only: no one can change it, so no one else can write records into it either.
    }
  }

  /// <summary>
  /// Whether the open file belongs to the current user (or the current user is root): whether
  /// setting its mode to the one it has, which changes nothing, is allowed. A file system that
  /// refuses every change (mounted read-only) cannot tell, and no other user can write to it either,
  /// so its files pass.
  /// </summary>
  [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
  private static bool IsOwnedByCurrentUser(SafeFileHandle file)
  {
    try
    {
      File.SetUnixFileMode(file, File.GetUnixFileMode(file));
      return true;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
    catch (IOException)
    {
      return true;
    }
  }
}
