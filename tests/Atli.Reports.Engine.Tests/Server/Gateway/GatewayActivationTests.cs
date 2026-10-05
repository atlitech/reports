using System.Net;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Hosting.Sandboxes;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Engine.Tests.Server.Gateway;

/// <summary>The port proxy activates a renderer within the original conversion request.</summary>
public class GatewayActivationTests
{
  [Test]
  public async Task On_demand_activation_waits_for_the_pdf_without_a_data_plane_client_or_resend()
  {
    var cancellationToken = TestContext.Current!.Execution.CancellationToken;
    TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var under = await GatewayUnderTest.StartAsync(async context =>
    {
      arrived.TrySetResult();
      await activated.Task.WaitAsync(context.RequestAborted);
      await FakeRenderer.WritePdfAsync(context, "%PDF-1.7 activated");
    });

    var conversion = under.PostAsync();
    try
    {
      await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(conversion.IsCompleted).IsFalse();
      await Assert.That(under.Gateway.Services.GetService<ISandboxesClient>()).IsNull();
    }
    finally
    {
      activated.TrySetResult();
    }

    using var response = await conversion;
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert
      .That(await response.Content.ReadAsStringAsync(cancellationToken))
      .IsEqualTo("%PDF-1.7 activated");
    await Assert.That(under.Renderer.Requests).HasSingleItem();
  }
}
