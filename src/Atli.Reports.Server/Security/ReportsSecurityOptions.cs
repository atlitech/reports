namespace Atli.Reports.Server.Security;

/// <summary>Server-only identity and admission settings. Credentials never enter the engine.</summary>
internal sealed class ReportsSecurityOptions
{
  public const string SectionName = "ReportsServer";

  public ReportsAuthenticationOptions Authentication { get; set; } = new();

  public int MaxConcurrentRequests { get; set; } = 128;

  public ReportsCallerLimits Limits { get; set; } = new();

  public ReportsCallerPolicy[] Callers { get; set; } = [];
}

internal sealed class ReportsAuthenticationOptions
{
  // An explicit None is required even in Development: environment names are not a trust boundary.
  public string Mode { get; set; } = "";

  public ReportsApiKey[] ApiKeys { get; set; } = [];

  public ReportsJwtOptions Jwt { get; set; } = new();
}

internal sealed class ReportsApiKey
{
  public string Id { get; set; } = "";

  // Base64 SHA-256 of the complete wire credential, including the public key identifier.
  public string Hash { get; set; } = "";

  public string CallerId { get; set; } = "";

  public string[] Permissions { get; set; } = [];

  public DateTimeOffset? ExpiresAt { get; set; }

  public bool Enabled { get; set; } = true;
}

internal sealed class ReportsJwtOptions
{
  public string Authority { get; set; } = "";

  public string Audience { get; set; } = "";

  public string PermissionClaimType { get; set; } = "roles";

  public string RequiredPermission { get; set; } = "reports.convert";

  public string CallerIdClaimType { get; set; } = "sub";
}

internal sealed class ReportsCallerLimits
{
  public int MaxConcurrentRequestsPerCaller { get; set; } = 4;

  public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

  // Covers request-body reading, engine queueing, rendering, and response streaming.
  public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
}

internal sealed class ReportsCallerPolicy
{
  public string CallerId { get; set; } = "";

  public ReportsCallerLimitOverrides Limits { get; set; } = new();
}

internal sealed class ReportsCallerLimitOverrides
{
  public int? MaxConcurrentRequestsPerCaller { get; set; }

  public long? MaxRequestBodyBytes { get; set; }

  public TimeSpan? RequestTimeout { get; set; }

  public ReportsCallerLimits ApplyTo(ReportsCallerLimits defaults) =>
    new()
    {
      MaxConcurrentRequestsPerCaller =
        MaxConcurrentRequestsPerCaller ?? defaults.MaxConcurrentRequestsPerCaller,
      MaxRequestBodyBytes = MaxRequestBodyBytes ?? defaults.MaxRequestBodyBytes,
      RequestTimeout = RequestTimeout ?? defaults.RequestTimeout,
    };
}
