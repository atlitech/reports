using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Support;

/// <summary>
/// Builds engines that run the Chrome (or Chromium) installed on the machine.
/// </summary>
/// <remarks>
/// The browser runs without its sandbox: Ubuntu 24.04 runners block the user namespaces the sandbox
/// needs, and the HTML here is trusted. Timeouts are generous because a two-core CI runner can be slow
/// to start a browser.
/// </remarks>
internal static class TestEngine
{
  public static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(30);

  public static ServiceProvider Create(
    Action<ReportsEngineOptions>? configure = null,
    Action<IServiceCollection>? configureServices = null
  )
  {
    ServiceCollection services = new();
    services.AddReportsEngine(options =>
    {
      ConfigureForTests(options);
      configure?.Invoke(options);
    });
    configureServices?.Invoke(services);
    return services.BuildServiceProvider();
  }

  public static void ConfigureForTests(ReportsEngineOptions options)
  {
    options.Browser.NoSandbox = true;
    options.Browser.DisableDevShmUsage = true;
    options.Browser.StartupTimeout = TimeSpan.FromSeconds(60);
    options.Browser.CommandTimeout = GenerousTimeout;
  }

  public static async Task<byte[]> ConvertToBytesAsync(
    this IHtmlToPdfConverter converter,
    string html,
    PdfOptions? options = null
  )
  {
    var result = await converter.ConvertAsync(
      html,
      options,
      TestContext.Current!.Execution.CancellationToken
    );
    if (result.TryPickT1(out var error, out var pdf))
    {
      throw new InvalidOperationException($"The conversion failed: {Describe(error)}");
    }

    await using (pdf)
    {
      using MemoryStream copy = new();
      await pdf.CopyToAsync(copy);
      return copy.ToArray();
    }
  }

  public static string Describe(object value) =>
    value is ConversionError error
      ? $"{error.Kind}: {error.Message} {error.Exception}"
      : "succeeded";

  /// <summary>
  /// The ids of running processes whose command line mentions <paramref name="text"/>, for example a
  /// profile directory. Unix only.
  /// </summary>
  public static async Task<IReadOnlyList<int>> FindProcessesAsync(string text)
  {
    ProcessStartInfo startInfo = new("ps", "-axo pid=,command=")
    {
      RedirectStandardOutput = true,
      UseShellExecute = false,
    };
    using var ps = Process.Start(startInfo)!;
    var output = await ps.StandardOutput.ReadToEndAsync();
    await ps.WaitForExitAsync();

    List<int> ids = [];
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
      var trimmed = line.TrimStart();
      var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
      if (
        space > 0
        && trimmed[space..].Contains(text, StringComparison.Ordinal)
        && !trimmed[space..].Contains("ps -axo", StringComparison.Ordinal)
      )
      {
        ids.Add(int.Parse(trimmed[..space], CultureInfo.InvariantCulture));
      }
    }

    return ids;
  }

  /// <summary>
  /// Waits until <paramref name="condition"/> holds, for at most <paramref name="timeout"/>.
  /// </summary>
  public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout)
  {
    var stopwatch = Stopwatch.StartNew();
    while (true)
    {
      if (await condition())
      {
        return true;
      }

      if (stopwatch.Elapsed > timeout)
      {
        return false;
      }

      await Task.Delay(50);
    }
  }
}
