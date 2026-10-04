using System.Collections;
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
  [Arguments("list --tenant a", "Unknown option '--tenant'.")]
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
  public async Task Wrong_configuration_is_a_configuration_error(
    string key,
    string value,
    string message
  )
  {
    using Provisioning provisioning = new();

    var (exitCode, _, error) = await RunAsync(
      provisioning,
      new(Configured) { [key] = value },
      "list"
    );

    await Assert.That(exitCode).IsEqualTo(2);
    await Assert.That(error).StartsWith("error: configuration: ");
    await Assert.That(error).Contains(message);
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
