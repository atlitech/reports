using System.Reflection;
using System.Text.Json.Nodes;
using Atli.Reports.Engine;
using Atli.Reports.Server.Models;
using Atli.Reports.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Atli.Reports.Server.OpenApi;

/// <summary>
/// The server's OpenAPI document, its public contract, served at <c>/openapi/v1.json</c>.
/// </summary>
/// <remarks>
/// The descriptions are the XML comments of the request models and of the <c>/convert</c> handler,
/// which the OpenAPI source generator compiles into the binary. The transformers here add what XML
/// comments cannot say.
/// </remarks>
internal static class ServerOpenApi
{
  /// <summary>
  /// The values of the options with a fixed vocabulary, keyed by their JSON property name: the names
  /// <c>ConvertEndpoints</c> maps, case-insensitively, onto the engine's values.
  /// </summary>
  private static readonly Dictionary<string, string[]> OptionValues = new(StringComparer.Ordinal)
  {
    ["orientation"] = ["portrait", "landscape"],
    ["paperSize"] = ["letter", "legal", "a4", "a3"],
  };

  /// <summary>
  /// Registers the document.
  /// </summary>
  public static IServiceCollection AddServerOpenApi(this IServiceCollection services) =>
    services.AddOpenApi(options =>
    {
      options.AddDocumentTransformer(DescribeServer);
      options.AddDocumentTransformer(DescribeAuthentication);
      options.AddSchemaTransformer(DescribeProblemKind);
      options.AddSchemaTransformer(DescribeOptionValues);
    });

  /// <summary>
  /// Titles the document after the server and versions it with the server's release.
  /// </summary>
  private static Task DescribeServer(
    OpenApiDocument document,
    OpenApiDocumentTransformerContext context,
    CancellationToken cancellationToken
  )
  {
    var assembly = typeof(ServerOpenApi).Assembly;
    document.Info.Title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title;
    // The informational version without its build metadata (+commit), so 0.26.0 for that release.
    document.Info.Version = assembly
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
      .InformationalVersion.Split('+')[0];
    document.Info.Description =
      "Converts HTML to PDF in a headless browser. Errors are RFC 9457 problem details with a `kind` member.";
    return Task.CompletedTask;
  }

  private static Task DescribeAuthentication(
    OpenApiDocument document,
    OpenApiDocumentTransformerContext context,
    CancellationToken cancellationToken
  )
  {
    var authentication = context
      .ApplicationServices.GetRequiredService<ReportsSecurityOptions>()
      .Authentication;
    if (authentication.Mode == "None")
    {
      return Task.CompletedTask;
    }

    const string schemeName = "ReportsAuthentication";
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes[schemeName] =
      authentication.Mode == "ApiKey"
        ? new OpenApiSecurityScheme
        {
          Type = SecuritySchemeType.ApiKey,
          In = ParameterLocation.Header,
          Name = ApiKeyAuthenticationHandler.HeaderName,
          Description =
            "A scoped API credential in the form key-id.random-secret. Converting requires the reports.convert permission; deleting a tenant, reports.tenants.",
        }
        : new OpenApiSecurityScheme
        {
          Type = SecuritySchemeType.Http,
          Scheme = "bearer",
          BearerFormat = "JWT",
          Description =
            "An access token from the configured trusted issuer, for this API's audience and with conversion permission.",
        };
    document.Security =
    [
      new OpenApiSecurityRequirement
      {
        [new OpenApiSecuritySchemeReference(schemeName, document)] = [],
      },
    ];
    return Task.CompletedTask;
  }

  /// <summary>
  /// Adds the <c>kind</c> member that every problem the server writes carries: the name of the
  /// <see cref="ConversionErrorKind"/>.
  /// </summary>
  private static Task DescribeProblemKind(
    OpenApiSchema schema,
    OpenApiSchemaTransformerContext context,
    CancellationToken cancellationToken
  )
  {
    if (context.JsonTypeInfo.Type == typeof(ProblemDetails))
    {
      schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
      schema.Properties["kind"] = new OpenApiSchema
      {
        Type = JsonSchemaType.String,
        Description =
          "The kind of error (a `ConversionErrorKind`), which tells whether retrying can help.",
        Enum = [.. Enum.GetNames<ConversionErrorKind>().Select(name => JsonValue.Create(name)!)],
      };
      schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
      schema.Required.Add("kind");
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Lists the values of the options with a fixed vocabulary.
  /// </summary>
  private static Task DescribeOptionValues(
    OpenApiSchema schema,
    OpenApiSchemaTransformerContext context,
    CancellationToken cancellationToken
  )
  {
    if (
      context.JsonPropertyInfo is { } property
      && property.DeclaringType == typeof(PdfOptionsRequest)
      && OptionValues.TryGetValue(property.Name, out var values)
    )
    {
      // Null, like an omitted option, keeps the default; System.Text.Json describes a nullable enum
      // the same way.
      schema.Enum = [.. values.Select(value => JsonValue.Create(value)!), null!];
    }

    return Task.CompletedTask;
  }
}
