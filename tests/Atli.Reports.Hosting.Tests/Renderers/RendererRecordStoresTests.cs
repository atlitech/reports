using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Azure.Identity;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>
/// Choosing the record store and the credential from settings. Creating either touches no network.
/// </summary>
public class RendererRecordStoresTests
{
  [Test]
  [Arguments("File")]
  [Arguments("file")]
  [Arguments("FILE")]
  public async Task File_creates_a_file_store(string store)
  {
    var created = RendererRecordStores.Create(
      new RendererRecordStoreOptions { Store = store, Path = Path.GetTempPath() }
    );

    await Assert.That(created).IsTypeOf<FileRendererRecordStore>();
  }

  [Test]
  [Arguments("")]
  [Arguments("  ")]
  public async Task File_needs_a_path(string path)
  {
    var exception = await Assert
      .That(() =>
        RendererRecordStores.Create(new RendererRecordStoreOptions { Store = "File", Path = path })
      )
      .Throws<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("Path");
  }

  [Test]
  [Arguments("KeyVault", "")]
  [Arguments("keyvault", "00000000-0000-0000-0000-000000000001")]
  public async Task KeyVault_creates_a_key_vault_store(string store, string clientId)
  {
    var created = RendererRecordStores.Create(
      new RendererRecordStoreOptions
      {
        Store = store,
        VaultUri = new Uri("https://contoso.vault.azure.net/"),
        ManagedIdentityClientId = clientId,
      }
    );

    await Assert.That(created).IsTypeOf<KeyVaultRendererRecordStore>();
  }

  [Test]
  public async Task KeyVault_needs_an_absolute_vault_uri()
  {
    var missing = await Assert
      .That(() =>
        RendererRecordStores.Create(new RendererRecordStoreOptions { Store = "KeyVault" })
      )
      .Throws<InvalidOperationException>();
    await Assert
      .That(() =>
        RendererRecordStores.Create(
          new RendererRecordStoreOptions
          {
            Store = "KeyVault",
            VaultUri = new Uri("contoso", UriKind.Relative),
          }
        )
      )
      .Throws<InvalidOperationException>();

    await Assert.That(missing!.Message).Contains("VaultUri");
  }

  [Test]
  [Arguments("", "No renderer record store is configured. Set Store to File or KeyVault.")]
  [Arguments("Redis", "Unknown renderer record store 'Redis'. Use File or KeyVault.")]
  public async Task Any_other_store_is_refused(string store, string message)
  {
    await Assert
      .That(() => RendererRecordStores.Create(new RendererRecordStoreOptions { Store = store }))
      .Throws<InvalidOperationException>()
      .WithMessage(message);
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("  ")]
  public async Task Without_a_client_id_the_credential_is_the_default_chain(string? clientId)
  {
    await Assert.That(AzureCredentials.Create(clientId)).IsTypeOf<DefaultAzureCredential>();
  }

  [Test]
  public async Task With_a_client_id_the_credential_is_that_managed_identity()
  {
    await Assert
      .That(AzureCredentials.Create("00000000-0000-0000-0000-000000000001"))
      .IsTypeOf<ManagedIdentityCredential>();
  }

  [Test]
  public async Task The_sandbox_groups_address_follows_its_region_and_names()
  {
    SandboxesOptions options = new()
    {
      SubscriptionId = "sub",
      ResourceGroup = "rg",
      SandboxGroup = "group",
      Region = "eastus2",
    };

    options.Validate();

    await Assert
      .That(options.GroupUri.ToString())
      .IsEqualTo(
        "https://management.eastus2.azuredevcompute.io/subscriptions/sub/resourceGroups/rg/sandboxGroups/group/"
      );
  }
}
