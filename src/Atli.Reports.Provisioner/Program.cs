using Atli.Reports.Hosting;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Provisioner;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
  // The first Ctrl+C lets the command delete what it created but has not recorded; the next one
  // ends the process at once.
  if (!cancellation.IsCancellationRequested)
  {
    e.Cancel = true;
    Console.Error.WriteLine("Canceling. Press Ctrl+C again to stop without cleaning up.");
    cancellation.Cancel();
  }
};

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
