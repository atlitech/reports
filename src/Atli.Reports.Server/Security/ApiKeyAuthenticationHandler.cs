using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Atli.Reports.Server.Security;

internal sealed class ApiKeyAuthenticationHandler(
  IOptionsMonitor<AuthenticationSchemeOptions> options,
  ILoggerFactory logger,
  UrlEncoder encoder,
  ReportsSecurityOptions settings
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  internal const string HeaderName = "X-Reports-Api-Key";

  protected override Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    if (!Request.Headers.TryGetValue(HeaderName, out var values))
    {
      return Task.FromResult(AuthenticateResult.NoResult());
    }

    if (values.Count != 1 || values[0] is not { Length: <= 1024 } credential)
    {
      return Invalid();
    }

    var separator = credential.IndexOf('.');
    if (separator is < 1 or > 64 || credential.Length - separator - 1 < 43)
    {
      return Invalid();
    }

    var id = credential[..separator];
    var key = Array.Find(
      settings.Authentication.ApiKeys,
      candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)
    );
    if (key is null || !key.Enabled || key.ExpiresAt <= TimeProvider.GetUtcNow())
    {
      return Invalid();
    }

    var actual = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
    var expected = Convert.FromBase64String(key.Hash);
    if (!CryptographicOperations.FixedTimeEquals(actual, expected))
    {
      return Invalid();
    }

    ClaimsIdentity identity = new(
      [
        new Claim(ReportsSecurityRegistration.CallerClaim, key.CallerId),
        new Claim(ReportsSecurityRegistration.PartitionClaim, key.CallerId),
        .. key.Permissions.Select(permission => new Claim(
          ReportsSecurityRegistration.PermissionClaim,
          permission
        )),
      ],
      Scheme.Name
    );
    return Task.FromResult(
      AuthenticateResult.Success(
        new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)
      )
    );
  }

  protected override Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    Response.StatusCode = StatusCodes.Status401Unauthorized;
    Response.Headers.WWWAuthenticate = ReportsSecurityRegistration.ApiKeyScheme;
    return Task.CompletedTask;
  }

  private static Task<AuthenticateResult> Invalid() =>
    Task.FromResult(AuthenticateResult.Fail("Invalid API credential."));
}
