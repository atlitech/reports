using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atli.Reports.Hosting.Renderers;

namespace Atli.Reports.Hosting.Tests.Renderers;

/// <summary>
/// A renderer's own credential, in the format <c>scripts/create-reports-api-key.sh</c> writes.
/// </summary>
public partial class RendererCredentialTests
{
  [Test]
  public async Task A_generated_credential_has_the_servers_api_key_format()
  {
    var credential = RendererCredential.Generate();

    await Assert.That(CredentialFormat().IsMatch(credential.Credential)).IsTrue();
    await Assert.That(credential.Credential).StartsWith(credential.KeyId + ".");
    await Assert.That(KeyIdFormat().IsMatch(credential.KeyId)).IsTrue();
  }

  [Test]
  public async Task The_verifier_is_the_base64_sha256_of_the_whole_credential()
  {
    var credential = RendererCredential.Generate();

    await Assert
      .That(credential.Verifier)
      .IsEqualTo(
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Credential)))
      );
  }

  [Test]
  public async Task The_verifier_matches_the_scripts()
  {
    // printf '%s' "$credential" | openssl dgst -sha256 -binary | openssl base64 -A
    var verifier = RendererCredential.VerifierOf("reports-0123456789ab." + new string('0', 64));

    await Assert.That(verifier).IsEqualTo("tbSy29a/ecp8CSqJvqmp5HMZqjn/Eyjwx/uBgCY+et8=");
  }

  [Test]
  public async Task Every_credential_is_new()
  {
    var credentials = Enumerable.Range(0, 20).Select(_ => RendererCredential.Generate()).ToList();

    await Assert.That(credentials.Select(c => c.KeyId).Distinct().Count()).IsEqualTo(20);
    await Assert.That(credentials.Select(c => c.Credential).Distinct().Count()).IsEqualTo(20);
  }

  [Test]
  public async Task Its_string_form_names_the_key_but_hides_the_secret()
  {
    var credential = RendererCredential.Generate();

    var text = credential.ToString();

    await Assert.That(text).Contains(credential.KeyId);
    await Assert.That(text).DoesNotContain(credential.Credential);
    await Assert.That(text).DoesNotContain(credential.Credential[(credential.KeyId.Length + 1)..]);
    await Assert.That(text).DoesNotContain(credential.Verifier);
  }

  [GeneratedRegex("^reports-[0-9a-f]{12}\\.[0-9a-f]{64}$")]
  private static partial Regex CredentialFormat();

  [GeneratedRegex("^reports-[0-9a-f]{12}$")]
  private static partial Regex KeyIdFormat();
}
