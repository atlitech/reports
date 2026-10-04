using System.Collections;
using System.Globalization;
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

  /// <summary>The longest <see cref="DrainDelay"/>: past the gateway's record cache and longest request many times over.</summary>
  public static readonly TimeSpan MaxDrainDelay = TimeSpan.FromHours(1);

  /// <summary>The longest <see cref="ReadyTimeout"/>; a renderer was ready in 1.4 to 5.2 seconds in the measured runs.</summary>
  public static readonly TimeSpan MaxReadyTimeout = TimeSpan.FromMinutes(15);

  /// <summary>The longest <see cref="AutoSuspendAfter"/>.</summary>
  public static readonly TimeSpan MaxAutoSuspendAfter = TimeSpan.FromHours(24);

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

  /// <summary>
  /// What a request to a stopped renderer's port does: <c>OnDemand</c> (the default) resumes it, so
  /// the gateway needs no resume permission; <c>Manual</c> leaves resuming to the gateway's
  /// <c>Wake:Mode=Sandboxes</c>.
  /// </summary>
  public string PortActivation { get; set; } = nameof(SandboxPortActivation.OnDemand);

  /// <summary>
  /// The source ranges a renderer's port admits, in CIDR notation, such as the gateway's outbound
  /// addresses; empty admits any address. With an anonymous on-demand port, anyone who learns the
  /// URL can otherwise wake the renderer, and run up its compute, before its credential is checked.
  /// The provisioner's own address must be among them: it asks the renderer whether it is ready
  /// through the same port.
  /// </summary>
  public List<string> AllowedSourceCidrs { get; } = [];

  /// <summary><see cref="Size"/>, parsed; <see cref="Load"/> has checked it.</summary>
  public RendererSize RendererSize => RendererSize.Parse(Size);

  /// <summary><see cref="PortActivation"/>, parsed; <see cref="Load"/> has checked it.</summary>
  public SandboxPortActivation Activation =>
    string.Equals(
      PortActivation,
      nameof(SandboxPortActivation.Manual),
      StringComparison.OrdinalIgnoreCase
    )
      ? SandboxPortActivation.Manual
      : SandboxPortActivation.OnDemand;

  /// <summary>How the renderer's port is exposed; <see cref="Load"/> has checked it.</summary>
  public SandboxPortOptions PortOptions =>
    new()
    {
      // The port URL is public; the renderer admits only the gateway's credential.
      Anonymous = true,
      Activation = Activation,
      AllowedSourceCidrs = [.. AllowedSourceCidrs],
    };

  /// <summary>
  /// Binds the <c>Provisioner</c> section and checks it. Throws <see cref="InvalidOperationException"/>
  /// naming the setting that is missing or wrong.
  /// </summary>
  public static ProvisionerOptions Load(IConfiguration configuration)
  {
    var section = configuration.GetSection(SectionName);
    // Before binding: TimeSpan reads a bare number as days, so DrainDelay=150 would bind as
    // 150 days. Durations must say hh:mm:ss.
    CheckDuration(section, nameof(AutoSuspendAfter));
    CheckDuration(section, nameof(ReadyTimeout));
    CheckDuration(section, nameof(DrainDelay));

    ProvisionerOptions options = new();
    section.Bind(options);
    options.Sandboxes.Validate();
    if (!RendererSizes.TryParse(options.Size, out _))
    {
      throw new InvalidOperationException(
        $"{SectionName}:Size is '{options.Size}'; use S, M, or L."
      );
    }

    if (
      !string.Equals(
        options.PortActivation,
        nameof(SandboxPortActivation.OnDemand),
        StringComparison.OrdinalIgnoreCase
      )
      && !string.Equals(
        options.PortActivation,
        nameof(SandboxPortActivation.Manual),
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      throw new InvalidOperationException(
        $"{SectionName}:PortActivation is '{options.PortActivation}'; use OnDemand or Manual."
      );
    }

    try
    {
      options.PortOptions.Validate();
    }
    catch (ArgumentException exception)
    {
      throw new InvalidOperationException(
        $"{SectionName}:AllowedSourceCidrs: {exception.Message}",
        exception
      );
    }

    CheckRange(
      nameof(AutoSuspendAfter),
      options.AutoSuspendAfter,
      TimeSpan.Zero,
      MaxAutoSuspendAfter,
      zeroAllowed: false
    );
    CheckRange(
      nameof(ReadyTimeout),
      options.ReadyTimeout,
      TimeSpan.Zero,
      MaxReadyTimeout,
      zeroAllowed: false
    );
    CheckRange(
      nameof(DrainDelay),
      options.DrainDelay,
      TimeSpan.Zero,
      MaxDrainDelay,
      zeroAllowed: true
    );
    return options;
  }

  /// <summary>
  /// Whether <paramref name="value"/> is a duration as the provisioner takes them: <c>hh:mm:ss</c>
  /// (or <c>d.hh:mm:ss</c>), or <c>0</c>. A bare number is refused.
  /// </summary>
  public static bool TryParseDuration(string value, out TimeSpan duration)
  {
    if (value == "0")
    {
      duration = TimeSpan.Zero;
      return true;
    }

    return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration)
      && value.Contains(':', StringComparison.Ordinal);
  }

  private static void CheckDuration(IConfigurationSection section, string name)
  {
    if (section[name] is { } value && !TryParseDuration(value.Trim(), out _))
    {
      throw new InvalidOperationException(
        $"{SectionName}:{name} is '{value}'; use hh:mm:ss, such as 00:02:30."
      );
    }
  }

  private static void CheckRange(
    string name,
    TimeSpan value,
    TimeSpan minimum,
    TimeSpan maximum,
    bool zeroAllowed
  )
  {
    if (value < minimum || (!zeroAllowed && value == minimum))
    {
      throw new InvalidOperationException(
        zeroAllowed
          ? $"{SectionName}:{name} cannot be negative."
          : $"{SectionName}:{name} must be positive."
      );
    }

    if (value > maximum)
    {
      throw new InvalidOperationException(
        $"{SectionName}:{name} is {value.ToString("c", CultureInfo.InvariantCulture)}; "
          + $"the most is {maximum.ToString("c", CultureInfo.InvariantCulture)}."
      );
    }
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
