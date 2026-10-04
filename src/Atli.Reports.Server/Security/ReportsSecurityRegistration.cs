using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Atli.Reports.Server.Security;

internal static class ReportsSecurityRegistration
{
  internal const string ApiKeyScheme = "ReportsApiKey";
  internal const string ConvertPolicy = "Reports.Convert";
  internal const string DiagnosticsPolicy = "Reports.Diagnostics";
  internal const string TenantsPolicy = "Reports.Tenants";
  internal const string TenantsPermission = "reports.tenants";
  internal const string CallerClaim = "atli.reports.caller";
  internal const string PartitionClaim = "atli.reports.partition";
  internal const string PermissionClaim = "atli.reports.permission";

  internal static void AddReportsSecurity(this WebApplicationBuilder builder)
  {
    // Bind once and validate before Build. In-place hot reload could revoke a key while retaining
    // its old quota policy; deployments apply the complete validated configuration atomically.
    var settings = new ReportsSecurityOptions();
    builder.Configuration.GetSection(ReportsSecurityOptions.SectionName).Bind(settings);
    Validate(settings);
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<CallerAdmission>();
    var authentication = builder.Services.AddAuthentication();
    var mode = settings.Authentication.Mode;
    if (mode == "ApiKey")
    {
      authentication.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyScheme,
        _ => { }
      );
      builder.Services.Configure<AuthenticationOptions>(options =>
        options.DefaultScheme = ApiKeyScheme
      );
    }
    else if (mode == "JwtBearer")
    {
      var jwt = settings.Authentication.Jwt;
      authentication.AddJwtBearer(options =>
      {
        options.Authority = jwt.Authority;
        options.Audience = jwt.Audience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
          ValidateIssuer = true,
          ValidateAudience = true,
          ValidateLifetime = true,
          RequireExpirationTime = true,
          RequireSignedTokens = true,
          ValidateIssuerSigningKey = true,
          // Do not accept a token for a different audience distinguished by a trailing slash.
          IgnoreTrailingSlashWhenValidatingAudience = false,
          ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
          OnTokenValidated = context =>
          {
            var principal = context.Principal!;
            var subjects = principal
              .Claims.Where(claim =>
                string.Equals(claim.Type, jwt.CallerIdClaimType, StringComparison.Ordinal)
              )
              .ToArray();
            if (subjects.Length != 1 || !ValidIdentifier(subjects[0].Value))
            {
              context.Fail("A unique caller identity is required.");
              return Task.CompletedTask;
            }

            // Only claims created here can identify a caller. An incoming token cannot supply our
            // internal claim namespace, and request headers/body never participate in identity.
            var permissionClaims = principal.Claims.Where(claim =>
              string.Equals(claim.Type, jwt.PermissionClaimType, StringComparison.Ordinal)
            );
            var permissions = jwt.PermissionClaimType is "scope" or "scp"
              ? permissionClaims.SelectMany(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
              )
              : permissionClaims.Select(claim => claim.Value);
            var caller = subjects[0].Value;
            // Discard all incoming claims. Authorization handlers match claim types case-insensitively;
            // removing only exact reserved names would allow differently cased forged internal claims.
            context.Principal = new ClaimsPrincipal(
              new ClaimsIdentity(
                [
                  new Claim(CallerClaim, caller),
                  new Claim(PartitionClaim, context.SecurityToken.Issuer + "\n" + caller),
                  .. permissions.Select(value => new Claim(PermissionClaim, value)),
                ],
                JwtBearerDefaults.AuthenticationScheme
              )
            );
            return Task.CompletedTask;
          },
        };
      });
      builder.Services.Configure<AuthenticationOptions>(options =>
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme
      );
    }

    builder
      .Services.AddAuthorizationBuilder()
      .AddPolicy(
        ConvertPolicy,
        policy =>
        {
          if (mode == "None")
          {
            policy.RequireAssertion(_ => true);
          }
          else
          {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(CallerClaim);
            policy.RequireClaim(
              PermissionClaim,
              mode == "JwtBearer"
                ? settings.Authentication.Jwt.RequiredPermission
                : "reports.convert"
            );
          }
        }
      )
      .AddPolicy(
        DiagnosticsPolicy,
        policy =>
        {
          if (mode == "None")
          {
            policy.RequireAssertion(_ => true);
          }
          else
          {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(PermissionClaim, "reports.diagnostics");
          }
        }
      )
      .AddPolicy(
        TenantsPolicy,
        policy =>
        {
          if (mode == "None")
          {
            policy.RequireAssertion(_ => true);
          }
          else
          {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(CallerClaim);
            policy.RequireClaim(
              PermissionClaim,
              mode == "JwtBearer"
                ? settings.Authentication.Jwt.TenantsPermission
                : TenantsPermission
            );
          }
        }
      );
  }

  private static bool ValidIdentifier(string value) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

  private static void Validate(ReportsSecurityOptions settings)
  {
    var auth = settings.Authentication;
    if (auth.Mode is not ("None" or "ApiKey" or "JwtBearer"))
    {
      throw new InvalidOperationException(
        "Configure ReportsServer:Authentication:Mode as ApiKey or JwtBearer. "
          + "Explicitly select None only when anonymous access is intended."
      );
    }

    if (auth.Mode == "ApiKey")
    {
      if (auth.ApiKeys.Length == 0)
      {
        throw new InvalidOperationException(
          "ApiKey authentication requires at least one configured key."
        );
      }

      HashSet<string> identifiers = new(StringComparer.Ordinal);
      Span<byte> hash = stackalloc byte[32];
      foreach (var key in auth.ApiKeys)
      {
        if (
          key.Id.Length is < 1 or > 64
          || key.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
          || !identifiers.Add(key.Id)
          || !ValidIdentifier(key.CallerId)
          || key.Permissions.Any(string.IsNullOrWhiteSpace)
        )
        {
          throw new InvalidOperationException(
            "API keys require unique URL-safe identifiers, a caller identity, and valid permissions."
          );
        }

        if (!Convert.TryFromBase64String(key.Hash, hash, out var written) || written != 32)
        {
          throw new InvalidOperationException(
            "Every API key Hash must be a base64-encoded SHA-256 verifier."
          );
        }
      }
    }

    if (auth.Mode == "JwtBearer")
    {
      var jwt = auth.Jwt;
      if (
        !Uri.TryCreate(jwt.Authority, UriKind.Absolute, out var authority)
        || authority.Scheme != Uri.UriSchemeHttps
        || !string.IsNullOrEmpty(authority.UserInfo)
        || !string.IsNullOrEmpty(authority.Query)
        || !string.IsNullOrEmpty(authority.Fragment)
        || string.IsNullOrWhiteSpace(jwt.Audience)
        || !ValidIdentifier(jwt.CallerIdClaimType)
        || !ValidIdentifier(jwt.PermissionClaimType)
        || !ValidIdentifier(jwt.RequiredPermission)
        || !ValidIdentifier(jwt.TenantsPermission)
        || jwt.CallerIdClaimType.StartsWith("atli.reports.", StringComparison.OrdinalIgnoreCase)
        || jwt.PermissionClaimType.StartsWith("atli.reports.", StringComparison.OrdinalIgnoreCase)
      )
      {
        throw new InvalidOperationException(
          "JwtBearer authentication requires an HTTPS Authority, an Audience, and valid caller/permission claim settings."
        );
      }

      // Otherwise every caller that may convert could delete its tenants' renderers.
      if (string.Equals(jwt.TenantsPermission, jwt.RequiredPermission, StringComparison.Ordinal))
      {
        throw new InvalidOperationException(
          "ReportsServer:Authentication:Jwt:TenantsPermission must differ from Jwt:RequiredPermission."
        );
      }
    }

    if (settings.MaxConcurrentRequests is < 1 or > 100_000)
    {
      throw new InvalidOperationException(
        "ReportsServer:MaxConcurrentRequests must be between 1 and 100000."
      );
    }
    ValidateLimits(settings.Limits);
    HashSet<string> callers = new(StringComparer.Ordinal);
    foreach (var caller in settings.Callers)
    {
      if (!ValidIdentifier(caller.CallerId) || !callers.Add(caller.CallerId))
      {
        throw new InvalidOperationException(
          "Caller policies require unique, non-empty CallerId values."
        );
      }
      ValidateLimits(caller.Limits.ApplyTo(settings.Limits));
    }
  }

  private static void ValidateLimits(ReportsCallerLimits limits)
  {
    if (
      limits.MaxConcurrentRequestsPerCaller is < 1 or > 10_000
      || limits.MaxRequestBodyBytes is < 1 or > int.MaxValue
      || limits.RequestTimeout < TimeSpan.FromMilliseconds(1)
      || limits.RequestTimeout > TimeSpan.FromHours(24)
    )
    {
      throw new InvalidOperationException(
        "Caller limits require 1-10000 concurrent requests, 1-2147483647 body bytes, and a timeout between 1ms and 24h."
      );
    }
  }
}
