using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Microsoft.Extensions.Configuration;

namespace Atli.Reports.Provisioner;

/// <summary>What the commands work through; <c>Program</c> supplies the real ones.</summary>
/// <param name="Sandboxes">The renderer sandbox group's data plane.</param>
/// <param name="Records">The records the gateway routes by.</param>
/// <param name="Readiness">Asks new renderers whether they are ready.</param>
internal sealed record ProvisionerServices(
  ISandboxesClient Sandboxes,
  IRendererRecordStore Records,
  IReadinessProbe Readiness
);

/// <summary>
/// Runs one command line: parses it, loads the configuration with the flags on top, and runs the
/// command. Exits with 0 on success, 1 on failure, and 2 for a usage or configuration error, which is
/// found before anything is changed.
/// </summary>
internal static class ProvisionerCli
{
  public static async Task<int> RunAsync(
    IReadOnlyList<string> arguments,
    IConfiguration configuration,
    Func<ProvisionerOptions, ProvisionerServices> createServices,
    TextWriter output,
    TextWriter error,
    TimeProvider time,
    CancellationToken cancellationToken
  )
  {
    ProvisionerCommand command;
    ProvisionerOptions options;
    ProvisionerServices services;
    try
    {
      command = CommandLine.Parse(arguments);
      if (command is HelpCommand help)
      {
        await output.WriteLineAsync(help.Text);
        return 0;
      }

      options = ProvisionerOptions.Load(
        new ConfigurationBuilder()
          .AddConfiguration(configuration)
          .AddInMemoryCollection(command.Settings)
          .Build()
      );
      if (
        command is CreateCommand or RolloutCommand
        && string.IsNullOrWhiteSpace(options.DiskImageId)
      )
      {
        throw new UsageException(
          "No disk image: pass --disk-image or set Provisioner:DiskImageId.",
          command is CreateCommand ? CommandLine.CreateUsage : CommandLine.RolloutUsage
        );
      }

      services = createServices(options);
    }
    catch (UsageException exception)
    {
      await error.WriteLineAsync($"error: {exception.Message}");
      if (exception.Usage is not null)
      {
        await error.WriteLineAsync();
        await error.WriteLineAsync(exception.Usage);
      }

      return 2;
    }
    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
    {
      await error.WriteLineAsync($"error: configuration: {exception.Message}");
      return 2;
    }
    catch (Exception exception)
    {
      await error.WriteLineAsync($"error: {exception.Message}");
      return 1;
    }

    RendererProvisioner provisioner = new(
      services.Sandboxes,
      services.Records,
      services.Readiness,
      time,
      output,
      options
    );
    try
    {
      switch (command)
      {
        case CreateCommand create:
          await provisioner.CreateAsync(
            create.TenantId,
            options.RendererSize,
            options.DiskImageId,
            cancellationToken
          );
          return 0;

        case RolloutCommand rollout:
          var result = await provisioner.RolloutAsync(
            options.DiskImageId,
            rollout.TenantId,
            rollout.MaxParallel,
            options.DrainDelay,
            cancellationToken
          );
          return result.Failures.Count == 0 ? 0 : 1;

        case DeleteCommand delete:
          await provisioner.DeleteAsync(delete.TenantId, delete.Drain, cancellationToken);
          return 0;

        case PruneCommand prune:
          var pruned = await provisioner.PruneAsync(
            prune.TenantId,
            options.DrainDelay,
            cancellationToken
          );
          return pruned.Failures.Count == 0 ? 0 : 1;

        default:
          await provisioner.ListAsync(cancellationToken);
          return 0;
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await error.WriteLineAsync("Canceled.");
      return 1;
    }
    catch (Exception exception)
    {
      // The message only: exceptions here come from the data plane, the record store, or the
      // provisioner itself, none of which put credentials in messages.
      await error.WriteLineAsync($"error: {exception.Message}");
      return 1;
    }
  }
}
