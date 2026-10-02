using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

public static partial class ReportsServerBuilderExtensions
{
  private const string AuthenticationPrefix = "ReportsServer__Authentication__";

  /// <summary>
  /// Authenticates a calling application with a secret API credential and its SHA-256 verifier.
  /// The server receives only the verifier; <c>WithReference</c> supplies the credential to clients.
  /// </summary>
  /// <param name="builder">The reports server.</param>
  /// <param name="keyId">The identifier before the dot in the credential.</param>
  /// <param name="apiKey">A secret parameter containing the full <c>key-id.secret</c> credential.</param>
  /// <param name="apiKeyHash">A secret parameter containing base64 SHA-256 of the entire credential.</param>
  /// <param name="callerId">A stable calling application identity; defaults to the key identifier.</param>
  /// <returns>The reports server builder.</returns>
  /// <remarks>
  /// Grants <c>reports.convert</c>. Use distinct keys and caller identities for independent
  /// applications. This helper configures one client identity; more server keys can be configured
  /// through <c>WithEnvironment</c>. Keep parameters in the deployment's secret store.
  /// </remarks>
  public static IResourceBuilder<ReportsServerResource> WithApiKeyAuthentication(
    this IResourceBuilder<ReportsServerResource> builder,
    string keyId,
    IResourceBuilder<ParameterResource> apiKey,
    IResourceBuilder<ParameterResource> apiKeyHash,
    string? callerId = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(apiKey);
    ArgumentNullException.ThrowIfNull(apiKeyHash);
    ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
    if (
      keyId.Length > 64
      || keyId.Any(character =>
        !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'
      )
    )
    {
      throw new ArgumentException(
        "Use up to 64 ASCII letters, digits, hyphens, or underscores for the key identifier.",
        nameof(keyId)
      );
    }
    if (!apiKey.Resource.Secret || !apiKeyHash.Resource.Secret)
    {
      throw new ArgumentException(
        "The API credential and verifier parameters must be marked secret."
      );
    }
    if (callerId is not null)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
    }

    builder.Resource.ApiKeyParameter = apiKey.Resource;
    builder.Resource.AnonymousAccess = false;
    return builder
      .WithEnvironment(AuthenticationPrefix + "Mode", "ApiKey")
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Id", keyId)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Hash", apiKeyHash)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__CallerId", callerId ?? keyId)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Permissions__0", "reports.convert");
  }

  /// <summary>
  /// Creates a fresh random API credential for this local Aspire run and wires authenticated
  /// clients and the dashboard test command. No credential is persisted or published.
  /// </summary>
  /// <param name="builder">The reports server.</param>
  /// <returns>The reports server builder.</returns>
  /// <exception cref="InvalidOperationException">The application is being published.</exception>
  /// <remarks>
  /// For deployment, call <see cref="WithApiKeyAuthentication"/> with secret parameters instead.
  /// Select the helper using the AppHost execution context's <c>IsRunMode</c>.
  /// </remarks>
  public static IResourceBuilder<ReportsServerResource> WithDevelopmentApiKey(
    this IResourceBuilder<ReportsServerResource> builder
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
    {
      throw new InvalidOperationException(
        "Development API keys cannot be published. Configure WithApiKeyAuthentication with deployment secret parameters."
      );
    }

    const string keyId = "development";
    var credential = keyId + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    var apiKey = builder.ApplicationBuilder.AddParameter(
      builder.Resource.Name + "-api-key",
      credential,
      secret: true
    );
    var apiKeyHash = builder.ApplicationBuilder.AddParameter(
      builder.Resource.Name + "-api-key-hash",
      hash,
      secret: true
    );
    return builder.WithApiKeyAuthentication(keyId, apiKey, apiKeyHash, builder.Resource.Name);
  }

  /// <summary>
  /// Explicitly permits anonymous conversion requests. Use only for a trusted local development
  /// environment or behind an operator-managed authentication boundary that prevents direct access.
  /// </summary>
  /// <param name="builder">The reports server.</param>
  /// <returns>The reports server builder.</returns>
  public static IResourceBuilder<ReportsServerResource> WithAnonymousAccess(
    this IResourceBuilder<ReportsServerResource> builder
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    builder.Resource.ApiKeyParameter = null;
    builder.Resource.AnonymousAccess = true;
    return builder.WithEnvironment(AuthenticationPrefix + "Mode", "None");
  }
}
