using System.Globalization;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Tests.Support;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>
/// The renderer's small types: records, sizes, tenant IDs, and the server's environment.
/// </summary>
public class RendererModelTests
{
  [Test]
  public async Task A_records_string_form_leaves_its_api_key_out()
  {
    var record = Records.Acme();

    var text = record.ToString();

    await Assert.That(text).DoesNotContain(record.ApiKey);
    await Assert.That(text).DoesNotContain("ApiKey");
    await Assert
      .That(text)
      .IsEqualTo(
        "RendererRecord { TenantId = acme, Url = https://acme--8080.eastus2.adcproxy.io/, "
          + "SandboxId = 98c01b65-b81b-4dca-b000-fdae0eb0939c, "
          + "DiskImageId = c3d87d13-9ce3-4fb5-b0db-db168ea50aa6, "
          + "CreatedAt = 2026-10-04T01:50:55.0000000+00:00 }"
      );
  }

  [Test]
  public async Task Records_are_equal_by_value()
  {
    await Assert.That(Records.Acme()).IsEqualTo(Records.Acme());
    await Assert.That(Records.Acme() with { ApiKey = "other" }).IsNotEqualTo(Records.Acme());
  }

  [Test]
  [Arguments("S", "S", "500m", "1024Mi", 1)]
  [Arguments("m", "M", "1000m", "2048Mi", 2)]
  [Arguments("L", "L", "2000m", "4096Mi", 4)]
  public async Task Sizes_parse_by_name_in_any_case(
    string name,
    string expected,
    string cpu,
    string memory,
    int concurrency
  )
  {
    var size = RendererSize.Parse(name);

    await Assert.That(size.Name).IsEqualTo(expected);
    await Assert.That(size.Cpu).IsEqualTo(cpu);
    await Assert.That(size.Memory).IsEqualTo(memory);
    await Assert.That(size.MaxConcurrentConversions).IsEqualTo(concurrency);
  }

  [Test]
  [Arguments("XL")]
  [Arguments("")]
  [Arguments(null)]
  public async Task An_unknown_size_is_refused(string? name)
  {
    await Assert.That(() => RendererSize.Parse(name!)).Throws<ArgumentException>();
  }

  [Test]
  [Arguments("a")]
  [Arguments("0")]
  [Arguments("acme")]
  [Arguments("acme-eu-1")]
  [Arguments("9lives")]
  [Arguments("a-")]
  public async Task Valid_tenant_IDs(string value)
  {
    await Assert.That(TenantId.IsValid(value)).IsTrue();
    await Assert.That(TenantId.Validate(value)).IsEqualTo(value);
  }

  [Test]
  public async Task A_tenant_ID_is_at_most_63_characters()
  {
    await Assert.That(TenantId.IsValid(new string('a', 63))).IsTrue();
    await Assert.That(TenantId.IsValid(new string('a', 64))).IsFalse();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("-acme")]
  [Arguments("Acme")]
  [Arguments("acme_eu")]
  [Arguments("acme.eu")]
  [Arguments("acme/eu")]
  [Arguments("../acme")]
  [Arguments(" acme")]
  [Arguments("acme\n")]
  [Arguments("äcme")]
  public async Task Invalid_tenant_IDs(string? value)
  {
    await Assert.That(TenantId.IsValid(value)).IsFalse();
    var exception = await Assert
      .That(() => TenantId.Validate(value, "tenant"))
      .Throws<ArgumentException>();
    await Assert.That(exception!.ParamName).IsEqualTo("tenant");
  }

  [Test]
  public async Task The_server_environment_admits_only_the_gateways_credential_to_convert()
  {
    var credential = RendererCredential.Generate();

    var environment = RendererServerEnvironment.Create(credential, RendererSize.Large);

    await Assert
      .That(environment)
      .IsEquivalentTo(
        new Dictionary<string, string>
        {
          ["ReportsServer__Authentication__Mode"] = "ApiKey",
          ["ReportsServer__Authentication__ApiKeys__0__Id"] = credential.KeyId,
          ["ReportsServer__Authentication__ApiKeys__0__Hash"] = credential.Verifier,
          ["ReportsServer__Authentication__ApiKeys__0__CallerId"] = "gateway",
          ["ReportsServer__Authentication__ApiKeys__0__Permissions__0"] = "reports.convert",
          ["ReportsServer__Limits__MaxConcurrentRequestsPerCaller"] = "16",
          ["ReportsServer__Limits__MaxRequestBodyBytes"] = "31457280",
          ["Kestrel__Limits__MaxRequestBodySize"] = "31457280",
          ["ReportsEngine__Concurrency__MaxConcurrentConversions"] = "4",
          ["ReportsEngine__Concurrency__MaxQueueLength"] = "12",
          ["ReportsEngine__Network__Mode"] = "Disabled",
        }
      );
  }

  [Test]
  [Arguments("S", "1", "4", "3")]
  [Arguments("M", "2", "8", "6")]
  [Arguments("L", "4", "16", "12")]
  public async Task A_renderer_queues_what_it_admits_beyond_its_conversions(
    string size,
    string conversions,
    string admitted,
    string queued
  )
  {
    var environment = RendererServerEnvironment.Create(
      RendererCredential.Generate(),
      RendererSize.Parse(size)
    );

    // The gateway's requests beyond the running conversions wait in the engine's queue instead of
    // being refused as busy.
    await Assert
      .That(environment["ReportsEngine__Concurrency__MaxConcurrentConversions"])
      .IsEqualTo(conversions);
    await Assert
      .That(environment["ReportsServer__Limits__MaxConcurrentRequestsPerCaller"])
      .IsEqualTo(admitted);
    await Assert.That(environment["ReportsEngine__Concurrency__MaxQueueLength"]).IsEqualTo(queued);
  }

  [Test]
  public async Task A_renderer_accepts_any_body_the_gateways_encoder_can_make_of_an_admitted_one()
  {
    var environment = RendererServerEnvironment.Create(
      RendererCredential.Generate(),
      RendererSize.Small
    );
    // The server's default body limit, and the most the gateway's JSON encoder grows text by.
    const long admitted = 10 * 1024 * 1024;

    await Assert
      .That(
        long.Parse(
          environment["ReportsServer__Limits__MaxRequestBodyBytes"],
          CultureInfo.InvariantCulture
        )
      )
      .IsGreaterThanOrEqualTo(3 * admitted);
    // Kestrel's own limit is 30,000,000 bytes unless set, and the lower of the two applies.
    await Assert
      .That(
        long.Parse(environment["Kestrel__Limits__MaxRequestBodySize"], CultureInfo.InvariantCulture)
      )
      .IsGreaterThanOrEqualTo(3 * admitted);
  }

  [Test]
  public async Task The_server_environment_never_holds_the_raw_credential()
  {
    var credential = RendererCredential.Generate();
    var secret = credential.Credential[(credential.KeyId.Length + 1)..];

    var environment = RendererServerEnvironment.Create(credential, RendererSize.Medium);

    await Assert
      .That(environment.Values.Any(value => value.Contains(secret, StringComparison.Ordinal)))
      .IsFalse();
  }

  [Test]
  public async Task The_server_environment_needs_a_credential_and_a_size()
  {
    await Assert
      .That(() => RendererServerEnvironment.Create(null!, RendererSize.Small))
      .Throws<ArgumentNullException>();
    await Assert
      .That(() => RendererServerEnvironment.Create(RendererCredential.Generate(), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task The_entrypoint_runs_the_server_under_tini()
  {
    await Assert
      .That(RendererServerEnvironment.Entrypoint)
      .IsEquivalentTo(["/usr/bin/tini", "--", "/app/Atli.Reports.Server"]);
  }
}
