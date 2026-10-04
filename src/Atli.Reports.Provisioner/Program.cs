using System.Runtime.InteropServices;
using Atli.Reports.Hosting;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner;

using CancellationTokenSource cancellation = new();

// The first Ctrl+C or SIGTERM (a pipeline canceling the job, a container stopping) lets the command
// delete what it created but has not recorded; the next one ends the process at once.
var canceling = 0;
bool Cancel(string signal)
{
  // Ctrl+C and SIGTERM arrive on threads of their own.
  if (Interlocked.Exchange(ref canceling, 1) != 0)
  {
    return false;
  }

  Console.Error.WriteLine(
    $"Canceling ({signal}). Signal again to stop at once, without cleaning up."
  );
  cancellation.Cancel();
  return true;
}

Console.CancelKeyPress += (_, e) => e.Cancel = Cancel("Ctrl+C");
using var terminate = PosixSignalRegistration.Create(
  PosixSignal.SIGTERM,
  context => context.Cancel = Cancel("SIGTERM")
);

using HttpClient sandboxesHttp = new();
using HttpClient readinessHttp = new();

return await ProvisionerCli.RunAsync(
  args,
  ProvisionerConfiguration.Build(AppContext.BaseDirectory, Environment.GetEnvironmentVariables()),
  options => new ProvisionerServices(
    new SandboxesClient(
      sandboxesHttp,
      AzureCredentials.Create(
        string.IsNullOrWhiteSpace(options.Sandboxes.ManagedIdentityClientId)
          ? null
          : options.Sandboxes.ManagedIdentityClientId
      ),
      options.Sandboxes
    ),
    RendererRecordStores.Create(options.Records),
    new HttpReadinessProbe(readinessHttp)
  ),
  Console.Out,
  Console.Error,
  TimeProvider.System,
  cancellation.Token
);
