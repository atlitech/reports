using System.Text.Json.Nodes;
using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Tests.Support;
using TUnit.Assertions.Enums;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>
/// Records as one owner-only JSON file per tenant, replaced atomically.
/// </summary>
public sealed class FileRendererRecordStoreTests : IDisposable
{
  private readonly string _root = Path.Combine(
    Path.GetTempPath(),
    "atli-reports-hosting-tests-" + Guid.NewGuid().ToString("N")
  );

  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  private string Directory => Path.Combine(_root, "records");

  public void Dispose()
  {
    if (System.IO.Directory.Exists(_root))
    {
      System.IO.Directory.Delete(_root, recursive: true);
    }
  }

  [Test]
  public async Task Put_writes_the_record_as_camel_case_json_named_for_its_tenant()
  {
    FileRendererRecordStore store = new(Directory);
    var record = Records.Acme();

    await store.PutAsync(record, TestToken);

    var json = JsonNode.Parse(
      await File.ReadAllTextAsync(Path.Combine(Directory, "acme.json"), TestToken)
    )!;
    await Assert.That(json["tenantId"]!.GetValue<string>()).IsEqualTo("acme");
    await Assert
      .That(json["url"]!.GetValue<string>())
      .IsEqualTo("https://acme--8080.eastus2.adcproxy.io/");
    await Assert.That(json["apiKey"]!.GetValue<string>()).IsEqualTo(record.ApiKey);
    await Assert.That(json["sandboxId"]!.GetValue<string>()).IsEqualTo(record.SandboxId);
    await Assert.That(json["diskImageId"]!.GetValue<string>()).IsEqualTo(record.DiskImageId);
    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(record);
  }

  [Test]
  public async Task Optional_fields_may_be_absent()
  {
    FileRendererRecordStore store = new(Directory);
    RendererRecord record = new()
    {
      TenantId = "acme",
      Url = new Uri("http://renderer.internal:8080"),
      ApiKey = "key",
    };

    await store.PutAsync(record, TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(record);
  }

  [Test]
  public async Task The_directory_and_the_records_are_readable_by_their_owner_only()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Unix file modes.");
      return;
    }

    FileRendererRecordStore store = new(Directory);

    await store.PutAsync(Records.Acme(), TestToken);
    await store.PutAsync(Records.Acme() with { ApiKey = "replaced" }, TestToken);

    await Assert
      .That(File.GetUnixFileMode(Directory))
      .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    await Assert
      .That(File.GetUnixFileMode(Path.Combine(Directory, "acme.json")))
      .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
  }

  [Test]
  public async Task Put_replaces_the_record_and_leaves_no_temporary_file()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme(), TestToken);
    var replacement = Records.Acme() with { ApiKey = "replaced" };

    await store.PutAsync(replacement, TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsEqualTo(replacement);
    await Assert
      .That(System.IO.Directory.GetFiles(Directory).Select(file => Path.GetFileName(file)!))
      .IsEquivalentTo(["acme.json"]);
  }

  [Test]
  public async Task A_reader_sees_the_old_record_or_the_new_one_never_a_partial_write()
  {
    FileRendererRecordStore store = new(Directory);
    var small = Records.Acme();
    // Much larger than one write, so a non-atomic replace would be seen half done.
    var large = Records.Acme() with
    {
      ApiKey = new string('k', 1 << 20),
    };
    await store.PutAsync(small, TestToken);

    var writer = Task.Run(
      async () =>
      {
        for (var i = 0; i < 40; i++)
        {
          await store.PutAsync(i % 2 == 0 ? large : small, TestToken);
        }
      },
      TestToken
    );
    var reads = 0;
    while (!writer.IsCompleted)
    {
      var read = await store.GetAsync("acme", TestToken);
      await Assert.That(read == small || read == large).IsTrue();
      reads++;
    }

    await writer;
    await Assert.That(reads).IsGreaterThan(0);
  }

  [Test]
  public async Task A_canceled_put_leaves_no_file_behind()
  {
    FileRendererRecordStore store = new(Directory);
    using CancellationTokenSource canceled = new();
    await canceled.CancelAsync();

    await Assert
      .That(async () => await store.PutAsync(Records.Acme(), canceled.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(System.IO.Directory.GetFiles(Directory)).IsEmpty();
  }

  [Test]
  public async Task Get_of_a_tenant_without_a_record_returns_null()
  {
    FileRendererRecordStore store = new(Directory);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
    await store.PutAsync(Records.Acme(), TestToken);
    await Assert.That(await store.GetAsync("globex", TestToken)).IsNull();
  }

  [Test]
  public async Task List_returns_every_record_by_tenant_and_skips_other_files()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme() with { TenantId = "zeta" }, TestToken);
    await store.PutAsync(Records.Acme(), TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "README.md"), "notes", TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, ".acme.1234.tmp"), "{", TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "Not_A_Tenant.json"), "{", TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "acme.json.bak"), "{", TestToken);

    var records = await store.ListAsync(TestToken);

    await Assert
      .That(records.Select(record => record.TenantId))
      .IsEquivalentTo(["acme", "zeta"], CollectionOrdering.Matching);
  }

  [Test]
  public async Task A_store_whose_directory_does_not_exist_is_empty()
  {
    FileRendererRecordStore store = new(Directory);

    await Assert.That((await store.ListAsync(TestToken)).Count).IsEqualTo(0);
    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
    await store.DeleteAsync("acme", TestToken);
    await Assert.That(System.IO.Directory.Exists(Directory)).IsFalse();
  }

  [Test]
  public async Task Delete_removes_the_record_and_deleting_a_missing_one_succeeds()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme(), TestToken);

    await store.DeleteAsync("acme", TestToken);
    await store.DeleteAsync("acme", TestToken);

    await Assert.That(await store.GetAsync("acme", TestToken)).IsNull();
    await Assert.That(File.Exists(Path.Combine(Directory, "acme.json"))).IsFalse();
  }

  [Test]
  public async Task A_file_holding_another_tenants_record_is_refused()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme() with { TenantId = "globex" }, TestToken);
    File.Move(Path.Combine(Directory, "globex.json"), Path.Combine(Directory, "acme.json"));

    await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  public async Task List_reports_files_that_are_not_their_tenants_record_and_returns_the_rest()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme(), TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "bogus.json"), "{}", TestToken);
    await File.WriteAllTextAsync(
      Path.Combine(Directory, "globex.json"),
      Records.Json(Records.Acme()),
      TestToken
    );

    var listing = await store.ListWithUnreadableAsync(TestToken);

    await Assert.That(listing.Records).IsEquivalentTo([Records.Acme()]);
    await Assert.That(await store.ListAsync(TestToken)).IsEquivalentTo([Records.Acme()]);
    await Assert
      .That(listing.Unreadable.Select(record => record.TenantId))
      .IsEquivalentTo(["bogus", "globex"], CollectionOrdering.Matching);
    await Assert
      .That(listing.Unreadable[1].Reason)
      .IsEqualTo(
        $"The renderer record file {Path.Combine(Directory, "globex.json")} holds the record of "
          + "another tenant."
      );
  }

  [Test]
  public async Task Listing_tenant_ids_takes_them_from_the_file_names()
  {
    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme() with { TenantId = "zeta" }, TestToken);
    await store.PutAsync(Records.Acme(), TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "damaged.json"), "{", TestToken);
    await File.WriteAllTextAsync(Path.Combine(Directory, "Not_A_Tenant.json"), "{", TestToken);

    var tenants = await store.ListTenantIdsAsync(TestToken);

    await Assert
      .That(tenants)
      .IsEquivalentTo(["acme", "damaged", "zeta"], CollectionOrdering.Matching);
    await Assert
      .That(await new FileRendererRecordStore(_root + "-none").ListTenantIdsAsync(TestToken))
      .IsEmpty();
  }

  [Test]
  public async Task A_directory_others_can_write_to_is_refused()
  {
    if (OperatingSystem.IsWindows())
    {
      Skip.Test("Unix file modes.");
      return;
    }

    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme(), TestToken);
    File.SetUnixFileMode(
      Directory,
      UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
    );

    var exception = await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<UnauthorizedAccessException>();
    await Assert.That(exception!.Message).Contains("is writable by other users");
    await Assert
      .That(async () => await store.ListAsync(TestToken))
      .Throws<UnauthorizedAccessException>();
    await Assert
      .That(async () => await store.PutAsync(Records.Acme(), TestToken))
      .Throws<UnauthorizedAccessException>();
    await Assert
      .That(async () => await store.DeleteAsync("acme", TestToken))
      .Throws<UnauthorizedAccessException>();
  }

  [Test]
  public async Task A_directory_of_another_user_is_refused()
  {
    if (OperatingSystem.IsWindows() || Environment.UserName == "root")
    {
      Skip.Test("Unix ownership, as a user other than root.");
      return;
    }

    // Root's, and not writable by anyone else.
    FileRendererRecordStore store = new("/etc");

    var exception = await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<UnauthorizedAccessException>();
    await Assert.That(exception!.Message).EndsWith("belongs to another user.");
  }

  [Test]
  public async Task A_record_file_of_another_user_is_refused()
  {
    if (OperatingSystem.IsWindows() || Environment.UserName == "root")
    {
      Skip.Test("Unix ownership, as a user other than root.");
      return;
    }

    FileRendererRecordStore store = new(Directory);
    await store.PutAsync(Records.Acme(), TestToken);
    // A file root owns, in the record's place.
    File.CreateSymbolicLink(Path.Combine(Directory, "globex.json"), "/etc/hosts");

    var exception = await Assert
      .That(async () => await store.GetAsync("globex", TestToken))
      .Throws<UnauthorizedAccessException>();
    var listing = await store.ListWithUnreadableAsync(TestToken);

    await Assert.That(exception!.Message).EndsWith("belongs to another user.");
    await Assert.That(listing.Records).IsEquivalentTo([Records.Acme()]);
    await Assert.That(listing.Unreadable.Single().TenantId).IsEqualTo("globex");
  }

  [Test]
  [Arguments("{")]
  [Arguments("""{"tenantId":"acme"}""")]
  [Arguments("""{"tenantId":"acme","url":"relative/path","apiKey":"k"}""")]
  [Arguments("""{"tenantId":"acme","url":"file:///etc/passwd","apiKey":"k"}""")]
  [Arguments("""{"tenantId":"acme","url":"https://renderer.example/","apiKey":""}""")]
  [Arguments("""{"tenantId":"acme","url":"https://renderer.example/","apiKey":" "}""")]
  [Arguments("""{"tenantId":"acme","url":"https://renderer.example/"}""")]
  [Arguments("null")]
  public async Task A_file_that_is_not_a_record_is_refused(string json)
  {
    FileRendererRecordStore store = new(Directory);
    System.IO.Directory.CreateDirectory(Directory);
    await File.WriteAllTextAsync(Path.Combine(Directory, "acme.json"), json, TestToken);

    await Assert
      .That(async () => await store.GetAsync("acme", TestToken))
      .Throws<InvalidDataException>();
  }

  [Test]
  [Arguments("")]
  [Arguments("Acme")]
  [Arguments("../acme")]
  [Arguments("acme/../../etc")]
  [Arguments("-acme")]
  public async Task Refuses_an_invalid_tenant_ID(string tenantId)
  {
    FileRendererRecordStore store = new(Directory);

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

  [Test]
  public async Task Refuses_a_record_without_an_absolute_url_or_an_api_key()
  {
    FileRendererRecordStore store = new(Directory);

    await Assert
      .That(async () =>
        await store.PutAsync(
          Records.Acme() with
          {
            Url = new Uri("relative", UriKind.Relative),
          },
          TestToken
        )
      )
      .Throws<ArgumentException>();
    await Assert
      .That(async () => await store.PutAsync(Records.Acme() with { ApiKey = " " }, TestToken))
      .Throws<ArgumentException>();
    await Assert
      .That(async () =>
        await store.PutAsync(
          Records.Acme() with
          {
            Url = new Uri("ftp://renderer.example/"),
          },
          TestToken
        )
      )
      .Throws<ArgumentException>();
    await Assert.That(System.IO.Directory.Exists(Directory)).IsFalse();
  }
}
