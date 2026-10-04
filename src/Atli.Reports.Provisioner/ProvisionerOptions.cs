using System.Collections;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Microsoft.Extensions.Configuration;

namespace Atli.Reports.Provisioner;

/// <summary>
/// The <c>Provisioner</c> configuration section: where renderers and their records live, and how new
/// renderers are made.
/// </summary>
internal sealed class ProvisionerOptions
{
  public const string SectionName = "Provisioner";

  /// <summary>The renderer sandbox group, which the provisioner holds the Data Owner role on.</summary>
  public SandboxesOptions Sandboxes { get; set; } = new();

  /// <summary>Where the records the gateway routes by are kept.</summary>
  public RendererRecordStoreOptions Records { get; set; } = new();

  /// <summary>The disk image new renderers start from, unless a command names one.</summary>
  public string DiskImageId { get; set; } = "";

  /// <summary>The size of new renderers, unless <c>create</c> names one: <c>S</c>, <c>M</c>, or <c>L</c>.</summary>
  public string Size { get; set; } = RendererSize.Medium.Name;

  /// <summary>How long a renderer may go without traffic before the platform suspends it.</summary>
  public TimeSpan AutoSuspendAfter { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>How long a new renderer may take to answer <c>/health/ready</c> with <c>200</c>.</summary>
  public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(3);

  /// <summary>
  /// How long a rollout keeps a replaced renderer after its record points elsewhere: the gateway's
  /// 30-second record cache, then its 90-second request timeout, then a margin.
  /// </summary>
  public TimeSpan DrainDelay { get; set; } = TimeSpan.FromSeconds(150);

  /// <summary><see cref="Size"/>, parsed; <see cref="Load"/> has checked it.</summary>
  public RendererSize RendererSize => RendererSize.Parse(Size);

  /// <summary>
  /// Binds the <c>Provisioner</c> section and checks it. Throws <see cref="InvalidOperationException"/>
  /// naming the setting that is missing or wrong.
  /// </summary>
  public static ProvisionerOptions Load(IConfiguration configuration)
  {
    ProvisionerOptions options = new();
    configuration.GetSection(SectionName).Bind(options);
    options.Sandboxes.Validate();
    if (!RendererSizes.TryParse(options.Size, out _))
    {
      throw new InvalidOperationException(
        $"{SectionName}:Size is '{options.Size}'; use S, M, or L."
      );
    }

    if (options.AutoSuspendAfter <= TimeSpan.Zero)
    {
      throw new InvalidOperationException($"{SectionName}:AutoSuspendAfter must be positive.");
    }

    if (options.ReadyTimeout <= TimeSpan.Zero)
    {
      throw new InvalidOperationException($"{SectionName}:ReadyTimeout must be positive.");
    }

    if (options.DrainDelay < TimeSpan.Zero)
    {
      throw new InvalidOperationException($"{SectionName}:DrainDelay cannot be negative.");
    }

    return options;
  }
}

/// <summary>
/// Builds the configuration the options come from: <c>appsettings.json</c> next to the binary, then
/// environment variables named <c>Provisioner__…</c>. Command-line flags go on top; see
/// <see cref="ProvisionerCli"/>.
/// </summary>
internal static class ProvisionerConfiguration
{
  public const string EnvironmentPrefix = ProvisionerOptions.SectionName + "__";

  /// <param name="directory">The directory that may hold <c>appsettings.json</c>.</param>
  /// <param name="environment">The process environment, as <see cref="Environment.GetEnvironmentVariables()"/> returns it.</param>
  public static IConfiguration Build(string directory, IDictionary environment)
  {
    ArgumentNullException.ThrowIfNull(environment);
    // As the standard environment-variable provider maps them, but only the provisioner's own: the
    // rest of the environment never enters the configuration.
    Dictionary<string, string?> variables = new(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry entry in environment)
    {
      if (
        entry.Key is string name
        && name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)
      )
      {
        variables[name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)] =
          entry.Value as string;
      }
    }

    return new ConfigurationBuilder()
      .AddJsonFile(
        Path.Combine(Path.GetFullPath(directory), "appsettings.json"),
        optional: true,
        reloadOnChange: false
      )
      .AddInMemoryCollection(variables)
      .Build();
  }
}
