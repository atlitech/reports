using System.Security.Cryptography;
using System.Text;
using Atli.Reports.Hosting.Provisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Provisioner.Service;

/// <summary>
/// The gateway's credentials for the service, <c>Provisioner:Service:ApiKeys</c>: each key's ID and
/// the SHA-256 of the whole <c>id.secret</c> key, as the server keeps its API keys.
/// </summary>
internal sealed class ServiceApiKeys
{
  /// <summary>The longest key accepted, as the server's.</summary>
  public const int MaxLength = 1024;

  private readonly Dictionary<string, byte[]> _hashes;

  /// <param name="keys">The configured keys; <see cref="ProvisioningServiceOptions.Validate"/> has checked them.</param>
  public ServiceApiKeys(IEnumerable<ProvisioningServiceApiKey> keys) =>
    _hashes = keys.ToDictionary(
      key => key.Id,
      key => Convert.FromBase64String(key.Hash),
      StringComparer.Ordinal
    );

  /// <summary>
  /// The ID of the configured key that <paramref name="key"/> is, or <see langword="null"/>: the part
  /// before the first dot names the key, and the SHA-256 of the whole key must equal its hash,
  /// compared in constant time.
  /// </summary>
  public string? Verify(string key)
  {
    ArgumentNullException.ThrowIfNull(key);
    var dot = key.IndexOf('.', StringComparison.Ordinal);
    if (
      key.Length > MaxLength
      || dot < 1
      || dot == key.Length - 1
      || !_hashes.TryGetValue(key[..dot], out var expected)
    )
    {
      return null;
    }

    Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
    SHA256.HashData(Encoding.UTF8.GetBytes(key), actual);
    return CryptographicOperations.FixedTimeEquals(actual, expected) ? key[..dot] : null;
  }
}

/// <summary>
/// Admits a request only with one of the gateway's keys in <see cref="ProvisioningApi.ApiKeyHeader"/>,
/// unknown routes included; only the health probes need none. Any other request is answered
/// <c>401</c>, which status code pages write as problem details.
/// </summary>
/// <remarks>
/// A middleware rather than an ASP.NET Core authentication scheme, which would bring Data
/// Protection and its key ring for nothing: the service has no cookies, users, or policies.
/// </remarks>
internal sealed partial class GatewayAuthentication(
  RequestDelegate next,
  ServiceApiKeys keys,
  ILogger<GatewayAuthentication> logger
)
{
  /// <summary>The <c>WWW-Authenticate</c> challenge of a <c>401</c>.</summary>
  public const string Challenge = "ProvisioningApiKey";

  /// <summary>Where the health probes are, which need no key.</summary>
  public static readonly PathString HealthPath = new("/health");

  public Task InvokeAsync(HttpContext context)
  {
    if (context.Request.Path.StartsWithSegments(HealthPath))
    {
      return next(context);
    }

    var values = context.Request.Headers[ProvisioningApi.ApiKeyHeader];
    if (values.Count == 1 && values[0] is { } key && keys.Verify(key) is not null)
    {
      return next(context);
    }

    // Nothing the caller sent is logged: neither the key nor the path.
    LogRefused(logger, values.Count == 0 ? "no key" : "a key that is not one of them");
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.Headers.WWWAuthenticate = Challenge;
    return Task.CompletedTask;
  }

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Warning,
    Message = "Refused a request with {Presented}: the gateway's keys are Provisioner:Service:ApiKeys."
  )]
  private static partial void LogRefused(ILogger logger, string presented);
}
