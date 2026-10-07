namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The shared reports API that authenticates callers and routes conversions to tenant renderers.
/// This container runs no browser. References supply the endpoint and configured client credential.
/// </summary>
/// <param name="name">The resource and connection string name.</param>
public sealed class ReportsGatewayResource([ResourceName] string name)
  : ContainerResource(name),
    IResourceWithConnectionString
{
  internal ParameterResource? ApiKeyParameter { get; set; }

  internal Dictionary<string, List<string>> TenantPrefixes { get; } = new(StringComparer.Ordinal);

  /// <summary>The gateway's HTTP endpoint.</summary>
  public EndpointReference PrimaryEndpoint => field ??= new(this, "http");

  /// <summary>The endpoint and optional secret client credential read by AddReportsClient.</summary>
  public ReferenceExpression ConnectionStringExpression =>
    ApiKeyParameter is { } key
      ? ReferenceExpression.Create($"Endpoint={PrimaryEndpoint};ApiKey={key}")
      : ReferenceExpression.Create($"Endpoint={PrimaryEndpoint}");

  IEnumerable<
    KeyValuePair<string, ReferenceExpression>
  > IResourceWithConnectionString.GetConnectionProperties()
  {
    yield return new(
      "Host",
      ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Host)}")
    );
    yield return new(
      "Port",
      ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Port)}")
    );
    yield return new("Uri", ReferenceExpression.Create($"{PrimaryEndpoint}"));
    if (ApiKeyParameter is { } key)
    {
      yield return new("ApiKey", ReferenceExpression.Create($"{key}"));
    }
  }
}
