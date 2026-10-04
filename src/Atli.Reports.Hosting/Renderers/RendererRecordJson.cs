using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atli.Reports.Hosting.Renderers;

/// <summary>
/// How stores write and read a <see cref="RendererRecord"/>: camelCase JSON, the URL as its absolute
/// URI string. Both stores check what they read, so a damaged or misplaced record fails loudly
/// instead of routing a tenant somewhere else.
/// </summary>
internal static class RendererRecordJson
{
  public static string Serialize(RendererRecord record) =>
    JsonSerializer.Serialize(Validate(record), RendererRecordJsonContext.Default.RendererRecord);

  public static Task SerializeAsync(
    Stream stream,
    RendererRecord record,
    CancellationToken cancellationToken
  ) =>
    JsonSerializer.SerializeAsync(
      stream,
      Validate(record),
      RendererRecordJsonContext.Default.RendererRecord,
      cancellationToken
    );

  /// <summary>
  /// Reads the record stored for <paramref name="tenantId"/> under <paramref name="source"/> (a file
  /// or secret name, for the error message).
  /// </summary>
  public static RendererRecord Deserialize(string json, string tenantId, string source)
  {
    RendererRecord? record;
    try
    {
      record = JsonSerializer.Deserialize(json, RendererRecordJsonContext.Default.RendererRecord);
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"{source} does not hold a renderer record.", exception);
    }

    return Check(record, tenantId, source);
  }

  /// <inheritdoc cref="Deserialize(string, string, string)"/>
  public static async Task<RendererRecord> DeserializeAsync(
    Stream stream,
    string tenantId,
    string source,
    CancellationToken cancellationToken
  )
  {
    RendererRecord? record;
    try
    {
      record = await JsonSerializer.DeserializeAsync(
        stream,
        RendererRecordJsonContext.Default.RendererRecord,
        cancellationToken
      );
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"{source} does not hold a renderer record.", exception);
    }

    return Check(record, tenantId, source);
  }

  /// <summary>Throws <see cref="ArgumentException"/> unless the record can be stored.</summary>
  public static RendererRecord Validate(RendererRecord record)
  {
    ArgumentNullException.ThrowIfNull(record);
    TenantId.Validate(record.TenantId, nameof(record));
    if (record.Url is null || !record.Url.IsAbsoluteUri)
    {
      throw new ArgumentException("A renderer record's URL is absolute.", nameof(record));
    }

    if (string.IsNullOrWhiteSpace(record.ApiKey))
    {
      throw new ArgumentException("A renderer record has an API key.", nameof(record));
    }

    return record;
  }

  private static RendererRecord Check(RendererRecord? record, string tenantId, string source)
  {
    if (record is null || record.Url is null || !record.Url.IsAbsoluteUri)
    {
      throw new InvalidDataException($"{source} does not hold a renderer record.");
    }

    // The gateway routes a tenant to the record it gets for that tenant; one that names another
    // tenant is never served.
    if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal))
    {
      throw new InvalidDataException($"{source} holds the record of another tenant.");
    }

    return record;
  }
}

[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(RendererRecord))]
internal sealed partial class RendererRecordJsonContext : JsonSerializerContext;
