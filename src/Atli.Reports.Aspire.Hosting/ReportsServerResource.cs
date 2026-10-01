namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An Atli Reports server: the container that converts HTML to PDF over HTTP
/// (<c>POST /convert</c>). Add one with
/// <see cref="ReportsServerBuilderExtensions.AddReportsServer(IDistributedApplicationBuilder, string, int?)"/>.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <remarks>
/// <para>
/// The connection string is <c>Endpoint=&lt;url&gt;</c>, the URL of the server's HTTP endpoint, which
/// is the form <c>AddReportsClient</c> in <c>Atli.Reports.Client</c> reads. <c>WithReference</c>
/// passes it to a resource as <c>ConnectionStrings__{name}</c>.
/// </para>
/// <para>
/// <c>WithReference</c> also passes the connection properties <c>Host</c>, <c>Port</c>, and <c>Uri</c>
/// as <c>{NAME}_HOST</c>, <c>{NAME}_PORT</c>, and <c>{NAME}_URI</c>, for apps that do not read .NET
/// connection strings.
/// </para>
/// </remarks>
public sealed class ReportsServerResource([ResourceName] string name)
  : ContainerResource(name),
    IResourceWithConnectionString
{
  /// <summary>
  /// The name of the server's HTTP endpoint.
  /// </summary>
  internal const string HttpEndpointName = "http";

  /// <summary>
  /// The server's HTTP endpoint, which serves <c>/convert</c> and the <c>/health</c> probes.
  /// </summary>
  public EndpointReference PrimaryEndpoint => field ??= new(this, HttpEndpointName);

  /// <summary>
  /// The host of <see cref="PrimaryEndpoint"/>.
  /// </summary>
  public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

  /// <summary>
  /// The port of <see cref="PrimaryEndpoint"/>.
  /// </summary>
  public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

  /// <summary>
  /// The URL of <see cref="PrimaryEndpoint"/>, for example <c>http://localhost:5123</c>.
  /// </summary>
  public ReferenceExpression UriExpression =>
    ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Url)}");

  /// <summary>
  /// The connection string: <c>Endpoint=</c> followed by the URL of <see cref="PrimaryEndpoint"/>.
  /// </summary>
  public ReferenceExpression ConnectionStringExpression =>
    ReferenceExpression.Create($"Endpoint={PrimaryEndpoint.Property(EndpointProperty.Url)}");

  IEnumerable<
    KeyValuePair<string, ReferenceExpression>
  > IResourceWithConnectionString.GetConnectionProperties()
  {
    yield return new("Host", ReferenceExpression.Create($"{Host}"));
    yield return new("Port", ReferenceExpression.Create($"{Port}"));
    yield return new("Uri", UriExpression);
  }
}
