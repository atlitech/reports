using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>Which prefixes are valid, which tenants they own, and which prefixes overlap.</summary>
public class TenantPrefixTests
{
  [Test]
  [Arguments("a-")]
  [Arguments("myapp-")]
  [Arguments("my-app-")]
  [Arguments("0-")]
  [Arguments("abcdefghijklmnopqrstuvwxyz-")]
  public async Task Valid_prefixes(string prefix)
  {
    await Assert.That(TenantPrefix.IsValid(prefix)).IsTrue();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("-")]
  [Arguments("myapp")]
  [Arguments("-myapp-")]
  [Arguments("MyApp-")]
  [Arguments("my_app-")]
  [Arguments("myapp-\n")]
  [Arguments("abcdefghijklmnopqrstuvwxyz0-")]
  public async Task Invalid_prefixes(string? prefix)
  {
    await Assert.That(TenantPrefix.IsValid(prefix)).IsFalse();
  }

  [Test]
  public async Task The_longest_prefix_leaves_room_for_a_guid()
  {
    var prefix = new string('a', TenantPrefix.MaxLength - 1) + "-";
    var tenant = prefix + Guid.NewGuid().ToString("D");

    await Assert.That(TenantPrefix.IsValid(prefix)).IsTrue();
    await Assert.That(tenant.Length).IsEqualTo(63);
    await Assert.That(TenantPrefix.Owns(prefix, tenant)).IsTrue();
  }

  [Test]
  [Arguments("myapp-", "myapp-1", true)]
  [Arguments("myapp-", "myapp-3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
  [Arguments("myapp-", "myapp-", false)]
  [Arguments("myapp-", "myapp", false)]
  [Arguments("myapp-", "myappx-1", false)]
  [Arguments("myapp-", "other-1", false)]
  [Arguments("myapp-", "myapp-UPPER", false)]
  [Arguments("myapp-", "myapp-1\n", false)]
  [Arguments("myapp-", null, false)]
  public async Task A_prefix_owns_valid_tenant_ids_that_continue_it(
    string prefix,
    string? tenantId,
    bool owned
  )
  {
    await Assert.That(TenantPrefix.Owns(prefix, tenantId)).IsEqualTo(owned);
  }

  [Test]
  [Arguments("myapp-", "myapp-", true)]
  [Arguments("myapp-", "myapp-eu-", true)]
  [Arguments("myapp-eu-", "myapp-", true)]
  [Arguments("myapp-", "myappx-", false)]
  [Arguments("a-", "b-", false)]
  public async Task Prefixes_overlap_when_one_starts_with_the_other(
    string first,
    string second,
    bool overlap
  )
  {
    await Assert.That(TenantPrefix.Overlap(first, second)).IsEqualTo(overlap);
  }
}
