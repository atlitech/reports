using Atli.Reports.Hosting.Renderers;
using Atli.Reports.Hosting.Sandboxes;

namespace Atli.Reports.Server.Gateway;

/// <summary>
/// <c>ReportsServer:Mode</c>: whether the server converts documents itself or routes them to each
/// tenant's renderer.
/// </summary>
internal enum ReportsServerMode
{
  /// <summary>The default: the reports engine converts in this process.</summary>
  Integrated,

  /// <summary>
  /// The hosted design's shared API: authenticate, resolve the product tenant, and relay the
  /// conversion to that tenant's renderer. No engine and no browser run here.
  /// </summary>
  Gateway,
}

/// <summary>
/// Gateway mode's settings, <c>ReportsServer:Gateway</c>. Like the security settings they bind once
/// and are validated before the application is built; changing them needs a restart.
/// </summary>
internal sealed class GatewayOptions
{
  public const string ModeKey = "ReportsServer:Mode";

  public const string SectionName = "ReportsServer:Gateway";

  /// <summary>The security setting that gateway mode refuses <c>None</c> for, unless allowed.</summary>
  public const string AuthenticationModeKey = "ReportsServer:Authentication:Mode";

  /// <summary>
  /// The tenant ID readiness looks up to see whether the record store answers. Reserved: no caller
  /// and no configured renderer may name it, so the gateway never routes a conversion to it.
  /// </summary>
  public const string ReadinessProbeTenantId = "readiness-probe";

  /// <summary>Product-tenant membership per authenticated caller ID.</summary>
  public GatewayCallerTenants[] Tenants { get; set; } = [];

  /// <summary>
  /// The header a caller with several tenants names one in. Only a selector: it is checked against
  /// the caller's membership and never names a tenant the caller does not belong to.
  /// </summary>
  public string TenantHeader { get; set; } = "X-Reports-Tenant";

  public GatewayRecordsOptions Records { get; set; } = new();

  public GatewayWakeOptions Wake { get; set; } = new();

  /// <summary>
  /// The deadline for one forwarded conversion, from the record lookup to the PDF's last byte,
  /// waking the renderer included.
  /// </summary>
  public TimeSpan RendererTimeout { get; set; } = TimeSpan.FromSeconds(90);

  /// <summary>The largest PDF the gateway relays; a larger one aborts the response.</summary>
  public long MaxPdfBytes { get; set; } = 256 * 1024 * 1024;

  /// <summary>Conversions in flight per tenant in this instance, across all of its callers.</summary>
  public int MaxConcurrentRequestsPerTenant { get; set; } = 8;

  /// <summary>
  /// How many gateway replicas send conversions to the same renderers. Each replica admits a tenant
  /// only its share of the requests the tenant's renderer admits, so that together they do not
  /// send it more; set it to the most replicas that run at once.
  /// </summary>
  public int Replicas { get; set; } = 1;

  /// <summary>Allows <c>http</c> renderer URLs. For tests and development only.</summary>
  public bool AllowHttpRenderers { get; set; }

  /// <summary>
  /// Allows <c>ReportsServer:Authentication:Mode=None</c>, under which every caller is
  /// <c>anonymous</c> and converts for that caller ID's tenants. For tests and development only;
  /// without it gateway mode refuses to start with no authentication.
  /// </summary>
  public bool AllowAnonymousCallers { get; set; }

  /// <summary>
  /// Reads <c>ReportsServer:Mode</c>. Missing or empty is <see cref="ReportsServerMode.Integrated"/>.
  /// </summary>
  public static ReportsServerMode ReadMode(IConfiguration configuration) =>
    configuration[ModeKey] switch
    {
      null or "" or "Integrated" => ReportsServerMode.Integrated,
      "Gateway" => ReportsServerMode.Gateway,
      _ => throw new InvalidOperationException(
        "ReportsServer:Mode must be Integrated (the default) or Gateway."
      ),
    };

  /// <summary>Throws <see cref="InvalidOperationException"/> naming the first invalid setting.</summary>
  public void Validate()
  {
    ValidateTenants();

    if (
      TenantHeader.Length is < 1 or > 64
      || TenantHeader.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
      || TenantHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
      || TenantHeader.Equals("X-Reports-Api-Key", StringComparison.OrdinalIgnoreCase)
    )
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:TenantHeader must be a header name of letters, digits, hyphens, and underscores, and not a credential header."
      );
    }

    Records.Validate(AllowHttpRenderers);
    Wake.Validate();

    if (RendererTimeout <= TimeSpan.Zero || RendererTimeout > TimeSpan.FromHours(24))
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:RendererTimeout must be positive and at most 24 hours."
      );
    }

    if (MaxPdfBytes is < 1024 or > 64L * 1024 * 1024 * 1024)
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:MaxPdfBytes must be between 1024 bytes and 64 GiB."
      );
    }

    if (MaxConcurrentRequestsPerTenant is < 1 or > 10_000)
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:MaxConcurrentRequestsPerTenant must be between 1 and 10000."
      );
    }

    if (Replicas is < 1 or > 1000)
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Replicas must be between 1 and 1000."
      );
    }
  }

  /// <summary>
  /// A tenant's in-flight limit in this replica when its renderer admits <paramref name="admitted"/>
  /// requests at once (<see langword="null"/> when unknown): this replica's share of them, rounded
  /// down so that the replicas' shares add up to no more, but at least 1, and never above
  /// <see cref="MaxConcurrentRequestsPerTenant"/>.
  /// </summary>
  public int TenantLimit(int? admitted) =>
    admitted is { } requests
      ? Math.Min(MaxConcurrentRequestsPerTenant, Math.Max(1, requests / Replicas))
      : MaxConcurrentRequestsPerTenant;

  /// <summary>
  /// Whether <paramref name="url"/> is a renderer base address the gateway sends a credential to:
  /// absolute HTTPS (or HTTP when allowed), with no user information, query, or fragment, since the
  /// gateway appends <c>/convert</c> to it.
  /// </summary>
  public static bool IsRendererUrl(Uri? url, bool allowHttp) =>
    url is { IsAbsoluteUri: true }
    && (url.Scheme == Uri.UriSchemeHttps || (allowHttp && url.Scheme == Uri.UriSchemeHttp))
    && string.IsNullOrEmpty(url.UserInfo)
    && string.IsNullOrEmpty(url.Query)
    && string.IsNullOrEmpty(url.Fragment);

  /// <summary>
  /// Whether <paramref name="apiKey"/> can travel in a header: non-blank, at most 1024 characters
  /// (the server's own limit), and printable ASCII without spaces.
  /// </summary>
  public static bool IsApiKey(string? apiKey) =>
    apiKey is { Length: > 0 and <= 1024 } && apiKey.All(c => c is > ' ' and < '\u007f');

  private void ValidateTenants()
  {
    if (Tenants.Length == 0)
    {
      throw new InvalidOperationException(
        "Gateway mode needs ReportsServer:Gateway:Tenants: each authenticated CallerId and the product tenants it belongs to."
      );
    }

    HashSet<string> callers = new(StringComparer.Ordinal);
    foreach (var membership in Tenants)
    {
      if (
        string.IsNullOrWhiteSpace(membership.CallerId)
        || membership.CallerId.Length > 256
        || membership.CallerId.Any(char.IsControl)
        || !callers.Add(membership.CallerId)
      )
      {
        throw new InvalidOperationException(
          "ReportsServer:Gateway:Tenants entries need unique, non-empty CallerId values."
        );
      }

      HashSet<string> tenants = new(StringComparer.Ordinal);
      if (
        membership.Tenants.Length == 0
        || !membership.Tenants.All(tenant => TenantId.IsValid(tenant) && tenants.Add(tenant))
      )
      {
        throw new InvalidOperationException(
          $"ReportsServer:Gateway:Tenants for caller '{membership.CallerId}' must list distinct tenant IDs: 1 to 63 lowercase letters, digits, and hyphens, starting with a letter or digit."
        );
      }

      if (tenants.Contains(ReadinessProbeTenantId))
      {
        throw new InvalidOperationException(
          $"ReportsServer:Gateway:Tenants for caller '{membership.CallerId}' names the tenant ID '{ReadinessProbeTenantId}', which readiness reserves."
        );
      }
    }
  }
}

/// <summary>One caller's product tenants.</summary>
internal sealed class GatewayCallerTenants
{
  /// <summary>The authenticated caller ID: an API key's <c>CallerId</c>, or the JWT caller claim.</summary>
  public string CallerId { get; set; } = "";

  public string[] Tenants { get; set; } = [];
}

/// <summary>
/// Where renderer records come from: the fields of <see cref="RendererRecordStoreOptions"/>, plus
/// <c>Configuration</c>, a read-only store of the <see cref="Renderers"/> listed here.
/// </summary>
internal sealed class GatewayRecordsOptions
{
  public const string ConfigurationStore = "Configuration";

  /// <summary><c>Configuration</c>, <c>File</c>, or <c>KeyVault</c>.</summary>
  public string Store { get; set; } = "";

  /// <summary>For <c>File</c>: the directory of record files.</summary>
  public string Path { get; set; } = "";

  /// <summary>For <c>KeyVault</c>: the vault's address.</summary>
  public Uri? VaultUri { get; set; }

  /// <summary>For <c>KeyVault</c>: a user-assigned managed identity's client ID; empty for the default chain.</summary>
  public string ManagedIdentityClientId { get; set; } = "";

  /// <summary>For <c>Configuration</c>: the renderers, one per tenant.</summary>
  public GatewayRendererEntry[] Renderers { get; set; } = [];

  /// <summary>How long a record lookup is reused before the store is asked again.</summary>
  public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

  public RendererRecordStoreOptions ToStoreOptions() =>
    new()
    {
      Store = Store,
      Path = Path,
      VaultUri = VaultUri,
      ManagedIdentityClientId = ManagedIdentityClientId,
    };

  public void Validate(bool allowHttpRenderers)
  {
    switch (Store)
    {
      case ConfigurationStore:
        ValidateRenderers(allowHttpRenderers);
        break;
      case "File" when string.IsNullOrWhiteSpace(Path):
        throw new InvalidOperationException(
          "ReportsServer:Gateway:Records:Store=File needs Records:Path, the directory of record files."
        );
      case "KeyVault"
        when VaultUri is not { IsAbsoluteUri: true } || VaultUri.Scheme != Uri.UriSchemeHttps:
        throw new InvalidOperationException(
          "ReportsServer:Gateway:Records:Store=KeyVault needs Records:VaultUri, the vault's https address."
        );
      case "File" or "KeyVault":
        break;
      default:
        throw new InvalidOperationException(
          "ReportsServer:Gateway:Records:Store must be Configuration, File, or KeyVault."
        );
    }

    if (Store != ConfigurationStore && Renderers.Length > 0)
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Records:Renderers applies only to Records:Store=Configuration."
      );
    }

    if (CacheDuration < TimeSpan.Zero || CacheDuration > TimeSpan.FromHours(1))
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Records:CacheDuration must be between zero and one hour."
      );
    }
  }

  private void ValidateRenderers(bool allowHttpRenderers)
  {
    if (Renderers.Length == 0)
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Records:Store=Configuration needs at least one entry in Records:Renderers."
      );
    }

    HashSet<string> tenants = new(StringComparer.Ordinal);
    foreach (var renderer in Renderers)
    {
      if (
        !TenantId.IsValid(renderer.TenantId)
        || renderer.TenantId == GatewayOptions.ReadinessProbeTenantId
        || !tenants.Add(renderer.TenantId)
      )
      {
        throw new InvalidOperationException(
          $"ReportsServer:Gateway:Records:Renderers need distinct, valid TenantId values other than '{GatewayOptions.ReadinessProbeTenantId}'."
        );
      }

      if (
        !Uri.TryCreate(renderer.Url, UriKind.Absolute, out var url)
        || !GatewayOptions.IsRendererUrl(url, allowHttpRenderers)
      )
      {
        throw new InvalidOperationException(
          $"The renderer Url for tenant '{renderer.TenantId}' must be an absolute https address without credentials, query, or fragment"
            + (allowHttpRenderers ? " (http is allowed)." : "; http needs AllowHttpRenderers=true.")
        );
      }

      if (!GatewayOptions.IsApiKey(renderer.ApiKey))
      {
        throw new InvalidOperationException(
          $"The renderer for tenant '{renderer.TenantId}' needs its ApiKey: printable ASCII without spaces, at most 1024 characters."
        );
      }

      if (
        renderer.SandboxId is not null
        && (
          string.IsNullOrWhiteSpace(renderer.SandboxId)
          || renderer.SandboxId.Length > 128
          || renderer.SandboxId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        )
      )
      {
        throw new InvalidOperationException(
          $"The renderer SandboxId for tenant '{renderer.TenantId}' must be letters, digits, hyphens, and underscores."
        );
      }

      if (renderer.MaxConcurrentRequests is < 1)
      {
        throw new InvalidOperationException(
          $"The renderer MaxConcurrentRequests for tenant '{renderer.TenantId}' must be at least 1."
        );
      }
    }
  }
}

/// <summary>A renderer listed in configuration; becomes a <see cref="RendererRecord"/>.</summary>
internal sealed class GatewayRendererEntry
{
  public string TenantId { get; set; } = "";

  public string Url { get; set; } = "";

  public string ApiKey { get; set; } = "";

  public string? SandboxId { get; set; }

  /// <summary>The requests the renderer admits at once, which caps the tenant's in-flight limit.</summary>
  public int? MaxConcurrentRequests { get; set; }

  public RendererRecord ToRecord() =>
    new()
    {
      TenantId = TenantId,
      Url = new Uri(Url, UriKind.Absolute),
      ApiKey = ApiKey,
      SandboxId = SandboxId,
      MaxConcurrentRequests = MaxConcurrentRequests,
    };
}

/// <summary>Waking suspended renderers before forwarding to them.</summary>
internal sealed class GatewayWakeOptions
{
  public const string SandboxesMode = "Sandboxes";

  /// <summary><c>None</c>, or <c>Sandboxes</c> to resume suspended Azure Container Apps sandboxes.</summary>
  public string Mode { get; set; } = "None";

  /// <summary>For <c>Sandboxes</c>: the sandbox group the renderers run in.</summary>
  public SandboxesOptions Sandboxes { get; set; } = new();

  /// <summary>How long one request keeps resuming and retrying a renderer that is not running.</summary>
  public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

  public bool Enabled => Mode == SandboxesMode;

  public void Validate()
  {
    if (Mode is not ("None" or SandboxesMode))
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Wake:Mode must be None (the default) or Sandboxes."
      );
    }

    if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10))
    {
      throw new InvalidOperationException(
        "ReportsServer:Gateway:Wake:Timeout must be positive and at most 10 minutes."
      );
    }

    if (Enabled)
    {
      try
      {
        Sandboxes.Validate();
      }
      catch (InvalidOperationException exception)
      {
        throw new InvalidOperationException(
          $"ReportsServer:Gateway:Wake:Sandboxes is incomplete. {exception.Message}",
          exception
        );
      }
    }
  }
}
