using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Atli.Reports.Engine;

namespace Atli.Reports.Client.Http;

/// <summary>
/// The JSON body of the server's <c>POST /convert</c>.
/// </summary>
internal sealed class ConvertRequestBody
{
  public required string Html { get; init; }

  public required PdfOptionsBody Options { get; init; }

  /// <summary>
  /// Builds the request for <paramref name="html"/>, spelling out every option so the server's own
  /// defaults never decide the outcome.
  /// </summary>
  public static ConvertRequestBody Create(string html, PdfOptions? options)
  {
    options ??= new PdfOptions();
    var paperSizeName = PaperSizeName(options.PaperSize);
    var margins = options.Margins;

    return new ConvertRequestBody
    {
      Html = html,
      Options = new PdfOptionsBody
      {
        Orientation = options.Orientation == PageOrientation.Landscape ? "landscape" : "portrait",
        PaperSize = paperSizeName,
        PaperWidth = paperSizeName is null ? options.PaperSize?.Width : null,
        PaperHeight = paperSizeName is null ? options.PaperSize?.Height : null,
        Margins = margins is null
          ? null
          : new MarginsBody
          {
            Top = margins.Top,
            Bottom = margins.Bottom,
            Left = margins.Left,
            Right = margins.Right,
          },
        PrintBackground = options.PrintBackground,
        Scale = options.Scale,
        HeaderTemplate = options.HeaderTemplate,
        FooterTemplate = options.FooterTemplate,
        DisplayHeaderFooter = options.DisplayHeaderFooter,
        PageRanges = options.PageRanges,
        PreferCssPageSize = options.PreferCssPageSize,
        GenerateTaggedPdf = options.GenerateTaggedPdf,
        WaitForSignal = options.WaitForSignal,
        // Timeout.InfiniteTimeSpan is -1 ms, so it travels as -0.001 and the server reads it back
        // as the same infinite wait.
        WaitTimeoutSeconds = options.WaitTimeout.TotalSeconds,
      },
    };
  }

  /// <summary>
  /// The server's name for <paramref name="paperSize"/>, or <see langword="null"/> when it is a
  /// custom size the request must spell out in inches.
  /// </summary>
  private static string? PaperSizeName(PaperSize? paperSize)
  {
    if (paperSize == PaperSize.Letter)
    {
      return "letter";
    }

    if (paperSize == PaperSize.Legal)
    {
      return "legal";
    }

    if (paperSize == PaperSize.A4)
    {
      return "a4";
    }

    return paperSize == PaperSize.A3 ? "a3" : null;
  }
}

internal sealed class PdfOptionsBody
{
  public required string Orientation { get; init; }

  public string? PaperSize { get; init; }

  public double? PaperWidth { get; init; }

  public double? PaperHeight { get; init; }

  public MarginsBody? Margins { get; init; }

  public bool PrintBackground { get; init; }

  public double Scale { get; init; }

  public string? HeaderTemplate { get; init; }

  public string? FooterTemplate { get; init; }

  public bool DisplayHeaderFooter { get; init; }

  public string? PageRanges { get; init; }

  [JsonPropertyName("preferCSSPageSize")]
  public bool PreferCssPageSize { get; init; }

  public bool GenerateTaggedPdf { get; init; } = true;

  public string? WaitForSignal { get; init; }

  public double WaitTimeoutSeconds { get; init; }
}

internal sealed class MarginsBody
{
  public double Top { get; init; }

  public double Bottom { get; init; }

  public double Left { get; init; }

  public double Right { get; init; }
}

[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(ConvertRequestBody))]
internal sealed partial class ClientJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// How the request body is written.
/// </summary>
internal static class RequestJson
{
  /// <summary>
  /// The request's type info, writing HTML's <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, and quotes
  /// as they are instead of as <c>\u003C</c> escapes, which would grow a typical document by a
  /// third. The body is JSON for the server, never embedded in a page, so the HTML-safe escaping
  /// buys nothing.
  /// </summary>
  public static readonly JsonTypeInfo<ConvertRequestBody> TypeInfo =
    new ClientJsonSerializerContext(
      new JsonSerializerOptions(ClientJsonSerializerContext.Default.Options)
      {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      }
    ).ConvertRequestBody;
}
