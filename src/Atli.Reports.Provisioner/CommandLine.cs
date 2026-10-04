using System.Globalization;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Provisioner;

/// <summary>A parsed command line.</summary>
internal abstract record ProvisionerCommand
{
  /// <summary>
  /// Configuration keys the command's flags set, over every other source: <c>--size</c>,
  /// <c>--disk-image</c>, and a rollout's or prune's <c>--drain</c>.
  /// </summary>
  public IReadOnlyDictionary<string, string?> Settings { get; init; } =
    new Dictionary<string, string?>();
}

internal sealed record CreateCommand(string TenantId) : ProvisionerCommand;

internal sealed record RolloutCommand(string? TenantId, int MaxParallel) : ProvisionerCommand;

/// <param name="Drain">How long to wait between deleting the record and the sandbox; none by default.</param>
internal sealed record DeleteCommand(string TenantId, TimeSpan Drain) : ProvisionerCommand;

internal sealed record ListCommand : ProvisionerCommand;

/// <param name="TenantId">The only tenant whose leftovers to delete, or <see langword="null"/> for all.</param>
internal sealed record PruneCommand(string? TenantId) : ProvisionerCommand;

/// <param name="TenantId">The tenant whose renderer to stop and keep stopped.</param>
internal sealed record DisableCommand(string TenantId) : ProvisionerCommand;

/// <param name="TenantId">The tenant whose disabled renderer may start again.</param>
internal sealed record EnableCommand(string TenantId) : ProvisionerCommand;

/// <summary><c>serve</c>: run the provisioning service until stopped.</summary>
internal sealed record ServeCommand : ProvisionerCommand;

/// <summary><c>retire</c>: retire managed tenants' renderers that have been stopped too long, once.</summary>
internal sealed record RetireCommand : ProvisionerCommand;

/// <summary><c>--help</c>: write <paramref name="Text"/> and succeed.</summary>
internal sealed record HelpCommand(string Text) : ProvisionerCommand;

/// <summary>The command line is wrong; the CLI writes the message and the usage, and exits with 2.</summary>
internal sealed class UsageException : Exception
{
  public UsageException() { }

  public UsageException(string message)
    : base(message) { }

  public UsageException(string message, Exception innerException)
    : base(message, innerException) { }

  public UsageException(string message, string usage)
    : base(message) => Usage = usage;

  /// <summary>The usage of the command concerned, or of the CLI.</summary>
  public string? Usage { get; }
}

/// <summary>
/// Parses <c>atli-reports-provisioner &lt;command&gt; [options]</c>. Options are <c>--name value</c> or
/// <c>--name=value</c>, each at most once; <c>-h</c> or <c>--help</c> anywhere asks for the command's
/// usage.
/// </summary>
internal static class CommandLine
{
  public const string DefaultMaxParallel = "4";

  public const string Overview = """
    Usage: atli-reports-provisioner <command> [options]

    Creates, rolls out, and deletes per-customer renderers on Azure Container Apps Sandboxes, and
    keeps the records the gateway routes by.

    Commands:
      create    Create a renderer for a tenant that has none.
      rollout   Replace every renderer that runs another disk image.
      delete    Delete a tenant's renderer and its record.
      list      List renderers with the state of their sandboxes.
      prune     Delete renderer sandboxes that no record points to.
      disable   Stop a tenant's renderer and keep it from starting: the kill switch.
      enable    Let a disabled renderer start again.
      serve     Run the provisioning service, which creates renderers on demand.
      retire    Retire managed tenants' renderers that have been stopped too long.

    Run 'atli-reports-provisioner <command> --help' for a command's options. Settings come from
    appsettings.json next to the binary, then Provisioner__* environment variables, then flags.
    Run one command at a time per sandbox group. Exit codes: 0 success, 1 failure, 2 usage or
    configuration error.
    """;

  public const string CreateUsage = """
    Usage: atli-reports-provisioner create --tenant <id> [--size S|M|L] [--disk-image <id>]

    Creates a renderer for a tenant that has none: a sandbox from the disk image with a credential of
    its own, port 8080 exposed, /health/ready answering 200, and then the record the gateway routes
    by. A tenant that already has a record is refused; rollout replaces renderers.

    Options:
      --tenant <id>       The tenant: lowercase letters, digits, and hyphens.
      --size S|M|L        The renderer's size; default Provisioner:Size, else M.
      --disk-image <id>   The disk image; default Provisioner:DiskImageId.
    """;

  public const string RolloutUsage = """
    Usage: atli-reports-provisioner rollout [--disk-image <id>] [--tenant <id>] [--max-parallel <n>]
                                            [--drain <hh:mm:ss>]

    Replaces every renderer, or the tenant's, that runs another disk image or whose sandbox is gone,
    suspended ones included: a new sandbox with a new credential and the old one's size, its record
    swapped in once it is ready, and the old sandbox deleted after the drain. Then deletes, as prune
    does, renderer sandboxes an earlier command left behind. Renderers already on the image are
    skipped, so running it again after a partial failure finishes the job.

    Options:
      --disk-image <id>      The disk image to move to; default Provisioner:DiskImageId.
      --tenant <id>          Replace only this tenant's renderer.
      --max-parallel <n>     Replacements created at once; default 4.
      --drain <hh:mm:ss>     How long an old renderer stays after its record moves, for the gateway's
                             cached records and requests in flight; default Provisioner:DrainDelay,
                             else 00:02:30.
    """;

  public const string DeleteUsage = """
    Usage: atli-reports-provisioner delete --tenant <id> [--drain <hh:mm:ss>]

    Deletes the tenant's record, even one that cannot be read, so the gateway stops routing to it,
    then every sandbox labeled for the tenant. A sandbox the record names that is labeled for another
    tenant is not deleted. Deleting a tenant that has neither succeeds.

    Options:
      --tenant <id>        The tenant.
      --drain <hh:mm:ss>   Wait this long between the record and the sandbox, so requests in flight
                           finish; by default the sandbox goes at once.
    """;

  public const string ListUsage = """
    Usage: atli-reports-provisioner list

    Lists each renderer record with its sandbox's current state, then any record that cannot be
    read, then any renderer sandbox no record points to. Credentials are never shown.
    """;

  public const string PruneUsage = """
    Usage: atli-reports-provisioner prune [--tenant <id>] [--drain <hh:mm:ss>]

    Deletes renderer sandboxes that no record points to, such as the old sandboxes of a rollout
    that was canceled during its drain, or the sandbox of a create that was killed. A sandbox newer
    than the drain and the ready timeout together is kept, as it may still be being created, and so
    is every sandbox of a tenant whose record cannot be read. The leftovers are deleted after the
    drain, in case the gateway still sends them work, if no record points to them by then.

    Options:
      --tenant <id>          Only this tenant's leftovers.
      --drain <hh:mm:ss>     How long to wait before deleting; default Provisioner:DrainDelay,
                             else 00:02:30.
    """;

  public const string DisableUsage = """
    Usage: atli-reports-provisioner disable --tenant <id>

    Disables every sandbox labeled for the tenant: the platform stops it and refuses to start it
    again, whether a request reaches its on-demand port or something resumes it, until enable. The
    kill switch for a compromised renderer: its record and disk stay for investigation, and the
    tenant's conversions fail meanwhile. Follow with delete, then create, to replace the renderer
    and its credential.

    Options:
      --tenant <id>   The tenant.
    """;

  public const string EnableUsage = """
    Usage: atli-reports-provisioner enable --tenant <id>

    Lets the tenant's disabled sandboxes start again. They stay stopped until a request or a resume
    starts them.

    Options:
      --tenant <id>   The tenant.
    """;

  public const string ServeUsage = """
    Usage: atli-reports-provisioner serve

    Runs the provisioning service until stopped: an HTTP API on internal ingress that the gateway
    calls to create a renderer for a tenant under one of Provisioner:Service:TenantPrefixes on its
    first conversion, and to delete one when the application deletes the tenant. It authenticates
    the gateway with Provisioner:Service:ApiKeys, keeps each prefix within its MaxTenants and every
    create within MaxCreatesPerMinute, and retires renderers stopped for longer than
    RetireAfterIdle. Listens where ASPNETCORE_URLS says.
    """;

  public const string RetireUsage = """
    Usage: atli-reports-provisioner retire

    Deletes, with their records, the renderers of tenants under Provisioner:Service:TenantPrefixes
    that have been stopped for longer than Provisioner:Service:RetireAfterIdle, as serve does every
    RetireCheckInterval. Each comes back from the current disk image on its tenant's next
    conversion. Disabled renderers are kept.
    """;

  public static ProvisionerCommand Parse(IReadOnlyList<string> arguments)
  {
    ArgumentNullException.ThrowIfNull(arguments);
    if (arguments.Count == 0)
    {
      throw new UsageException("No command given.", Overview);
    }

    var name = arguments[0];
    if (name is "-h" or "--help" or "help")
    {
      return new HelpCommand(Overview);
    }

    var usage = name switch
    {
      "create" => CreateUsage,
      "rollout" => RolloutUsage,
      "delete" => DeleteUsage,
      "list" => ListUsage,
      "prune" => PruneUsage,
      "disable" => DisableUsage,
      "enable" => EnableUsage,
      "serve" => ServeUsage,
      "retire" => RetireUsage,
      _ => throw new UsageException($"Unknown command '{name}'.", Overview),
    };
    var options = ParseOptions(arguments.Skip(1), usage);
    if (options is null)
    {
      return new HelpCommand(usage);
    }

    return name switch
    {
      "create" => ParseCreate(options, usage),
      "rollout" => ParseRollout(options, usage),
      "delete" => ParseDelete(options, usage),
      "prune" => ParsePrune(options, usage),
      "disable" => ParseTenantOnly(options, usage, tenant => new DisableCommand(tenant)),
      "enable" => ParseTenantOnly(options, usage, tenant => new EnableCommand(tenant)),
      "serve" => ParseNoOptions(options, usage, new ServeCommand()),
      "retire" => ParseNoOptions(options, usage, new RetireCommand()),
      _ => ParseList(options, usage),
    };
  }

  private static CreateCommand ParseCreate(Options options, string usage)
  {
    options.Allow(usage, "--tenant", "--size", "--disk-image");
    Dictionary<string, string?> settings = [];
    if (options.Get("--size") is { } size)
    {
      settings[$"{ProvisionerOptions.SectionName}:Size"] = ParseSize(size, usage).Name;
    }

    AddDiskImage(options, settings, usage);
    return new CreateCommand(RequireTenant(options, usage)) { Settings = settings };
  }

  private static RolloutCommand ParseRollout(Options options, string usage)
  {
    options.Allow(usage, "--disk-image", "--tenant", "--max-parallel", "--drain");
    Dictionary<string, string?> settings = [];
    AddDiskImage(options, settings, usage);
    AddDrain(options, settings, usage);

    var maxParallelText = options.Get("--max-parallel") ?? DefaultMaxParallel;
    if (
      !int.TryParse(
        maxParallelText,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var maxParallel
      )
      || maxParallel < 1
    )
    {
      throw new UsageException(
        $"--max-parallel is '{maxParallelText}'; use a whole number of at least 1.",
        usage
      );
    }

    var tenant = options.Get("--tenant") is { } value ? ValidateTenant(value, usage) : null;
    return new RolloutCommand(tenant, maxParallel) { Settings = settings };
  }

  private static DeleteCommand ParseDelete(Options options, string usage)
  {
    options.Allow(usage, "--tenant", "--drain");
    var drain = options.Get("--drain") is { } value
      ? ParseDrain("--drain", value, usage)
      : TimeSpan.Zero;
    return new DeleteCommand(RequireTenant(options, usage), drain);
  }

  private static PruneCommand ParsePrune(Options options, string usage)
  {
    options.Allow(usage, "--tenant", "--drain");
    Dictionary<string, string?> settings = [];
    AddDrain(options, settings, usage);
    var tenant = options.Get("--tenant") is { } value ? ValidateTenant(value, usage) : null;
    return new PruneCommand(tenant) { Settings = settings };
  }

  /// <summary>A command that takes <c>--tenant</c> alone, and requires it.</summary>
  private static ProvisionerCommand ParseTenantOnly(
    Options options,
    string usage,
    Func<string, ProvisionerCommand> create
  )
  {
    options.Allow(usage, "--tenant");
    return create(RequireTenant(options, usage));
  }

  /// <summary><c>--drain</c>, over <c>Provisioner:DrainDelay</c>.</summary>
  private static void AddDrain(Options options, Dictionary<string, string?> settings, string usage)
  {
    if (options.Get("--drain") is { } drain)
    {
      settings[$"{ProvisionerOptions.SectionName}:DrainDelay"] = ParseDrain("--drain", drain, usage)
        .ToString("c", CultureInfo.InvariantCulture);
    }
  }

  private static ListCommand ParseList(Options options, string usage)
  {
    options.Allow(usage);
    return new ListCommand();
  }

  private static ProvisionerCommand ParseNoOptions(
    Options options,
    string usage,
    ProvisionerCommand command
  )
  {
    options.Allow(usage);
    return command;
  }

  /// <summary>
  /// Collects <c>--name value</c> and <c>--name=value</c> pairs, or returns <see langword="null"/>
  /// when help was asked for.
  /// </summary>
  private static Options? ParseOptions(IEnumerable<string> arguments, string usage)
  {
    Dictionary<string, string> values = new(StringComparer.Ordinal);
    using var enumerator = arguments.GetEnumerator();
    while (enumerator.MoveNext())
    {
      var argument = enumerator.Current;
      if (argument is "-h" or "--help")
      {
        return null;
      }

      if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
      {
        throw new UsageException($"Unexpected argument '{argument}'.", usage);
      }

      string name;
      string value;
      var equals = argument.IndexOf('=', StringComparison.Ordinal);
      if (equals >= 0)
      {
        name = argument[..equals];
        value = argument[(equals + 1)..];
      }
      else if (enumerator.MoveNext())
      {
        name = argument;
        value = enumerator.Current;
      }
      else
      {
        throw new UsageException($"{argument} needs a value.", usage);
      }

      if (!values.TryAdd(name, value))
      {
        throw new UsageException($"{name} is given more than once.", usage);
      }
    }

    return new Options(values);
  }

  private static void AddDiskImage(
    Options options,
    Dictionary<string, string?> settings,
    string usage
  )
  {
    if (options.Get("--disk-image") is { } diskImage)
    {
      if (string.IsNullOrWhiteSpace(diskImage))
      {
        throw new UsageException("--disk-image is empty.", usage);
      }

      settings[$"{ProvisionerOptions.SectionName}:DiskImageId"] = diskImage;
    }
  }

  private static string RequireTenant(Options options, string usage) =>
    ValidateTenant(
      options.Get("--tenant") ?? throw new UsageException("--tenant is required.", usage),
      usage
    );

  private static string ValidateTenant(string value, string usage) =>
    TenantId.IsValid(value)
      ? value
      : throw new UsageException(
        $"--tenant '{value}' is not a tenant ID: 1 to 63 lowercase letters, digits, and hyphens, "
          + "starting with a letter or digit.",
        usage
      );

  private static RendererSize ParseSize(string value, string usage) =>
    RendererSizes.TryParse(value, out var size)
      ? size
      : throw new UsageException($"--size is '{value}'; use S, M, or L.", usage);

  /// <summary>
  /// Parses a drain: <c>hh:mm:ss</c>, or <c>0</c>, of at most an hour. A bare number is refused:
  /// <see cref="TimeSpan"/> would read <c>90</c> as 90 days.
  /// </summary>
  private static TimeSpan ParseDrain(string name, string value, string usage)
  {
    if (!ProvisionerOptions.TryParseDuration(value, out var duration) || duration < TimeSpan.Zero)
    {
      throw new UsageException($"{name} is '{value}'; use hh:mm:ss, such as 00:02:30.", usage);
    }

    if (duration > ProvisionerOptions.MaxDrainDelay)
    {
      throw new UsageException(
        $"{name} is '{value}'; the most is "
          + $"{ProvisionerOptions.MaxDrainDelay.ToString("c", CultureInfo.InvariantCulture)}.",
        usage
      );
    }

    return duration;
  }

  /// <summary>The options of one command line.</summary>
  private sealed class Options(Dictionary<string, string> values)
  {
    public string? Get(string name) => values.GetValueOrDefault(name);

    /// <summary>Refuses any option but <paramref name="allowed"/>.</summary>
    public void Allow(string usage, params string[] allowed)
    {
      foreach (var name in values.Keys)
      {
        if (!allowed.Contains(name, StringComparer.Ordinal))
        {
          throw new UsageException($"Unknown option '{name}'.", usage);
        }
      }
    }
  }
}
