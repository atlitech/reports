using System.Collections;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace Atli.Reports.Provisioner.Tests;

/// <summary>
/// The command line: arguments, configuration and its precedence, and exit codes (0 success, 1
/// failure, 2 usage or configuration error).
/// </summary>
public class CommandLineTests
{
  private static readonly Dictionary<string, string?> Configured = new()
  {
    ["Provisioner:Sandboxes:SubscriptionId"] = "00000000-0000-0000-0000-000000000000",
    ["Provisioner:Sandboxes:ResourceGroup"] = "renderers",
    ["Provisioner:Sandboxes:SandboxGroup"] = "renderers",
    ["Provisioner:Sandboxes:Region"] = "eastus2",
    ["Provisioner:Records:Store"] = "File",
    ["Provisioner:Records:Path"] = "/var/lib/atli-reports/renderers",
    ["Provisioner:DiskImageId"] = "disk-1",
  };

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  [Arguments("", "No command given.")]
  [Arguments("deploy", "Unknown command 'deploy'.")]
  [Arguments("create", "--tenant is required.")]
  [Arguments("create --tenant", "--tenant needs a value.")]
  [Arguments("create --tenant Contoso", "--tenant 'Contoso' is not a tenant ID")]
  [Arguments("create --tenant a --size XL", "--size is 'XL'; use S, M, or L.")]
  [Arguments("create --tenant a --disk-image=", "--disk-image is empty.")]
  [Arguments("create --tenant a --tenant b", "--tenant is given more than once.")]
  [Arguments("create --tenant a --drain 0", "Unknown option '--drain'.")]
  [Arguments("create a", "Unexpected argument 'a'.")]
  [Arguments("rollout --max-parallel 0", "--max-parallel is '0'")]
  [Arguments("rollout --max-parallel two", "--max-parallel is 'two'")]
  [Arguments("rollout --drain 90", "--drain is '90'; use hh:mm:ss, such as 00:02:30.")]
  [Arguments("rollout --tenant A", "--tenant 'A' is not a tenant ID")]
  [Arguments("delete --tenant a --drain soon", "--drain is 'soon'")]
  [Arguments("delete --tenant a --drain -00:01:00", "--drain is '-00:01:00'")]
  [Arguments("delete --tenant a --drain 01:00:01", "--drain is '01:00:01'; the most is 01:00:00.")]
  [Arguments("rollout --drain 1.00:00:00", "--drain is '1.00:00:00'; the most is 01:00:00.")]
  [Arguments("list --tenant a", "Unknown option '--tenant'.")]
  [Arguments("prune --drain 150", "--drain is '150'; use hh:mm:ss")]
  [Arguments("prune --tenant A", "--tenant 'A' is not a tenant ID")]
  [Arguments("prune --size S", "Unknown option '--size'.")]
  [Arguments("disable", "--tenant is required.")]
  [Arguments("enable --tenant a --drain 0", "Unknown option '--drain'.")]
  [Arguments("rollout --stopped delete", "--stopped is 'delete'; use replace or retire.")]
  [Arguments("rollout --stopped", "--stopped needs a value.")]
  [Arguments("rollout --stopped retire --stopped replace", "--stopped is given more than once.")]
  [Arguments("retire --tenant a", "Unknown option '--tenant'.")]
  [Arguments("retire now", "Unexpected argument 'now'.")]
  public async Task A_wrong_command_line_is_a_usage_error_that_changes_nothing(
    string commandLine,
    string message
  )
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");

    var (exitCode, output, error) = await RunAsync(provisioning, Configured, commandLine);

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert.That(error).StartsWith("error: " + message);
    await Assert.That(error).Contains("Usage: atli-reports-provisioner");
    await Assert.That(output).IsEmpty();
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  [Arguments("--help", "Commands:")]
  [Arguments("help", "Commands:")]
  [Arguments("create --help", "Usage: atli-reports-provisioner create --tenant <id>")]
  [Arguments("rollout -h", "Usage: atli-reports-provisioner rollout [--disk-image <id>]")]
  [Arguments("delete --tenant a --help", "Usage: atli-reports-provisioner delete --tenant <id>")]
  [Arguments("list --help", "Usage: atli-reports-provisioner list")]
  [Arguments("prune --help", "Usage: atli-reports-provisioner prune [--tenant <id>]")]
  [Arguments("disable --help", "Usage: atli-reports-provisioner disable --tenant <id>")]
  [Arguments("enable -h", "Usage: atli-reports-provisioner enable --tenant <id>")]
  [Arguments("rollout --help", "[--drain <hh:mm:ss>] [--stopped replace|retire]")]
  [Arguments("retire --help", "Usage: atli-reports-provisioner retire")]
  public async Task Help_needs_no_configuration(string commandLine, string expected)
  {
    using Provisioning provisioning = new();

    var (exitCode, output, error) = await RunAsync(provisioning, [], commandLine);

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(output).Contains(expected);
    await Assert.That(error).IsEmpty();
  }

  [Test]
  [Arguments("Provisioner:Sandboxes:Region", "", "Sandboxes needs SubscriptionId")]
  [Arguments("Provisioner:Size", "XL", "Provisioner:Size is 'XL'; use S, M, or L.")]
  [Arguments("Provisioner:ReadyTimeout", "soon", "Provisioner:ReadyTimeout")]
  [Arguments("Provisioner:ReadyTimeout", "00:00:00", "Provisioner:ReadyTimeout must be positive.")]
  [Arguments("Provisioner:AutoSuspendAfter", "00:00:00", "AutoSuspendAfter must be positive.")]
  [Arguments("Provisioner:DrainDelay", "-00:00:01", "Provisioner:DrainDelay cannot be negative.")]
  // A bare number would bind as days: DrainDelay=150 is 150 days, not seconds.
  [Arguments("Provisioner:DrainDelay", "150", "Provisioner:DrainDelay is '150'; use hh:mm:ss")]
  [Arguments("Provisioner:ReadyTimeout", "180", "Provisioner:ReadyTimeout is '180'; use hh:mm:ss")]
  [Arguments(
    "Provisioner:AutoSuspendAfter",
    "300",
    "Provisioner:AutoSuspendAfter is '300'; use hh:mm:ss"
  )]
  [Arguments(
    "Provisioner:DrainDelay",
    "01:00:01",
    "Provisioner:DrainDelay is 01:00:01; the most is 01:00:00."
  )]
  [Arguments(
    "Provisioner:ReadyTimeout",
    "00:15:01",
    "Provisioner:ReadyTimeout is 00:15:01; the most is 00:15:00."
  )]
  [Arguments(
    "Provisioner:AutoSuspendAfter",
    "1.00:00:01",
    "Provisioner:AutoSuspendAfter is 1.00:00:01; the most is 1.00:00:00."
  )]
  [Arguments(
    "Provisioner:PortActivation",
    "Always",
    "Provisioner:PortActivation is 'Always'; use OnDemand or Manual."
  )]
  [Arguments(
    "Provisioner:AllowedSourceCidrs:0",
    "203.0.113.7",
    "Provisioner:AllowedSourceCidrs: '203.0.113.7' is not a source range in CIDR notation"
  )]
  [Arguments("Provisioner:Sandboxes:Region", "x.attacker.example#", "Sandboxes Region")]
  [Arguments("Provisioner:Sandboxes:SubscriptionId", "sub-1", "Sandboxes SubscriptionId")]
  [Arguments("Provisioner:Sandboxes:ResourceGroup", "rg/../x", "Sandboxes ResourceGroup")]
  public async Task Wrong_configuration_is_a_configuration_error(
    string key,
    string value,
    string message
  )
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");

    var (exitCode, _, error) = await RunAsync(
      provisioning,
      new(Configured) { [key] = value },
      "rollout --disk-image disk-2"
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert.That(error).StartsWith("error: configuration: ");
    await Assert.That(error).Contains(message);
    // Found before anything changed.
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  public async Task Durations_within_their_bounds_are_taken()
  {
    using Provisioning provisioning = new();
    ProvisionerOptions? options = null;
    using StringWriter error = new() { NewLine = "\n" };

    var exitCode = await ProvisionerCli.RunAsync(
      ["list"],
      new ConfigurationBuilder()
        .AddInMemoryCollection(
          new Dictionary<string, string?>(Configured)
          {
            ["Provisioner:DrainDelay"] = "01:00:00",
            ["Provisioner:ReadyTimeout"] = "00:15:00",
            ["Provisioner:AutoSuspendAfter"] = "1.00:00:00",
            ["Provisioner:PortActivation"] = "manual",
            ["Provisioner:AllowedSourceCidrs:0"] = "203.0.113.7/32",
            ["Provisioner:AllowedSourceCidrs:1"] = "2001:db8::/48",
            ["Provisioner:NetworkConnection"] = "renderers",
          }
        )
        .Build(),
      configured =>
      {
        options = configured;
        return provisioning.Services;
      },
      provisioning.Output,
      error,
      provisioning.Clock,
      TestToken
    );

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(options!.DrainDelay).IsEqualTo(TimeSpan.FromHours(1));
    await Assert.That(options.ReadyTimeout).IsEqualTo(TimeSpan.FromMinutes(15));
    await Assert.That(options.AutoSuspendAfter).IsEqualTo(TimeSpan.FromDays(1));
    await Assert.That(options.Activation).IsEqualTo(SandboxPortActivation.Manual);
    await Assert
      .That(options.PortOptions.AllowedSourceCidrs)
      .IsEquivalentTo(["203.0.113.7/32", "2001:db8::/48"]);
    await Assert.That(options.NetworkConnectionName).IsEqualTo("renderers");
  }

  [Test]
  public async Task Disable_stops_the_tenants_renderer_and_enable_undoes_it()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");

    var (disabled, _, _) = await RunAsync(provisioning, Configured, "disable --tenant a");
    await Assert.That(disabled).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Disabled).IsEquivalentTo(["old-a"]);

    var (enabled, _, _) = await RunAsync(provisioning, Configured, "enable --tenant a");
    await Assert.That(enabled).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Disabled).IsEmpty();
  }

  [Test]
  public async Task Prune_deletes_leftovers_after_the_drain_and_exits_with_1_when_one_stays()
  {
    using Provisioning provisioning = new();
    provisioning.Sandboxes.Add("left-a", RendererLabels.For("a", RendererSize.Medium));
    provisioning.Sandboxes.Add("left-b", RendererLabels.For("b", RendererSize.Medium));
    provisioning.Sandboxes.FailDelete.Add("left-b");
    var drain = TimeSpan.FromSeconds(10);

    var prune = RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:DrainDelay"] = "00:02:30" },
      "prune --drain 00:00:10"
    );
    await provisioning.Clock.WaitForTimerAsync(drain);
    provisioning.Clock.Advance(drain);
    var (exitCode, output, _) = await prune;

    await Assert.That(exitCode).IsEqualTo(1);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["left-b"]);
    await Assert.That(output).Contains("Deleted 1, failed 1.");
  }

  [Test]
  [Arguments("rollout --stopped retire")]
  [Arguments("retire")]
  public async Task Retiring_needs_tenant_prefixes(string commandLine)
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);

    var (exitCode, output, error) = await RunAsync(provisioning, Configured, commandLine);

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert
      .That(error)
      .StartsWith("error: configuration: Provisioner:Service:TenantPrefixes is empty");
    await Assert.That(output).IsEmpty();
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  [Arguments(
    "Provisioner:Service:TenantPrefixes:0:MaxCreatesPerMinute",
    "0",
    "Provisioner:Service:TenantPrefixes:0:MaxCreatesPerMinute must be between 1 and 10000."
  )]
  [Arguments(
    "Provisioner:Service:TenantPrefixes:0:MaxCreatesPerMinute",
    "10001",
    "Provisioner:Service:TenantPrefixes:0:MaxCreatesPerMinute must be between 1 and 10000."
  )]
  [Arguments(
    "Provisioner:Service:MaxCreatesPerMinute",
    "0",
    "Provisioner:Service:MaxCreatesPerMinute must be between 1 and 10000."
  )]
  [Arguments(
    "Provisioner:Service:TenantPrefixes:1:Prefix",
    "readiness-",
    "Provisioner:Service:TenantPrefixes:1:Prefix 'readiness-' owns the tenant ID "
      + "'readiness-probe', which the gateway's readiness reserves."
  )]
  public async Task Wrong_service_configuration_is_a_configuration_error(
    string key,
    string value,
    string message
  )
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);

    var (exitCode, output, error) = await RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:Service:TenantPrefixes:0:Prefix"] = "app-", [key] = value },
      "retire"
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert.That(error).IsEqualTo($"error: configuration: {message}\n");
    await Assert.That(output).IsEmpty();
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  public async Task Serve_needs_the_source_ranges_its_renderers_admit()
  {
    using Provisioning provisioning = new();
    var gateway = RendererCredential.Generate();

    var (exitCode, output, error) = await RunAsync(
      provisioning,
      new(Configured)
      {
        ["Provisioner:Service:TenantPrefixes:0:Prefix"] = "app-",
        ["Provisioner:Service:ApiKeys:0:Id"] = gateway.KeyId,
        ["Provisioner:Service:ApiKeys:0:Hash"] = gateway.Verifier,
      },
      "serve"
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert
      .That(error)
      .IsEqualTo(
        "error: configuration: Provisioner:AllowedSourceCidrs is empty: serve creates renderers on "
          + "demand, whose ports must admit only the gateway's outbound addresses and the "
          + "service's own, such as [\"203.0.113.7/32\"].\n"
      );
    await Assert.That(output).IsEmpty();
    await Assert.That(provisioning.Journal.Entries).IsEmpty();
  }

  [Test]
  public async Task A_prefixs_creates_per_minute_default_to_20_under_60_in_all()
  {
    ProvisioningServiceOptions service = new();
    service.TenantPrefixes.Add(new ManagedTenantPrefix { Prefix = "app-" });

    service.Validate(requireApiKeys: false);

    await Assert.That(service.TenantPrefixes[0].MaxCreatesPerMinute).IsEqualTo(20);
    await Assert.That(service.MaxCreatesPerMinute).IsEqualTo(60);
  }

  [Test]
  [Arguments("rollout --disk-image disk-2 --drain 0")]
  [Arguments("rollout --disk-image disk-2 --drain 0 --stopped replace")]
  public async Task A_rollout_replaces_stopped_renderers_unless_told_to_retire_them(
    string commandLine
  )
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);

    // No tenant prefixes needed.
    var (exitCode, output, _) = await RunAsync(provisioning, Configured, commandLine);

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(provisioning.Records["app-a"]!.DiskImageId).IsEqualTo("disk-2");
    await Assert.That(output).Contains("Replaced 1, already on the image 0, failed 0.");
  }

  [Test]
  public async Task A_rollout_with_stopped_retire_retires_managed_stopped_renderers()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("app-a", "disk-1", state: SandboxStates.Stopped);
    provisioning.AddRenderer("app-b", "disk-1");

    var (exitCode, output, _) = await RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:Service:TenantPrefixes:0:Prefix"] = "app-" },
      "rollout --disk-image disk-2 --drain 0 --stopped retire"
    );

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(provisioning.Records["app-a"]).IsNull();
    await Assert.That(provisioning.Records["app-b"]!.DiskImageId).IsEqualTo("disk-2");
    await Assert.That(output).Contains("Replaced 1, retired 1, already on the image 0, failed 0.");
  }

  [Test]
  public async Task Retire_retires_renderers_idle_for_the_configured_time_and_exits_with_1_when_one_fails()
  {
    using Provisioning provisioning = new();
    foreach (var (tenant, stoppedFor) in new[] { ("app-a", 48), ("app-b", 48), ("app-c", 12) })
    {
      provisioning.AddRenderer(tenant, "disk-1");
      provisioning.Sandboxes.Suspend($"old-{tenant}", TimeSpan.FromHours(stoppedFor));
    }

    provisioning.Sandboxes.FailDelete.Add("old-app-b");

    var (exitCode, output, _) = await RunAsync(
      provisioning,
      new(Configured)
      {
        ["Provisioner:Service:TenantPrefixes:0:Prefix"] = "app-",
        ["Provisioner:Service:RetireAfterIdle"] = "1.00:00:00",
        // Not needed to retire.
        ["Provisioner:DiskImageId"] = null,
      },
      "retire"
    );

    await Assert.That(exitCode).IsEqualTo(1);
    await Assert.That(provisioning.Records["app-a"]).IsNull();
    await Assert.That(provisioning.Records["app-c"]).IsNotNull();
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-app-b", "old-app-c"]);
    await Assert.That(output).Contains("Retired 1, failed 1.\n  app-b: Deleting old-app-b failed.");
  }

  [Test]
  [Arguments("create --tenant a")]
  [Arguments("rollout")]
  public async Task Creating_renderers_needs_a_disk_image(string commandLine)
  {
    using Provisioning provisioning = new();

    var (exitCode, _, error) = await RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:DiskImageId"] = null },
      commandLine
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert
      .That(error)
      .StartsWith("error: No disk image: pass --disk-image or set Provisioner:DiskImageId.");
  }

  [Test]
  public async Task A_store_the_services_refuse_is_a_configuration_error()
  {
    using Provisioning provisioning = new();
    using StringWriter error = new() { NewLine = "\n" };

    var exitCode = await ProvisionerCli.RunAsync(
      ["list"],
      new ConfigurationBuilder().AddInMemoryCollection(Configured).Build(),
      _ => throw new InvalidOperationException("Unknown renderer record store 'Disk'."),
      provisioning.Output,
      error,
      provisioning.Clock,
      TestToken
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert
      .That(error.ToString())
      .IsEqualTo("error: configuration: Unknown renderer record store 'Disk'.\n");
  }

  [Test]
  public async Task Flags_override_environment_variables_which_override_appsettings_json()
  {
    var directory = Directory.CreateTempSubdirectory("atli-reports-provisioner-");
    try
    {
      await File.WriteAllTextAsync(
        Path.Combine(directory.FullName, "appsettings.json"),
        """
        {
          "Provisioner": {
            "Sandboxes": {
              "SubscriptionId": "00000000-0000-0000-0000-000000000000",
              "ResourceGroup": "renderers",
              "SandboxGroup": "renderers",
              "Region": "westus3"
            },
            "Records": { "Store": "KeyVault", "VaultUri": "https://contoso.vault.azure.net/" },
            "DiskImageId": "disk-json",
            "Size": "S",
            "ReadyTimeout": "00:01:00"
          }
        }
        """,
        TestToken
      );
      Hashtable environment = new()
      {
        ["Provisioner__Sandboxes__Region"] = "eastus2",
        ["Provisioner__DiskImageId"] = "disk-environment",
        ["PROVISIONER__RECORDS__MANAGEDIDENTITYCLIENTID"] = "11111111-1111-1111-1111-111111111111",
        // Not the provisioner's: never part of its configuration.
        ["Sandboxes__Region"] = "northeurope",
      };
      var configuration = ProvisionerConfiguration.Build(directory.FullName, environment);
      using Provisioning provisioning = new();
      ProvisionerOptions? options = null;
      using StringWriter error = new() { NewLine = "\n" };

      var exitCode = await ProvisionerCli.RunAsync(
        ["create", "--tenant", "a", "--disk-image", "disk-flag"],
        configuration,
        configured =>
        {
          options = configured;
          return provisioning.Services;
        },
        provisioning.Output,
        error,
        provisioning.Clock,
        TestToken
      );

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(options!.Sandboxes.Region).IsEqualTo("eastus2");
      await Assert.That(options.Sandboxes.SandboxGroup).IsEqualTo("renderers");
      await Assert.That(options.Records.Store).IsEqualTo("KeyVault");
      await Assert
        .That(options.Records.VaultUri)
        .IsEqualTo(new Uri("https://contoso.vault.azure.net/"));
      await Assert
        .That(options.Records.ManagedIdentityClientId)
        .IsEqualTo("11111111-1111-1111-1111-111111111111");
      await Assert.That(options.DiskImageId).IsEqualTo("disk-flag");
      await Assert.That(options.ReadyTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
      await Assert.That(options.AutoSuspendAfter).IsEqualTo(TimeSpan.FromMinutes(5));
      await Assert.That(options.DrainDelay).IsEqualTo(TimeSpan.FromSeconds(150));
      var spec = provisioning.Sandboxes.Created[0];
      await Assert.That(spec.DiskImageId).IsEqualTo("disk-flag");
      await Assert.That(spec.Labels["size"]).IsEqualTo("S");
    }
    finally
    {
      directory.Delete(recursive: true);
    }
  }

  [Test]
  public async Task Without_appsettings_json_the_environment_alone_configures_it()
  {
    var directory = Directory.CreateTempSubdirectory("atli-reports-provisioner-");
    try
    {
      Hashtable environment = new();
      foreach (var (key, value) in Configured)
      {
        environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
      }

      using Provisioning provisioning = new();

      var (exitCode, output, _) = await RunConfiguredAsync(
        provisioning,
        ProvisionerConfiguration.Build(directory.FullName, environment),
        "list"
      );

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(output).IsEqualTo("No renderers.\n");
    }
    finally
    {
      directory.Delete(recursive: true);
    }
  }

  [Test]
  public async Task A_created_renderer_exits_with_0_and_shows_no_credential()
  {
    using Provisioning provisioning = new();

    var (exitCode, output, error) = await RunAsync(
      provisioning,
      Configured,
      "create --tenant a --size L"
    );

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(error).IsEmpty();
    await Assert.That(output).Contains("Sandbox:     sandbox-1");
    await Assert.That(provisioning.Sandboxes.Created[0].DiskImageId).IsEqualTo("disk-1");
    await Assert.That(provisioning.Sandboxes.Created[0].Cpu).IsEqualTo("2000m");
    await Assert.That(output).DoesNotContain(provisioning.Records["a"]!.ApiKey);
  }

  [Test]
  public async Task A_refused_create_exits_with_1()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");

    var (exitCode, _, error) = await RunAsync(provisioning, Configured, "create --tenant a");

    await Assert.That(exitCode).IsEqualTo(1);
    await Assert
      .That(error)
      .IsEqualTo(
        "error: Tenant a already has a renderer (sandbox old-a). Replace it with rollout, or delete it first.\n"
      );
  }

  [Test]
  public async Task A_rollout_with_a_failed_tenant_exits_with_1_after_the_others()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    provisioning.Sandboxes.FailAddPort = sandbox =>
      sandbox.Labels["tenant"] == "a"
        ? new Hosting.Sandboxes.SandboxesException("Too many ports.")
        : null;

    var (exitCode, output, _) = await RunAsync(
      provisioning,
      Configured,
      "rollout --disk-image disk-2 --drain 0"
    );

    await Assert.That(exitCode).IsEqualTo(1);
    await Assert.That(provisioning.Records["b"]!.DiskImageId).IsEqualTo("disk-2");
    await Assert.That(output).Contains("Replaced 1, already on the image 0, failed 1.");
  }

  [Test]
  public async Task A_rollout_drains_for_the_configured_delay_unless_the_flag_says_otherwise()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    var configured = TimeSpan.FromSeconds(10);

    var rollout = RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:DrainDelay"] = "00:00:10" },
      "rollout --disk-image disk-2 --tenant a --max-parallel 1"
    );
    await provisioning.Clock.WaitForTimerAsync(configured);
    provisioning.Clock.Advance(configured);
    var (exitCode, _, _) = await rollout;
    await Assert.That(exitCode).IsEqualTo(0);

    (exitCode, _, _) = await RunAsync(
      provisioning,
      new(Configured) { ["Provisioner:DrainDelay"] = "00:00:10" },
      "rollout --disk-image=disk-2 --drain=00:00:00"
    );

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["sandbox-1", "sandbox-2"]);
  }

  [Test]
  public async Task Delete_removes_the_sandbox_at_once_unless_told_to_drain()
  {
    using Provisioning provisioning = new();
    provisioning.AddRenderer("a", "disk-1");
    provisioning.AddRenderer("b", "disk-1");
    var drain = TimeSpan.FromSeconds(30);

    var (exitCode, _, _) = await RunAsync(provisioning, Configured, "delete --tenant a");
    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b"]);

    var delete = RunAsync(provisioning, Configured, "delete --tenant b --drain 00:00:30");
    await provisioning.Clock.WaitForTimerAsync(drain);
    await Assert.That(provisioning.Sandboxes.Ids).IsEquivalentTo(["old-b"]);
    provisioning.Clock.Advance(drain);
    (exitCode, _, _) = await delete;

    await Assert.That(exitCode).IsEqualTo(0);
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  [Test]
  public async Task A_canceled_command_exits_with_1()
  {
    using Provisioning provisioning = new();
    provisioning.Readiness.AnswerInTurn(ReadinessAnswer.NotReady("HTTP 503"));
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
    using StringWriter error = new() { NewLine = "\n" };

    var create = ProvisionerCli.RunAsync(
      ["create", "--tenant", "a"],
      new ConfigurationBuilder().AddInMemoryCollection(Configured).Build(),
      _ => provisioning.Services,
      provisioning.Output,
      error,
      provisioning.Clock,
      cancellation.Token
    );
    await provisioning.Clock.WaitForTimerAsync(RendererProvisioner.ReadyPollInterval);
    await cancellation.CancelAsync();

    await Assert.That(await create).IsEqualTo(1);
    await Assert.That(error.ToString()).IsEqualTo("Canceled.\n");
    await Assert.That(provisioning.Sandboxes.Ids).IsEmpty();
  }

  private static Task<(int ExitCode, string Output, string Error)> RunAsync(
    Provisioning provisioning,
    Dictionary<string, string?> settings,
    string commandLine
  ) =>
    RunConfiguredAsync(
      provisioning,
      new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
      commandLine
    );

  private static async Task<(int ExitCode, string Output, string Error)> RunConfiguredAsync(
    Provisioning provisioning,
    IConfiguration configuration,
    string commandLine
  )
  {
    using StringWriter error = new() { NewLine = "\n" };
    var exitCode = await ProvisionerCli.RunAsync(
      commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
      configuration,
      _ => provisioning.Services,
      provisioning.Output,
      error,
      provisioning.Clock,
      TestToken
    );
    return (exitCode, provisioning.Output.ToString(), error.ToString());
  }
}
