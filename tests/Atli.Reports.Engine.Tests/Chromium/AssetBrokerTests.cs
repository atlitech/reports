using System.Net;
using System.Text;
using Atli.Reports.Engine.Chromium.Network;

namespace Atli.Reports.Engine.Tests.Chromium;

public class AssetBrokerTests
{
  [Test]
  public async Task Allowed_asset_is_loaded_without_forwarding_credentials()
  {
    using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("body { color: green; }", Encoding.UTF8, "text/css"),
    });
    using AssetBroker broker = new(AssetNetworkPolicyTests.Options(), handler);

    var asset = await broker.FetchAsync(
      new Uri("https://assets.example.test/style.css"),
      "GET",
      CancellationToken.None
    );

    await Assert.That(asset).IsNotNull();
    await Assert.That(Encoding.UTF8.GetString(asset!.Body)).IsEqualTo("body { color: green; }");
    await Assert.That(handler.Calls).IsEqualTo(1);
    await Assert.That(handler.HadCredentials).IsFalse();
  }

  [Test]
  [Arguments("http://169.254.169.254/metadata")]
  [Arguments("https://elsewhere.example.test/image")]
  [Arguments("file:///etc/passwd")]
  public async Task A_redirect_is_reauthorized_before_another_request(string destination)
  {
    using StubHandler handler = new(_ =>
    {
      HttpResponseMessage response = new(HttpStatusCode.Redirect);
      response.Headers.Location = new Uri(destination);
      return response;
    });
    using AssetBroker broker = new(AssetNetworkPolicyTests.Options(), handler);

    var asset = await broker.FetchAsync(
      new Uri("https://assets.example.test/image"),
      "GET",
      CancellationToken.None
    );

    await Assert.That(asset).IsNull();
    await Assert.That(handler.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task Request_count_and_decoded_response_bytes_are_bounded()
  {
    var options = AssetNetworkPolicyTests.Options();
    options.MaxRequests = 2;
    options.MaxResponseBytes = 4;
    options.MaxTotalResponseBytes = 5;
    using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("1234"),
    });
    using AssetBroker broker = new(options, handler);
    var uri = new Uri("https://assets.example.test/image");

    await Assert.That(await broker.FetchAsync(uri, "GET", CancellationToken.None)).IsNotNull();
    await Assert.That(await broker.FetchAsync(uri, "GET", CancellationToken.None)).IsNull();
    await Assert.That(await broker.FetchAsync(uri, "GET", CancellationToken.None)).IsNull();
    await Assert.That(handler.Calls).IsEqualTo(2);
  }

  [Test]
  public async Task Oversized_responses_and_state_changing_methods_are_denied()
  {
    var options = AssetNetworkPolicyTests.Options();
    options.MaxResponseBytes = 3;
    using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("1234"),
    });
    using AssetBroker broker = new(options, handler);
    var uri = new Uri("https://assets.example.test/image");

    await Assert.That(await broker.FetchAsync(uri, "POST", CancellationToken.None)).IsNull();
    await Assert.That(await broker.FetchAsync(uri, "GET", CancellationToken.None)).IsNull();
    await Assert.That(handler.Calls).IsEqualTo(1);
  }

  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
  {
    public int Calls { get; private set; }
    public bool HadCredentials { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken
    )
    {
      Calls++;
      HadCredentials |=
        request.Headers.Authorization is not null
        || request.Headers.Contains("Cookie")
        || request.Headers.Contains("Proxy-Authorization");
      return Task.FromResult(respond(request));
    }
  }
}
