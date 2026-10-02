using System.Net;
using Atli.Reports.Engine.Chromium.Network;

namespace Atli.Reports.Engine.Tests.Chromium;

public class AssetNetworkPolicyTests
{
  [Test]
  [Arguments("127.0.0.1")]
  [Arguments("10.1.2.3")]
  [Arguments("172.16.0.1")]
  [Arguments("192.168.1.1")]
  [Arguments("169.254.169.254")]
  [Arguments("168.63.129.16")]
  [Arguments("100.64.0.1")]
  [Arguments("0.0.0.0")]
  [Arguments("224.0.0.1")]
  [Arguments("::1")]
  [Arguments("::")]
  [Arguments("::ffff:127.0.0.1")]
  [Arguments("::ffff:169.254.169.254")]
  [Arguments("fd00::1")]
  [Arguments("fe80::1")]
  [Arguments("2002:7f00:0001::")]
  [Arguments("2001:db8::1")]
  [Arguments("64:ff9b::7f00:1")]
  public async Task Nonpublic_and_platform_addresses_are_denied(string address)
  {
    await Assert.That(AssetNetworkPolicy.IsPublicAddress(IPAddress.Parse(address))).IsFalse();
  }

  [Test]
  [Arguments("8.8.8.8")]
  [Arguments("1.1.1.1")]
  [Arguments("2606:4700:4700::1111")]
  [Arguments("2001:4860:4860::8888")]
  public async Task Global_unicast_addresses_are_eligible(string address)
  {
    await Assert.That(AssetNetworkPolicy.IsPublicAddress(IPAddress.Parse(address))).IsTrue();
  }

  [Test]
  [Arguments("https://assets.example.test/path")]
  [Arguments("https://assets.example.test?query")]
  [Arguments("https://user:secret@assets.example.test")]
  [Arguments("https://*.example.test")]
  [Arguments("file:///etc/passwd")]
  public async Task Ambiguous_or_overbroad_origin_configuration_is_rejected(string origin)
  {
    await Assert.That(AssetNetworkPolicy.IsValidOrigin(origin)).IsFalse();
  }

  [Test]
  [Arguments("http://assets.example.test/image.png")]
  [Arguments("https://assets.example.test:444/image.png")]
  [Arguments("https://assets.example.test.attacker.test/image.png")]
  [Arguments("https://attacker.test@assets.example.test/image.png")]
  [Arguments("https://sub.assets.example.test/image.png")]
  [Arguments("wss://assets.example.test/socket")]
  public async Task Allowlists_match_the_exact_scheme_host_and_port(string url)
  {
    var options = Options();
    await Assert.That(new AssetNetworkPolicy(options).Allows(new Uri(url))).IsFalse();
  }

  [Test]
  public async Task Mixed_public_and_private_DNS_answers_are_rejected()
  {
    await Assert
      .That(async () =>
        await AssetBroker.ResolvePublicAddressesAsync(
          "assets.example.test",
          (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Loopback }),
          CancellationToken.None
        )
      )
      .Throws<HttpRequestException>();
  }

  [Test]
  [Arguments("127.1")]
  [Arguments("2130706433")]
  [Arguments("0x7f000001")]
  [Arguments("::ffff:127.0.0.1")]
  public async Task Alternative_loopback_spellings_do_not_trigger_DNS_or_bypass_checks(string host)
  {
    var lookups = 0;
    await Assert
      .That(async () =>
        await AssetBroker.ResolvePublicAddressesAsync(
          host,
          (_, _) =>
          {
            lookups++;
            return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") });
          },
          CancellationToken.None
        )
      )
      .Throws<HttpRequestException>();
    await Assert.That(lookups).IsEqualTo(0);
  }

  internal static ReportsEngineNetworkOptions Options()
  {
    ReportsEngineNetworkOptions options = new() { Mode = ReportsEngineNetworkMode.AllowList };
    options.AllowedOrigins.Add("https://assets.example.test");
    return options;
  }
}
