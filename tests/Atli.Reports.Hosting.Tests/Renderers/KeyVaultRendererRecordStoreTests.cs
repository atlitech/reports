using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Tests.Support;
using Azure;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>
/// Records as Key Vault secrets, against an in-memory vault that answers as Key Vault does,
/// soft delete included.
/// </summary>
public class KeyVaultRendererRecordStoreTests
{
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Put_writes_the_record_as_a_json_secret_tagged_with_its_tenant()
  {
    FakeVault vault = new();
    var store = Store(vault);
    var record = Records.Acme();

    await store.PutAsync(record, TestToken);

    var secret = vault.Peek("renderer-acme")!.Value;
    await Assert.That(secret.ContentType).IsEqualTo("application/json");
    await Assert.That(secret.Tags["tenant"]).IsEqualTo("acme");
    var json = JsonNode.Parse(secret.Value)!;
    await Assert.That(json["tenantId"]!.GetValue<string>()).IsEqualTo("acme");
    await Assert
      .That(json["url"]!.GetValue<string>())
      .IsEqualTo("https://acme--8080.eastus2.adcproxy.io/");
    await Assert.That(json["apiKey"]!.GetValue<string>()).IsEqualTo(record.ApiKey);
    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(record);
  }

  [Test]
  public async Task Put_replaces_the_record_with_a_new_version()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    var replacement = Records.Acme() with { ApiKey = "reports-000000000000.new" };

    await store.PutAsync(replacement, TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(replacement);
  }

  [Test]
  public async Task Get_of_a_tenant_without_a_record_returns_null()
  {
    var store = Store(new FakeVault());

    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
  }

  [Test]
  public async Task A_disabled_secret_is_no_record()
  {
    FakeVault vault = new();
    vault.Seed("renderer-acme", Records.Json(Records.Acme()), enabled: false);
    var store = Store(vault);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
    await Assert.That((await store.ListAsync(TestToken)).Count).IsEqualTo(0);
  }

  [Test]
  public async Task A_refused_read_is_not_taken_for_a_missing_record()
  {
    FakeVault vault = new()
    {
      ReadFailure = new RequestFailedException(
        403,
        "Caller is not authorized to perform action on resource.",
        "Forbidden",
        null
      ),
    };
    var store = Store(vault);

    await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<RequestFailedException>();
  }

  [Test]
  public async Task A_secret_holding_another_tenants_record_is_refused()
  {
    FakeVault vault = new();
    vault.Seed("renderer-acme", Records.Json(Records.Acme() with { TenantId = "globex" }));
    var store = Store(vault);

    await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task A_secret_that_is_not_a_record_is_refused()
  {
    FakeVault vault = new();
    vault.Seed("renderer-acme", "not json");
    var store = Store(vault);

    await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task List_returns_the_enabled_records_and_skips_other_secrets()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme() with { TenantId = "zeta" }, TestToken);
    await store.PutAsync(Records.Acme(), TestToken);
    vault.Seed("renderer-off", Records.Json(Records.Acme() with { TenantId = "off" }), false);
    vault.Seed("database-password", "hunter2");
    vault.Seed("renderer-", "{}");
    vault.Seed("renderer--bad", "{}");

    var records = await store.ListAsync(TestToken);

    await Assert
      .That(records.Select(record => record.TenantId))
      .IsEquivalentTo(["acme", "zeta"], CollectionOrdering.Matching);
    await Assert.That(vault.Calls).DoesNotContain("Get database-password");
  }

  [Test]
  public async Task Delete_soft_deletes_the_secret()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);

    await store.DeleteAsync("acme", TestToken);

    await Assert.That(vault.Calls).Contains("Delete renderer-acme");
    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
    await Assert.That((await store.ListAsync(TestToken)).Count).IsEqualTo(0);
  }

  [Test]
  public async Task Deleting_a_missing_or_deleted_record_succeeds()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.DeleteAsync("acme", TestToken);
    await store.PutAsync(Records.Acme(), TestToken);
    await store.DeleteAsync("acme", TestToken);

    await store.DeleteAsync("acme", TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
  }

  [Test]
  public async Task Putting_a_soft_deleted_record_again_recovers_its_secret_first()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    await store.DeleteAsync("acme", TestToken);
    var again = Records.Acme() with { ApiKey = "reports-000000000000.again" };

    await store.PutAsync(again, TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(again);
    await Assert
      .That(vault.Calls.Skip(3).Take(3))
      .IsEquivalentTo(
        ["Set renderer-acme", "Recover renderer-acme", "Set renderer-acme"],
        CollectionOrdering.Matching
      );
  }

  [Test]
  public async Task Delete_disables_the_record_before_deleting_its_secret()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);

    await store.DeleteAsync("acme", TestToken);

    await Assert
      .That(vault.Calls.Skip(1))
      .IsEquivalentTo(
        ["Disable renderer-acme", "Delete renderer-acme"],
        CollectionOrdering.Matching
      );
    await Assert.That(vault.IsDisabled("renderer-acme")).IsTrue();
    await Assert.That(vault.IsDeleted("renderer-acme")).IsTrue();
  }

  [Test]
  public async Task A_disabled_or_unreadable_record_can_still_be_deleted()
  {
    FakeVault vault = new();
    vault.Seed("renderer-acme", Records.Json(Records.Acme()), enabled: false);
    vault.Seed("renderer-globex", "not a record");
    var store = Store(vault);

    await store.DeleteAsync("acme", TestToken);
    await store.DeleteAsync("globex", TestToken);

    await Assert.That(vault.IsDeleted("renderer-acme")).IsTrue();
    await Assert.That(vault.IsDeleted("renderer-globex")).IsTrue();
  }

  [Test]
  public async Task A_recovered_secret_is_no_record_until_the_new_one_is_written()
  {
    FakeVault vault = new();
    var store = Store(vault);
    var old = Records.Acme();
    await store.PutAsync(old, TestToken);
    await store.DeleteAsync("acme", TestToken);
    RendererRecord? seenAfterRecovery = old;
    var listedAfterRecovery = -1;
    // Reads between the recovery and the write: what a gateway would see meanwhile.
    vault.WriteFailure = write =>
    {
      if (write == 3)
      {
        seenAfterRecovery = store.GetAsync("acme", TestToken).GetAwaiter().GetResult();
        listedAfterRecovery = store.ListAsync(TestToken).GetAwaiter().GetResult().Count;
      }

      return null;
    };

    var fresh = old with { SandboxId = "11111111-1111-1111-1111-111111111111" };
    await store.PutAsync(fresh, TestToken);

    await Assert.That(seenAfterRecovery).IsNull();
    await Assert.That(listedAfterRecovery).IsEqualTo(0);
    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(fresh);
  }

  [Test]
  public async Task A_put_whose_write_fails_after_recovering_the_secret_deletes_it_again()
  {
    FakeVault vault = new();
    var store = Store(vault);
    var old = Records.Acme();
    await store.PutAsync(old, TestToken);
    await store.DeleteAsync("acme", TestToken);
    // Writes: 1 the first record, 2 the new one refused as deleted, 3 the new one after recovery.
    vault.WriteFailure = write =>
      write == 3 ? new RequestFailedException(429, "Too many requests.") : null;

    var exception = await Assert
      .That(async () =>
        await store.PutAsync(
          old with
          {
            SandboxId = "11111111-1111-1111-1111-111111111111",
          },
          TestToken
        )
      )
      .Throws<RequestFailedException>();

    await Assert.That(exception!.Status).IsEqualTo(429);
    await Assert
      .That(vault.Calls.TakeLast(3))
      .IsEquivalentTo(
        ["Set renderer-acme", "Disable renderer-acme", "Delete renderer-acme"],
        CollectionOrdering.Matching
      );
    await Assert.That(vault.IsDeleted("renderer-acme")).IsTrue();
    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
  }

  [Test]
  public async Task A_failed_put_deletes_a_recovered_secret_that_an_older_delete_left_enabled()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    // Deleted without being disabled first, as before deletes disabled records.
    await vault.StartDeleteSecretAsync("renderer-acme", TestToken);
    vault.WriteFailure = write =>
      write == 3 ? new RequestFailedException(503, "Unavailable.") : null;

    await Assert
      .That(async () => await store.PutAsync(Records.Acme(), TestToken))
      .Throws<RequestFailedException>();

    await Assert.That(vault.IsDisabled("renderer-acme")).IsTrue();
    await Assert.That(vault.IsDeleted("renderer-acme")).IsTrue();
  }

  [Test]
  public async Task List_reports_secrets_that_do_not_hold_their_tenants_record_and_returns_the_rest()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    vault.Seed("renderer-signing-key", "not a record");
    vault.Seed("renderer-globex", Records.Json(Records.Acme()));

    var listing = await store.ListWithUnreadableAsync(TestToken);
    var records = await store.ListAsync(TestToken);

    await Assert.That(listing.Records).IsEquivalentTo([Records.Acme()]);
    await Assert.That(records).IsEquivalentTo([Records.Acme()]);
    await Assert
      .That(listing.Unreadable)
      .IsEquivalentTo(
        [
          new UnreadableRendererRecord(
            "globex",
            "The Key Vault secret renderer-globex holds the record of another tenant."
          ),
          new UnreadableRendererRecord(
            "signing-key",
            "The Key Vault secret renderer-signing-key does not hold a renderer record."
          ),
        ],
        CollectionOrdering.Matching
      );
  }

  [Test]
  public async Task A_refused_read_still_fails_the_list()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    vault.ReadFailure = new RequestFailedException(403, "Forbidden.", "Forbidden", null);

    await Assert
      .That(async () => await store.ListAsync(TestToken))
      .Throws<RequestFailedException>();
  }

  [Test]
  public async Task Listing_tenant_ids_reads_no_secret()
  {
    FakeVault vault = new();
    var store = Store(vault);
    await store.PutAsync(Records.Acme() with { TenantId = "zeta" }, TestToken);
    await store.PutAsync(Records.Acme(), TestToken);
    vault.Seed("renderer-damaged", "not a record");
    vault.Seed("renderer-off", Records.Json(Records.Acme() with { TenantId = "off" }), false);
    vault.Seed("renderer--bad", "{}");
    vault.Seed("database-password", "hunter2");

    var tenants = await store.ListTenantIdsAsync(TestToken);

    await Assert
      .That(tenants)
      .IsEquivalentTo(["acme", "damaged", "zeta"], CollectionOrdering.Matching);
    await Assert
      .That(vault.Calls.Any(call => call.StartsWith("Get", StringComparison.Ordinal)))
      .IsFalse();
  }

  [Test]
  public async Task List_reads_at_most_eight_secrets_at_once()
  {
    FakeVault vault = new();
    var store = Store(vault);
    for (var tenant = 0; tenant < 20; tenant++)
    {
      await store.PutAsync(Records.Acme() with { TenantId = $"t{tenant}" }, TestToken);
    }

    Lock counting = new();
    var reading = 0;
    var mostReading = 0;
    TaskCompletionSource eightReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    vault.BeforeRead = async cancellationToken =>
    {
      int now;
      lock (counting)
      {
        now = ++reading;
        mostReading = Math.Max(mostReading, now);
      }

      if (now == KeyVaultRendererRecordStore.MaxListParallelism)
      {
        eightReading.TrySetResult();
      }

      await release.Task.WaitAsync(cancellationToken);
      lock (counting)
      {
        reading--;
      }
    };

    var list = store.ListAsync(TestToken);
    await eightReading.Task.WaitAsync(TestToken);
    // Eight reads wait; a ninth would have started by now if it could.
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestToken);
    release.SetResult();
    var records = await list;

    await Assert.That(records.Count).IsEqualTo(20);
    await Assert.That(mostReading).IsEqualTo(8);
  }

  [Test]
  public async Task Putting_a_record_still_being_deleted_waits_for_the_deletion_then_recovers()
  {
    FakeVault vault = new() { DeletionCalls = 4 };
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    await store.DeleteAsync("acme", TestToken);
    var again = Records.Acme() with { ApiKey = "reports-000000000000.again" };

    await store.PutAsync(again, TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(again);
    await Assert.That(vault.Calls).Contains("Recover renderer-acme");
  }

  [Test]
  public async Task A_put_gives_up_on_a_secret_that_never_finishes_deleting()
  {
    FakeVault vault = new() { NeverFinishesDeleting = true };
    var store = Store(vault);
    await store.PutAsync(Records.Acme(), TestToken);
    await store.DeleteAsync("acme", TestToken);

    var exception = await Assert
      .That(async () => await store.PutAsync(Records.Acme(), TestToken))
      .Throws<RequestFailedException>();

    await Assert.That(exception!.Status).IsEqualTo(409);
    await Assert
      .That(vault.Calls.Count(call => call == "Set renderer-acme"))
      .IsEqualTo(KeyVaultRendererRecordStore.MaxPutAttempts + 1);
  }

  [Test]
  public async Task A_put_waits_between_attempts_on_a_secret_being_deleted()
  {
    FakeVault vault = new() { DeletionCalls = 100 };
    TestClock clock = new();
    KeyVaultRendererRecordStore store = new(vault, TimeSpan.FromSeconds(2), clock);
    await store.PutAsync(Records.Acme(), TestToken);
    await store.DeleteAsync("acme", TestToken);

    var put = store.PutAsync(Records.Acme(), TestToken);
    var delay = await clock.NextTimerAsync(TestToken);
    vault.FinishDeletions();
    clock.Advance(delay - TestClock.Tick);
    await Assert.That(put.IsCompleted).IsFalse();
    clock.Advance(TestClock.Tick);
    await put;

    await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(Records.Acme());
  }

  [Test]
  [Arguments("")]
  [Arguments("Acme")]
  [Arguments("../acme")]
  [Arguments("-acme")]
  public async Task Refuses_an_invalid_tenant_ID(string tenantId)
  {
    var store = Store(new FakeVault());

    await Assert
      .That(async () => await store.GetAsync(tenantId, TestToken))
      .Throws<ArgumentException>();
    await Assert
      .That(async () => await store.DeleteAsync(tenantId, TestToken))
      .Throws<ArgumentException>();
    await Assert
      .That(async () =>
        await store.PutAsync(Records.Acme() with { TenantId = tenantId }, TestToken)
      )
      .Throws<ArgumentException>();
  }

  private static KeyVaultRendererRecordStore Store(FakeVault vault) =>
    new(vault, TimeSpan.Zero, TimeProvider.System);
}
