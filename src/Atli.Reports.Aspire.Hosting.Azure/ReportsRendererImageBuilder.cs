using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

internal static class ReportsRendererImageBuilder
{
  private static readonly string[] SourcePaths =
  [
    "global.json",
    "Directory.Build.props",
    "Directory.Packages.props",
    ".editorconfig",
    "src/Atli.Reports.Engine",
    "src/Atli.Reports.Client",
    "src/Atli.Reports.Hosting",
    "src/Atli.Reports.Server",
  ];

  internal static async Task CheckPrerequisitesAsync(
    ReportsRendererImageResource resource,
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(Path.Combine(resource.RepositoryRoot, "src/Atli.Reports.Server/Dockerfile")))
    {
      throw new InvalidOperationException(
        "The renderer source must be an Atli Reports checkout containing src/Atli.Reports.Server/Dockerfile."
      );
    }
    await RunAsync("git", ["--version"], null, cancellationToken).ConfigureAwait(false);
    await RunAsync("aca", ["--version"], null, cancellationToken).ConfigureAwait(false);
    await RunAsync(
        "git",
        ["-C", resource.RepositoryRoot, "rev-parse", "--show-toplevel"],
        null,
        cancellationToken
      )
      .ConfigureAwait(false);
  }

  internal static async Task<string> BuildAsync(
    ReportsRendererImageResource resource,
    CancellationToken cancellationToken
  )
  {
    var azure = resource.Environment;
    Dictionary<string, string> environment = new(StringComparer.Ordinal)
    {
      ["ACA_SUBSCRIPTION"] = await RequiredValueAsync(azure.SubscriptionId, cancellationToken)
        .ConfigureAwait(false),
      ["ACA_RESOURCE_GROUP"] = await RequiredValueAsync(azure.ResourceGroupName, cancellationToken)
        .ConfigureAwait(false),
      ["ACA_SANDBOX_GROUP"] = await RequiredValueAsync(azure.SandboxGroupName, cancellationToken)
        .ConfigureAwait(false),
      ["ACA_REGION"] = await RequiredValueAsync(azure.Location, cancellationToken)
        .ConfigureAwait(false),
    };
    var staging = Directory.CreateTempSubdirectory("atli-reports-renderer-").FullName;
    try
    {
      var name = await StageSourcesAsync(resource.RepositoryRoot, staging, cancellationToken)
        .ConfigureAwait(false);
      var existing = await FindReadyImageAsync(name, environment, cancellationToken)
        .ConfigureAwait(false);
      if (existing is not null)
      {
        return existing;
      }

      // The CLI builds remotely on linux/amd64, as in the measured Azure runs. No source-tree
      // paths or credentials are interpolated through a shell, and only tracked build inputs ship.
      await RunAcaWithRoleRetryAsync(
          [
            "sandboxgroup",
            "disk",
            "create",
            "--source",
            staging,
            "--name",
            name,
            "--wait-timeout",
            "3000",
          ],
          environment,
          cancellationToken
        )
        .ConfigureAwait(false);
      return await FindReadyImageAsync(name, environment, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(
          "The renderer disk build completed without a Ready disk image. Inspect the sandbox group's disk images before retrying deployment."
        );
    }
    finally
    {
      Directory.Delete(staging, recursive: true);
    }
  }

  internal static async Task<string> StageSourcesAsync(
    string repositoryRoot,
    string staging,
    CancellationToken cancellationToken
  )
  {
    repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
    var arguments = new List<string> { "-C", repositoryRoot, "ls-files", "-z", "--" };
    arguments.AddRange(SourcePaths);
    var listing = await RunAsync("git", arguments, null, cancellationToken).ConfigureAwait(false);
    var files = listing
      .Split('\0', StringSplitOptions.RemoveEmptyEntries)
      .Order(StringComparer.Ordinal)
      .ToArray();
    if (!files.Contains("src/Atli.Reports.Server/Dockerfile", StringComparer.Ordinal))
    {
      throw new InvalidOperationException(
        "The renderer Dockerfile must be tracked in the Reports checkout."
      );
    }
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var relative in files)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var source = Path.Combine(repositoryRoot, relative);
      if (!File.Exists(source))
      {
        continue; // Respect tracked files deleted in the working tree.
      }
      // Never follow a tracked symlink outside the reviewed source context.
      var component = source;
      while (!string.Equals(component, repositoryRoot, StringComparison.Ordinal))
      {
        if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
        {
          throw new InvalidOperationException(
            $"Renderer build inputs cannot be symbolic links: {relative}."
          );
        }
        component =
          Path.GetDirectoryName(component)
          ?? throw new InvalidOperationException("A renderer source path escaped its checkout.");
      }
      var destination = Path.Combine(staging, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
      hash.AppendData(Encoding.UTF8.GetBytes(relative));
      hash.AppendData([0]);
      hash.AppendData(SHA256.HashData(bytes));
      await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
    }
    File.Copy(
      Path.Combine(staging, "src/Atli.Reports.Server/Dockerfile"),
      Path.Combine(staging, "Dockerfile")
    );
    return "reports-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..24];
  }

  private static async Task<string?> FindReadyImageAsync(
    string name,
    IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken
  )
  {
    var listing = await RunAcaWithRoleRetryAsync(
        ["sandboxgroup", "disk", "list", "-o", "json"],
        environment,
        cancellationToken
      )
      .ConfigureAwait(false);
    using var document = JsonDocument.Parse(listing);
    foreach (var disk in document.RootElement.EnumerateArray())
    {
      if (
        disk.TryGetProperty("name", out var diskName)
        && diskName.GetString() == name
        && disk.TryGetProperty("id", out var diskId)
        && diskId.GetString() is { Length: > 0 } id
      )
      {
        var detail = await RunAcaWithRoleRetryAsync(
            ["sandboxgroup", "disk", "get", "--id", id, "-o", "json"],
            environment,
            cancellationToken
          )
          .ConfigureAwait(false);
        if (IsReadyImage(detail))
        {
          return id;
        }
      }
    }
    return null;
  }

  internal static bool IsReadyImage(string json)
  {
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty("status", out var status)
      && status.ValueKind == JsonValueKind.Object
      && status.TryGetProperty("state", out var state)
      && string.Equals(state.GetString(), "Ready", StringComparison.OrdinalIgnoreCase);
  }

  private static async Task<string> RequiredValueAsync(
    BicepOutputReference value,
    CancellationToken cancellationToken
  ) =>
    await value.GetValueAsync(cancellationToken).ConfigureAwait(false)
    ?? throw new InvalidOperationException(
      "The Reports environment did not produce a required Azure deployment output."
    );

  private static async Task<string> RunAcaWithRoleRetryAsync(
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken
  )
  {
    for (var attempt = 0; ; attempt++)
    {
      try
      {
        return await RunAsync("aca", arguments, environment, cancellationToken)
          .ConfigureAwait(false);
      }
      catch (ReportsRendererCommandException exception)
        when (attempt < 80 && exception.IsRolePropagation)
      {
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
      }
    }
  }

  private static async Task<string> RunAsync(
    string executable,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string>? environment,
    CancellationToken cancellationToken
  )
  {
    ProcessStartInfo start = new(executable)
    {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = true,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }
    if (environment is not null)
    {
      foreach (var (key, value) in environment)
      {
        start.Environment[key] = value;
      }
    }
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromHours(1));
    using var process = new Process { StartInfo = start };
    try
    {
      process.Start();
    }
    catch (Win32Exception exception)
    {
      throw new InvalidOperationException(
        $"Cannot start {executable}. Install Git and the Azure Sandboxes ACA CLI (https://aka.ms/aca-cli-install) before aspire deploy.",
        exception
      );
    }
    process.StandardInput.Close();
    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var error = process.StandardError.ReadToEndAsync(timeout.Token);
    try
    {
      await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
      var stdout = await output.ConfigureAwait(false);
      var stderr = await error.ConfigureAwait(false);
      if (process.ExitCode != 0)
      {
        throw new ReportsRendererCommandException(executable, process.ExitCode, stderr);
      }
      return stdout;
    }
    catch (OperationCanceledException)
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
      }
      throw;
    }
  }

  private sealed class ReportsRendererCommandException(
    string executable,
    int exitCode,
    string error
  )
    : InvalidOperationException(
      $"{executable} failed with exit code {exitCode} while preparing the renderer disk. Check Azure CLI authentication, sandbox preview access and the source Dockerfile. CLI output is withheld because it can contain credentials."
    )
  {
    public bool IsRolePropagation =>
      error.Contains("403", StringComparison.Ordinal)
      || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
  }
}
