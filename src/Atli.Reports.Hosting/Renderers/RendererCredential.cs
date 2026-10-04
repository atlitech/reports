using System.Security.Cryptography;
using System.Text;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// A renderer's own API key, in the server's API-key format (the format
/// <c>scripts/create-reports-api-key.sh</c> writes): <c>reports-&lt;12 hex&gt;.&lt;64 hex&gt;</c>. The
/// gateway presents <see cref="Credential"/>; the renderer holds only <see cref="Verifier"/>.
/// </summary>
/// <param name="KeyId">The public key identifier, the part before the dot.</param>
/// <param name="Credential">The complete credential; secret.</param>
/// <param name="Verifier">Base64 SHA-256 of <see cref="Credential"/>, the server's <c>Hash</c> setting.</param>
public sealed record RendererCredential(string KeyId, string Credential, string Verifier)
{
  /// <summary>Generates a new credential from the system's cryptographic random number generator.</summary>
  public static RendererCredential Generate()
  {
    var keyId = "reports-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
    var credential = keyId + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    return new RendererCredential(keyId, credential, VerifierOf(credential));
  }

  /// <summary>The server's verifier for a complete credential.</summary>
  public static string VerifierOf(string credential) =>
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

  /// <inheritdoc />
  public override string ToString() => $"RendererCredential {{ KeyId = {KeyId} }}";
}
