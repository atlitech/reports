using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;
using Atli.Reports.Hosting.Tests.Support;
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
  public async Task KeyVault_refuses_http_before_creating_the_store()
  {
    await Assert
      .That(() =>
        RendererRecordStores.Create(
          new RendererRecordStoreOptions
          {
            Store = "keyvault",
            VaultUri = new Uri("http://contoso.vault.azure.net/"),
          }
        )
      )
      .Throws<InvalidOperationException>();
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
      SubscriptionId = "00000000-0000-0000-0000-000000000001",
      ResourceGroup = "rg",
      SandboxGroup = "group",
      Region = "eastus2",
    };

    options.Validate();

    await Assert
      .That(options.GroupUri.ToString())
      .IsEqualTo(
        "https://management.eastus2.azuredevcompute.io/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg/sandboxGroups/group/"
      );
  }

  [Test]
  public async Task Resource_names_with_the_characters_azure_allows_are_escaped_into_the_address()
  {
    SandboxesOptions options = new()
    {
      SubscriptionId = "00000000-0000-0000-0000-000000000001",
      ResourceGroup = "reports_(prod).eu-1",
      SandboxGroup = "renderers",
      Region = "westeurope",
    };

    await Assert
      .That(options.GroupUri.AbsoluteUri)
      .IsEqualTo(
        "https://management.westeurope.azuredevcompute.io/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/reports_%28prod%29.eu-1/sandboxGroups/renderers/"
      );
  }

  [Test]
  // The region is part of the host name: anything but a region name could move the bearer token.
  [Arguments("Region", "x.attacker.example#", "Region")]
  [Arguments("Region", "attacker.example/", "Region")]
  [Arguments("Region", "EastUS2", "Region")]
  [Arguments("Region", "east us", "Region")]
  [Arguments("SubscriptionId", "sub-1", "SubscriptionId")]
  [Arguments("SubscriptionId", "{00000000-0000-0000-0000-000000000001}", "SubscriptionId")]
  [Arguments("SubscriptionId", "00000000-0000-0000-0000-000000000001/../x", "SubscriptionId")]
  [Arguments("ResourceGroup", "rg/../other", "ResourceGroup")]
  [Arguments("ResourceGroup", "rg?x=1", "ResourceGroup")]
  [Arguments("ResourceGroup", "rg#x", "ResourceGroup")]
  [Arguments("ResourceGroup", "rg%2F", "ResourceGroup")]
  [Arguments("ResourceGroup", "rg.", "ResourceGroup")]
  [Arguments("SandboxGroup", "group/sandboxes", "SandboxGroup")]
  [Arguments("SandboxGroup", "group\n", "SandboxGroup")]
  public async Task Settings_that_could_change_the_address_are_refused(
    string setting,
    string value,
    string named
  )
  {
    SandboxesOptions options = new()
    {
      SubscriptionId = "00000000-0000-0000-0000-000000000001",
      ResourceGroup = "rg",
      SandboxGroup = "group",
      Region = "eastus2",
    };
    switch (setting)
    {
      case "Region":
        options.Region = value;
        break;
      case "SubscriptionId":
        options.SubscriptionId = value;
        break;
      case "ResourceGroup":
        options.ResourceGroup = value;
        break;
      default:
        options.SandboxGroup = value;
        break;
    }

    var exception = await Assert.That(options.Validate).Throws<InvalidOperationException>();
    await Assert.That(exception!.Message).StartsWith($"Sandboxes {named} '");
    await Assert.That(() => options.GroupUri).Throws<InvalidOperationException>();
    using HttpClient http = new();
    await Assert
      .That(() => new SandboxesClient(http, new FakeCredential(TimeProvider.System), options))
      .Throws<InvalidOperationException>();
  }
}
