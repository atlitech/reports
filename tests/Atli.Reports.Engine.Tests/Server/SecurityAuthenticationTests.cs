using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atli.Reports.Engine.Tests.Support;
using Atli.Reports.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Atli.Reports.Engine.Tests.Server;

/// <summary>Exercises real authentication/authorization middleware before body binding and conversion.</summary>
public class SecurityAuthenticationTests
{
  private const string Caller = "billing";
  private static readonly string[] ConversionRoles = ["reports.convert"];
  private const string Issuer = "https://identity.example.test/";
  private const string Audience = "atli-reports";
  private const string Credential = "primary.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
  private static CancellationToken TestToken => TestContext.Current!.Execution.CancellationToken;

  [Test]
  public async Task Missing_authentication_configuration_fails_even_in_development()
  {
    await Assert
      .That(() =>
        ReportsServerApplication.Create([
          "--environment=Development",
          "--ReportsServer:Authentication:Mode=",
        ])
      )
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Standalone_server_network_defaults_to_disabled_without_a_settings_file()
  {
    await using var server = await SecurityServer.StartAsync(
      SuccessfulConverter(),
      ["--ReportsServer:Authentication:Mode=None"],
      builder =>
      {
        foreach (
          var source in builder
            .Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
            .ToArray()
        )
        {
          builder.Configuration.Sources.Remove(source);
        }
      }
    );
    var options = server.Services.GetRequiredService<IOptions<ReportsEngineOptions>>().Value;
    await Assert.That(options.Network.Mode).IsEqualTo(ReportsEngineNetworkMode.Disabled);
  }

  [Test]
  [Arguments("ApiKey", "--ReportsServer:Authentication:ApiKeys:0:Hash=invalid")]
  [Arguments("JwtBearer", "--ReportsServer:Authentication:Jwt:Authority=http://identity.test")]
  [Arguments("None", "--ReportsServer:Limits:RequestTimeout=00:00:00")]
  [Arguments("None", "--ReportsServer:MaxConcurrentRequests=0")]
  [Arguments("None", "--ReportsServer:Limits:MaxRequestBodyBytes=0")]
  public async Task Invalid_security_configuration_fails_at_startup(string mode, string setting)
  {
    await Assert
      .That(() =>
        ReportsServerApplication.Create([$"--ReportsServer:Authentication:Mode={mode}", setting])
      )
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Missing_or_wrong_credentials_return_401_before_body_binding()
  {
    var converter = SuccessfulConverter();
    await using var server = await SecurityServer.StartAsync(converter, ApiKeySettings());
    foreach (var credential in new[] { "", Credential + "wrong", "missing." + new string('x', 43) })
    {
      using var request = ConvertRequest("not json", credential);
      // None of these headers is an authenticated identity or permission.
      request.Headers.Add("X-Tenant-Id", Caller);
      request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", Caller);
      request.Headers.Add("X-Reports-Caller-Id", Caller);
      using var response = await server.Client.SendAsync(request, TestToken);
      await AssertProblem(response, HttpStatusCode.Unauthorized, "Unauthorized");
      await Assert
        .That(response.Headers.WwwAuthenticate.Single().Scheme)
        .IsEqualTo("ReportsApiKey");
    }
    await Assert.That(converter.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Valid_key_requires_permission_and_does_not_grant_diagnostics()
  {
    var converter = SuccessfulConverter();
    await using (
      var server = await SecurityServer.StartAsync(
        converter,
        ApiKeySettings(permission: "reports.read")
      )
    )
    {
      using var request = ConvertRequest("not json", Credential);
      using var response = await server.Client.SendAsync(request, TestToken);
      await AssertProblem(response, HttpStatusCode.Forbidden, "Forbidden");
    }
    await using (var server = await SecurityServer.StartAsync(converter, ApiKeySettings()))
    {
      using var request = ConvertRequest("""{"html":"<p>safe</p>"}""", Credential);
      using var response = await server.Client.SendAsync(request, TestToken);
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
      foreach (var path in new[] { "/openapi/v1.json", "/health/details" })
      {
        using var diagnostics = new HttpRequestMessage(HttpMethod.Get, path);
        diagnostics.Headers.Add("X-Reports-Api-Key", Credential);
        using var denied = await server.Client.SendAsync(diagnostics, TestToken);
        await AssertProblem(denied, HttpStatusCode.Forbidden, "Forbidden");
      }
    }
    await Assert.That(converter.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task Key_rotation_accepts_overlapping_keys_but_not_expired_or_revoked_keys()
  {
    var second = "replacement." + new string('y', 43);
    var expired = "expired." + new string('z', 43);
    var revoked = "revoked." + new string('w', 43);
    var settings = ApiKeySettings()
      .Concat(KeySettings(1, "replacement", second))
      .Concat(KeySettings(2, "expired", expired))
      .Concat(KeySettings(3, "revoked", revoked))
      .Concat([
        "--ReportsServer:Authentication:ApiKeys:2:ExpiresAt=2000-01-01T00:00:00Z",
        "--ReportsServer:Authentication:ApiKeys:3:Enabled=false",
      ])
      .ToArray();
    var converter = SuccessfulConverter();
    await using var server = await SecurityServer.StartAsync(converter, settings);
    foreach (var key in new[] { Credential, second, expired, revoked })
    {
      using var request = ConvertRequest("""{"html":"ok"}""", key);
      using var response = await server.Client.SendAsync(request, TestToken);
      await Assert
        .That(response.StatusCode)
        .IsEqualTo(
          key == Credential || key == second ? HttpStatusCode.OK : HttpStatusCode.Unauthorized
        );
    }
    await Assert.That(converter.Calls).IsEqualTo(2);
  }

  [Test]
  public async Task Repeated_api_key_headers_are_rejected()
  {
    await using var server = await SecurityServer.StartAsync(
      SuccessfulConverter(),
      ApiKeySettings()
    );
    using var request = ConvertRequest("{}", Credential);
    request.Headers.Add("X-Reports-Api-Key", Credential);
    using var response = await server.Client.SendAsync(request, TestToken);
    await AssertProblem(response, HttpStatusCode.Unauthorized, "Unauthorized");
  }

  [Test]
  public async Task Anonymous_health_is_minimal_and_diagnostics_requires_its_permission()
  {
    await using var server = await SecurityServer.StartAsync(
      SuccessfulConverter(),
      ApiKeySettings(permission: "reports.diagnostics")
    );
    foreach (var path in new[] { "/health/live", "/health/ready" })
    {
      using var response = await server.Client.GetAsync(path, TestToken);
      using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
      await Assert.That(json.RootElement.EnumerateObject().Count()).IsEqualTo(1);
      await Assert.That(json.RootElement.TryGetProperty("status", out _)).IsTrue();
    }
    using var request = new HttpRequestMessage(HttpMethod.Get, "/health/details");
    request.Headers.Add("X-Reports-Api-Key", Credential);
    using var details = await server.Client.SendAsync(request, TestToken);
    using var document = JsonDocument.Parse(await details.Content.ReadAsStringAsync(TestToken));
    await Assert.That(document.RootElement.TryGetProperty("checks", out _)).IsTrue();
    using var openApiRequest = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
    openApiRequest.Headers.Add("X-Reports-Api-Key", Credential);
    using var openApiResponse = await server.Client.SendAsync(openApiRequest, TestToken);
    await Assert.That(openApiResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
    using var openApi = JsonDocument.Parse(
      await openApiResponse.Content.ReadAsStringAsync(TestToken)
    );
    var scheme = openApi
      .RootElement.GetProperty("components")
      .GetProperty("securitySchemes")
      .GetProperty("ReportsAuthentication");
    await Assert.That(scheme.GetProperty("type").GetString()).IsEqualTo("apiKey");
    await Assert.That(scheme.GetProperty("name").GetString()).IsEqualTo("X-Reports-Api-Key");
    await Assert
      .That(
        openApi
          .RootElement.GetProperty("security")[0]
          .TryGetProperty("ReportsAuthentication", out _)
      )
      .IsTrue();
    using var unauthorized = await server.Client.GetAsync("/health/details", TestToken);
    await AssertProblem(unauthorized, HttpStatusCode.Unauthorized, "Unauthorized");
  }

  [Test]
  [Arguments("valid", 200)]
  [Arguments("wrong-issuer", 401)]
  [Arguments("wrong-audience", 401)]
  [Arguments("expired", 401)]
  [Arguments("wrong-signature", 401)]
  [Arguments("missing-subject", 401)]
  [Arguments("missing-permission", 403)]
  [Arguments("forged-internal-permission", 403)]
  [Arguments("forged-internal-caller", 401)]
  [Arguments("mixed-case-internal-permission", 403)]
  [Arguments("role-containing-permission", 403)]
  [Arguments("scope-with-permission", 200)]
  [Arguments("custom-claim-containing-permission", 403)]
  public async Task Jwt_validation_and_permission_checks_use_validated_claims(
    string scenario,
    int expectedStatus
  )
  {
    SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "trusted" };
    var converter = SuccessfulConverter();
    await using var server = await SecurityServer.StartAsync(
      converter,
      [
        .. JwtSettings(),
        $"--ReportsServer:Authentication:Jwt:PermissionClaimType={(scenario == "scope-with-permission" ? "scp" : scenario == "custom-claim-containing-permission" ? "permissions" : "roles")}",
      ],
      builder => ConfigureSigningKeys(builder, key)
    );
    Dictionary<string, object> claims = [];
    if (scenario is not ("missing-subject" or "forged-internal-caller"))
    {
      claims["sub"] = Caller;
    }
    if (
      scenario
      is not (
        "missing-permission"
        or "forged-internal-permission"
        or "mixed-case-internal-permission"
      )
    )
    {
      claims["roles"] = ConversionRoles;
    }
    if (scenario == "role-containing-permission")
    {
      claims["roles"] = "other reports.convert";
    }
    claims["permissions"] = "other reports.convert";
    claims["scp"] = "other reports.convert";
    claims["ATLI.REPORTS.PERMISSION"] = "reports.convert";
    claims["atli.reports.caller"] = "forged";
    claims["atli.reports.permission"] = "reports.convert";
    var token = new JsonWebTokenHandler().CreateToken(
      new SecurityTokenDescriptor
      {
        Issuer = scenario == "wrong-issuer" ? "https://other.example.test/" : Issuer,
        Audience = scenario == "wrong-audience" ? "some-other-api" : Audience,
        Claims = claims,
        IssuedAt = DateTime.UtcNow.AddHours(-2),
        NotBefore = DateTime.UtcNow.AddHours(-2),
        Expires =
          scenario == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(
          scenario == "wrong-signature"
            ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = key.KeyId }
            : key,
          SecurityAlgorithms.HmacSha256
        ),
      }
    );
    using var request = ConvertRequest("""{"html":"safe"}""");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await server.Client.SendAsync(request, TestToken);
    await Assert.That((int)response.StatusCode).IsEqualTo(expectedStatus);
    await Assert.That(converter.Calls).IsEqualTo(expectedStatus == 200 ? 1 : 0);
    if (expectedStatus != 200)
    {
      var body = await response.Content.ReadAsStringAsync(TestToken);
      await Assert.That(body).DoesNotContain(token);
      await Assert.That(body).DoesNotContain("IDX");
    }
  }

  [Test]
  public async Task Jwt_accepts_overlapping_trusted_signing_keys()
  {
    SymmetricSecurityKey oldKey = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "old" };
    SymmetricSecurityKey newKey = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "new" };
    await using var server = await SecurityServer.StartAsync(
      SuccessfulConverter(),
      JwtSettings(),
      builder => ConfigureSigningKeys(builder, oldKey, newKey)
    );
    foreach (var key in new[] { oldKey, newKey })
    {
      var token = new JsonWebTokenHandler().CreateToken(
        new SecurityTokenDescriptor
        {
          Issuer = Issuer,
          Audience = Audience,
          Claims = new Dictionary<string, object> { ["sub"] = Caller, ["roles"] = ConversionRoles },
          Expires = DateTime.UtcNow.AddMinutes(5),
          SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        }
      );
      using var request = ConvertRequest("""{"html":"safe"}""");
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      using var response = await server.Client.SendAsync(request, TestToken);
      await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
  }

  [Test]
  public async Task Jwt_discovers_RSA_signing_keys_through_standard_HTTPS_metadata_and_JWKS()
  {
    using var rsa = RSA.Create(2048);
    RsaSecurityKey signingKey = new(rsa) { KeyId = "rsa-key" };
    var parameters = rsa.ExportParameters(false);
    var jwks =
      $$$"""{"keys":[{"kty":"RSA","use":"sig","kid":"rsa-key","alg":"RS256","n":"{{{Base64UrlEncoder.Encode(parameters.Modulus!)}}}","e":"{{{Base64UrlEncoder.Encode(parameters.Exponent!)}}}"}]}""";
    using MetadataHandler handler = new(jwks);
    using HttpClient backchannel = new(handler);
    await using var server = await SecurityServer.StartAsync(
      SuccessfulConverter(),
      JwtSettings(),
      builder =>
        builder.Services.PostConfigure<JwtBearerOptions>(
          JwtBearerDefaults.AuthenticationScheme,
          options => options.Backchannel = backchannel
        )
    );
    var token = new JsonWebTokenHandler().CreateToken(
      new SecurityTokenDescriptor
      {
        Issuer = Issuer,
        Audience = Audience,
        Claims = new Dictionary<string, object> { ["sub"] = Caller, ["roles"] = ConversionRoles },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
      }
    );
    using var request = ConvertRequest();
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await server.Client.SendAsync(request, TestToken);
    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(handler.DiscoveryRequests).IsEqualTo(1);
    await Assert.That(handler.KeyRequests).IsEqualTo(1);
  }

  private sealed class MetadataHandler(string jwks) : HttpMessageHandler
  {
    public int DiscoveryRequests { get; private set; }
    public int KeyRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken
    )
    {
      string content;
      if (request.RequestUri!.AbsoluteUri == Issuer + ".well-known/openid-configuration")
      {
        DiscoveryRequests++;
        content = $$$"""{"issuer":"{{{Issuer}}}","jwks_uri":"{{{Issuer}}}keys"}""";
      }
      else if (request.RequestUri.AbsoluteUri == Issuer + "keys")
      {
        KeyRequests++;
        content = jwks;
      }
      else
      {
        throw new InvalidOperationException("Unexpected identity metadata request.");
      }
      return Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(content, Encoding.UTF8, "application/json"),
        }
      );
    }
  }

  private static void ConfigureSigningKeys(
    WebApplicationBuilder builder,
    params SecurityKey[] keys
  ) =>
    builder.Services.PostConfigure<JwtBearerOptions>(
      JwtBearerDefaults.AuthenticationScheme,
      options =>
      {
        // A test-only trusted metadata snapshot. Production always discovers signing keys over HTTPS.
        options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        foreach (var key in keys)
        {
          options.Configuration.SigningKeys.Add(key);
        }
      }
    );

  internal static string[] JwtSettings() =>
    [
      "--ReportsServer:Authentication:Mode=JwtBearer",
      $"--ReportsServer:Authentication:Jwt:Authority={Issuer}",
      $"--ReportsServer:Authentication:Jwt:Audience={Audience}",
    ];

  internal static string[] ApiKeySettings(string permission = "reports.convert") =>
    [
      "--ReportsServer:Authentication:Mode=ApiKey",
      .. KeySettings(0, "primary", Credential, permission),
    ];

  internal static string[] KeySettings(
    int index,
    string id,
    string credential,
    string permission = "reports.convert",
    string caller = Caller
  ) =>
    [
      $"--ReportsServer:Authentication:ApiKeys:{index}:Id={id}",
      $"--ReportsServer:Authentication:ApiKeys:{index}:Hash={Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))}",
      $"--ReportsServer:Authentication:ApiKeys:{index}:CallerId={caller}",
      $"--ReportsServer:Authentication:ApiKeys:{index}:Permissions:0={permission}",
    ];

  internal static HttpRequestMessage ConvertRequest(
    string body = """{"html":"safe"}""",
    string credential = ""
  )
  {
    var request = new HttpRequestMessage(HttpMethod.Post, "/convert")
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    if (credential.Length > 0)
    {
      request.Headers.Add("X-Reports-Api-Key", credential);
    }
    return request;
  }

  internal static FakeConverter SuccessfulConverter() =>
    new((_, _) => Task.FromResult<ConversionError?>(null));

  private static async Task AssertProblem(
    HttpResponseMessage response,
    HttpStatusCode status,
    string kind
  )
  {
    await Assert.That(response.StatusCode).IsEqualTo(status);
    await Assert
      .That(response.Content.Headers.ContentType?.MediaType)
      .IsEqualTo("application/problem+json");
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestToken));
    await Assert.That(body.RootElement.GetProperty("kind").GetString()).IsEqualTo(kind);
  }

  internal sealed class SecurityServer(WebApplication app) : IAsyncDisposable
  {
    public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

    public IServiceProvider Services => app.Services;

    public static async Task<SecurityServer> StartAsync(
      IHtmlToPdfConverter converter,
      string[] settings,
      Action<WebApplicationBuilder>? configure = null
    )
    {
      var app = ReportsServerApplication.Create(
        ["--urls=http://127.0.0.1:0", "--ReportsEngine:Browser:WarmUpOnStartup=false", .. settings],
        builder =>
        {
          builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
          builder.Services.AddSingleton(converter);
          configure?.Invoke(builder);
        }
      );
      try
      {
        await app.StartAsync(TestToken);
        return new SecurityServer(app);
      }
      catch
      {
        await app.DisposeAsync();
        throw;
      }
    }

    public async ValueTask DisposeAsync()
    {
      Client.Dispose();
      await app.StopAsync(TestToken);
      await app.DisposeAsync();
    }
  }
}
