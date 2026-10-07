using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Builds the gateway and provisioning service for isolated tenant renderers.</summary>
public static class ReportsHostedBuilderExtensions
{
  private const string AuthenticationPrefix = "ReportsServer__Authentication__";
  private const string GatewayPrefix = "ReportsServer__Gateway__";

  /// <summary>
  /// Adds a browser-free reports gateway with readiness, liveness, and OTLP export. Configure
  /// authentication, tenant membership, and a renderer record store before starting it.
  /// </summary>
  /// <param name="builder">The application builder.</param>
  /// <param name="name">The resource and connection string name.</param>
  /// <param name="port">The optional host HTTP port.</param>
  public static IResourceBuilder<ReportsGatewayResource> AddReportsGateway(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    int? port = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrEmpty(name);
    return builder
      .AddResource(new ReportsGatewayResource(name))
      .WithImage(ReportsServerContainerImageTags.Image, ReportsServerContainerImageTags.Tag)
      .WithImageRegistry(ReportsServerContainerImageTags.Registry)
      .WithEnvironment("ReportsServer__Mode", "Gateway")
      .WithHttpEndpoint(port: port, targetPort: 8080, name: "http")
      .WithHttpHealthCheck("/health/ready", endpointName: "http")
      .WithOtlpExporter()
      .WithHealthProbes();
  }

  /// <summary>
  /// Adds the internal provisioning service, whose image starts in <c>serve</c> mode. Configure
  /// its Azure resources, credentials, allowed source CIDRs, and tenant quotas before starting it.
  /// Keep a single replica: its create locks and rate limits are process-local.
  /// </summary>
  /// <param name="builder">The application builder.</param>
  /// <param name="name">The resource name.</param>
  /// <param name="port">The optional host HTTP port.</param>
  public static IResourceBuilder<ReportsProvisionerResource> AddReportsProvisioner(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    int? port = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrEmpty(name);
    return builder
      .AddResource(new ReportsProvisionerResource(name))
      .WithImage("atlitech/reports-provisioner", ReportsServerContainerImageTags.Tag)
      .WithImageRegistry(ReportsServerContainerImageTags.Registry)
      .WithHttpEndpoint(port: port, targetPort: 8080, name: "http")
      .WithHttpHealthCheck("/health/ready", endpointName: "http")
      .WithHealthProbes();
  }

  /// <summary>
  /// Authenticates one client application. Only its verifier enters the gateway environment;
  /// references supply the full credential to clients. Additional callers can use WithEnvironment.
  /// </summary>
  /// <param name="builder">The gateway.</param>
  /// <param name="keyId">The identifier before the dot in the full credential.</param>
  /// <param name="apiKey">A secret parameter holding the full credential.</param>
  /// <param name="apiKeyHash">A secret parameter holding base64 SHA-256 of the full credential.</param>
  /// <param name="callerId">The client identity; defaults to the key identifier.</param>
  /// <param name="allowTenantManagement">Also grants the tenant deletion permission.</param>
  public static IResourceBuilder<ReportsGatewayResource> WithApiKeyAuthentication(
    this IResourceBuilder<ReportsGatewayResource> builder,
    string keyId,
    IResourceBuilder<ParameterResource> apiKey,
    IResourceBuilder<ParameterResource> apiKeyHash,
    string? callerId = null,
    bool allowTenantManagement = false
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ValidateKeyId(keyId);
    ValidateSecret(apiKey);
    ValidateSecret(apiKeyHash);
    if (callerId is not null)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
    }

    builder.Resource.ApiKeyParameter = apiKey.Resource;
    builder
      .WithEnvironment(AuthenticationPrefix + "Mode", "ApiKey")
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Id", keyId)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Hash", apiKeyHash)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__CallerId", callerId ?? keyId)
      .WithEnvironment(AuthenticationPrefix + "ApiKeys__0__Permissions__0", "reports.convert")
      .WithEnvironment(context =>
      {
        var name = AuthenticationPrefix + "ApiKeys__0__Permissions__1";
        if (allowTenantManagement)
        {
          context.EnvironmentVariables[name] = "reports.tenants";
        }
        else
        {
          context.EnvironmentVariables.Remove(name);
        }
      });
    return builder;
  }

  /// <summary>Configures the verifier for the gateway's credential on the internal service.</summary>
  /// <param name="builder">The provisioning service.</param>
  /// <param name="keyId">The identifier before the dot in the full credential.</param>
  /// <param name="apiKeyHash">A secret parameter holding base64 SHA-256 of the full credential.</param>
  public static IResourceBuilder<ReportsProvisionerResource> WithApiKeyAuthentication(
    this IResourceBuilder<ReportsProvisionerResource> builder,
    string keyId,
    IResourceBuilder<ParameterResource> apiKeyHash
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ValidateKeyId(keyId);
    ValidateSecret(apiKeyHash);
    return builder
      .WithEnvironment("Provisioner__Service__ApiKeys__0__Id", keyId)
      .WithEnvironment("Provisioner__Service__ApiKeys__0__Hash", apiKeyHash);
  }

  /// <summary>
  /// Connects a gateway to its on-demand provisioning service and waits for its readiness.
  /// Run mode allows the local HTTP endpoint. Publishing uses HTTPS unless an explicit endpoint
  /// expression is supplied; the deployment must terminate TLS for the provisioning service.
  /// </summary>
  /// <param name="builder">The gateway.</param>
  /// <param name="provisioner">The provisioning service.</param>
  /// <param name="apiKey">A secret parameter holding the gateway's full service credential.</param>
  /// <param name="endpoint">An optional deployment-specific provisioning URL expression.</param>
  public static IResourceBuilder<ReportsGatewayResource> WithProvisioner(
    this IResourceBuilder<ReportsGatewayResource> builder,
    IResourceBuilder<ReportsProvisionerResource> provisioner,
    IResourceBuilder<ParameterResource> apiKey,
    ReferenceExpression? endpoint = null
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(provisioner);
    ValidateSecret(apiKey);
    var runMode = builder.ApplicationBuilder.ExecutionContext.IsRunMode;
    endpoint ??= runMode
      ? ReferenceExpression.Create($"{provisioner.Resource.PrimaryEndpoint}")
      : ReferenceExpression.Create(
        $"https://{provisioner.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}"
      );
    builder
      .WithEnvironment(GatewayPrefix + "Provisioning__Mode", "OnDemand")
      .WithEnvironment(GatewayPrefix + "Provisioning__Url", endpoint)
      .WithEnvironment(GatewayPrefix + "Provisioning__ApiKey", apiKey)
      .WaitFor(provisioner);
    if (runMode)
    {
      builder.WithEnvironment(GatewayPrefix + "AllowHttpRenderers", "true");
    }
    return builder;
  }

  /// <summary>Grants a caller membership of tenant IDs beginning with the supplied prefix.</summary>
  /// <param name="builder">The gateway.</param>
  /// <param name="callerId">The authenticated client identity.</param>
  /// <param name="prefix">A tenant prefix, such as <c>myapp-</c>.</param>
  public static IResourceBuilder<ReportsGatewayResource> WithTenantPrefix(
    this IResourceBuilder<ReportsGatewayResource> builder,
    string callerId,
    string prefix
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
    ValidatePrefix(prefix);
    ValidateNoOverlap(
      prefix,
      builder.Resource.TenantPrefixes.Values.SelectMany(prefixes => prefixes)
    );
    if (!builder.Resource.TenantPrefixes.TryGetValue(callerId, out var prefixes))
    {
      prefixes = [];
      builder.Resource.TenantPrefixes.Add(callerId, prefixes);
    }
    var callerIndex = builder.Resource.TenantPrefixes.Keys.ToList().IndexOf(callerId);
    var prefixIndex = prefixes.Count;
    prefixes.Add(prefix);
    var entry = GatewayPrefix + $"Tenants__{callerIndex}__";
    return builder
      .WithEnvironment(entry + "CallerId", callerId)
      .WithEnvironment(entry + $"TenantPrefixes__{prefixIndex}", prefix);
  }

  /// <summary>Configures an on-demand tenant namespace and its resource creation quotas.</summary>
  /// <param name="builder">The provisioning service.</param>
  /// <param name="prefix">A tenant prefix, such as <c>myapp-</c>.</param>
  /// <param name="maxTenants">The most renderer tenants under this prefix, from 1 to 100000.</param>
  /// <param name="maxCreatesPerMinute">The per-prefix create budget, from 1 to 10000.</param>
  public static IResourceBuilder<ReportsProvisionerResource> WithTenantPrefix(
    this IResourceBuilder<ReportsProvisionerResource> builder,
    string prefix,
    int maxTenants = 1000,
    int maxCreatesPerMinute = 20
  )
  {
    ArgumentNullException.ThrowIfNull(builder);
    ValidatePrefix(prefix);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxTenants, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTenants, 100000);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxCreatesPerMinute, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCreatesPerMinute, 10000);
    ValidateNoOverlap(prefix, builder.Resource.TenantPrefixes);
    var index = builder.Resource.TenantPrefixes.Count;
    builder.Resource.TenantPrefixes.Add(prefix);
    var entry = $"Provisioner__Service__TenantPrefixes__{index}__";
    return builder
      .WithEnvironment(entry + "Prefix", prefix)
      .WithEnvironment(entry + "MaxTenants", maxTenants.ToString(CultureInfo.InvariantCulture))
      .WithEnvironment(
        entry + "MaxCreatesPerMinute",
        maxCreatesPerMinute.ToString(CultureInfo.InvariantCulture)
      );
  }

  private static void ValidateSecret(IResourceBuilder<ParameterResource> parameter)
  {
    ArgumentNullException.ThrowIfNull(parameter);
    if (!parameter.Resource.Secret)
    {
      throw new ArgumentException(
        "Credential and verifier parameters must be marked secret.",
        nameof(parameter)
      );
    }
  }

  private static void ValidateKeyId(string keyId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
    if (
      keyId.Length > 64
      || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
    )
    {
      throw new ArgumentException(
        "Use up to 64 ASCII letters, digits, hyphens, or underscores for the key identifier.",
        nameof(keyId)
      );
    }
  }

  private static void ValidatePrefix(string prefix)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
    if (
      prefix.Length is < 2 or > 27
      || !char.IsAsciiLetterOrDigit(prefix[0])
      || prefix[^1] != '-'
      || prefix.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
      || "readiness-probe".StartsWith(prefix, StringComparison.Ordinal)
    )
    {
      throw new ArgumentException(
        "Use 2 to 27 lowercase letters, digits, or hyphens, starting with a letter or digit and ending in a hyphen; the readiness-probe tenant is reserved.",
        nameof(prefix)
      );
    }
  }

  private static void ValidateNoOverlap(string prefix, IEnumerable<string> existing)
  {
    if (
      existing.Any(other =>
        other.StartsWith(prefix, StringComparison.Ordinal)
        || prefix.StartsWith(other, StringComparison.Ordinal)
      )
    )
    {
      throw new ArgumentException("Tenant prefixes must not overlap.", nameof(prefix));
    }
  }
}
