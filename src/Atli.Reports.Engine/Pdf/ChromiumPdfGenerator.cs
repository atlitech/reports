using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Atli.Reports.Engine.Chromium.Connection;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol.Messages;

namespace Atli.Reports.Engine.Pdf;

/// <summary>
/// Maps <see cref="PdfOptions"/> onto <c>Page.printToPDF</c> and streams the printed PDF out of the
/// browser.
/// </summary>
internal static class ChromiumPdfGenerator
{
  /// <summary>
  /// The default number of PDF bytes requested per <c>IO.read</c>.
  /// </summary>
  public const int DefaultReadChunkSize = 1024 * 1024;

  /// <summary>
  /// Maps <paramref name="options"/> onto a <c>Page.printToPDF</c> command that returns the PDF as
  /// a stream handle.
  /// </summary>
  internal static DevToolsMessage CreatePrintToPdfMessage(PdfOptions options)
  {
    DevToolsMessage message = new("Page.printToPDF");
    message.Parameters.Add("landscape", options.Orientation == PageOrientation.Landscape);
    message.Parameters.Add("paperHeight", options.PaperSize.Height);
    message.Parameters.Add("paperWidth", options.PaperSize.Width);
    message.Parameters.Add("marginTop", options.Margins.Top);
    message.Parameters.Add("marginBottom", options.Margins.Bottom);
    message.Parameters.Add("marginLeft", options.Margins.Left);
    message.Parameters.Add("marginRight", options.Margins.Right);
    message.Parameters.Add("printBackground", options.PrintBackground);
    message.Parameters.Add("scale", options.Scale);
    message.Parameters.Add("transferMode", "ReturnAsStream");

    if (!string.IsNullOrEmpty(options.HeaderTemplate))
    {
      message.Parameters.Add("headerTemplate", options.HeaderTemplate);
    }

    if (!string.IsNullOrEmpty(options.FooterTemplate))
    {
      message.Parameters.Add("footerTemplate", options.FooterTemplate);
    }

    if (options.DisplayHeaderFooter)
    {
      message.Parameters.Add("displayHeaderFooter", true);
    }

    if (!string.IsNullOrEmpty(options.PageRanges))
    {
      message.Parameters.Add("pageRanges", options.PageRanges);
    }

    if (options.PreferCssPageSize)
    {
      message.Parameters.Add("preferCSSPageSize", true);
    }

    if (options.GenerateTaggedPdf is { } tagged)
    {
      message.Parameters.Add("generateTaggedPDF", tagged);
    }

    return message;
  }

  /// <summary>
  /// Reads the stream <paramref name="handle"/> with <c>IO.read</c> and writes the decoded bytes to
  /// <paramref name="destination"/> as each chunk arrives, then closes the handle.
  /// </summary>
  /// <remarks>
  /// The next chunk is requested before the current one is written, so the browser reads while the
  /// destination writes. Chunks are decoded straight from the reply's UTF-8 JSON into a pooled
  /// buffer: no base64 string is ever materialized.
  /// </remarks>
  /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
  /// <exception cref="DestinationWriteException"><paramref name="destination"/> threw.</exception>
  public static async Task<long> CopyToAsync(
    DevToolsSession session,
    string handle,
    Stream destination,
    int chunkSize,
    CancellationToken cancellationToken
  )
  {
    DevToolsMessage read = new("IO.read");
    read.Parameters.Add("handle", handle);
    read.Parameters.Add("size", chunkSize);

    long written = 0;
    Task<DevToolsReply>? pending = session.SendAsync(read, cancellationToken);
    try
    {
      while (pending is not null)
      {
        PdfChunk chunk;
        using (var reply = await pending)
        {
          chunk = DecodeReadResult(reply.Result);
        }

        pending = chunk.Eof ? null : session.SendAsync(read, cancellationToken);
        try
        {
          if (chunk.Length > 0)
          {
            await WriteAsync(destination, chunk, cancellationToken);
            written += chunk.Length;
          }
        }
        finally
        {
          chunk.Return();
        }
      }

      return written;
    }
    finally
    {
      if (pending is not null)
      {
        // Leaving early (a failed write, cancellation): release the read still in flight.
        DevToolsReply.DisposeWhenReady(pending);
      }

      DevToolsMessage close = new("IO.close");
      close.Parameters.Add("handle", handle);
      session.Post(close);
    }
  }

  /// <summary>
  /// Decodes the result of an <c>IO.read</c> into a pooled buffer.
  /// </summary>
  internal static PdfChunk DecodeReadResult(ReadOnlySpan<byte> result)
  {
    Utf8JsonReader reader = new(result);
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      throw new JsonException("IO.read returned no result object.");
    }

    var eof = false;
    var base64Encoded = false;
    byte[]? unescaped = null;
    ReadOnlySpan<byte> data = default;

    try
    {
      while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
      {
        if (reader.ValueTextEquals("eof"u8))
        {
          reader.Read();
          eof = reader.GetBoolean();
        }
        else if (reader.ValueTextEquals("base64Encoded"u8))
        {
          reader.Read();
          base64Encoded = reader.GetBoolean();
        }
        else if (reader.ValueTextEquals("data"u8))
        {
          reader.Read();
          if (reader.ValueIsEscaped)
          {
            unescaped = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
            data = unescaped.AsSpan(0, reader.CopyString(unescaped));
          }
          else
          {
            data = reader.ValueSpan;
          }
        }
        else
        {
          reader.Read();
          reader.Skip();
        }
      }

      if (data.IsEmpty)
      {
        return new PdfChunk(null, 0, eof);
      }

      if (!base64Encoded)
      {
        var copy = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(copy);
        return new PdfChunk(copy, data.Length, eof);
      }

      var decoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(data.Length));
      if (Base64.DecodeFromUtf8(data, decoded, out _, out var written) != OperationStatus.Done)
      {
        ArrayPool<byte>.Shared.Return(decoded);
        throw new InvalidDataException("IO.read returned malformed base64 data.");
      }

      return new PdfChunk(decoded, written, eof);
    }
    finally
    {
      if (unescaped is not null)
      {
        ArrayPool<byte>.Shared.Return(unescaped);
      }
    }
  }

  private static async Task WriteAsync(
    Stream destination,
    PdfChunk chunk,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await destination.WriteAsync(chunk.Buffer.AsMemory(0, chunk.Length), cancellationToken);
    }
    catch (Exception exception)
      when (exception is not OperationCanceledException
        || !cancellationToken.IsCancellationRequested
      )
    {
      throw new DestinationWriteException(exception);
    }
  }

  /// <summary>
  /// Decoded PDF bytes in a pooled buffer.
  /// </summary>
  internal readonly record struct PdfChunk(byte[]? Buffer, int Length, bool Eof)
  {
    public void Return()
    {
      if (Buffer is not null)
      {
        ArrayPool<byte>.Shared.Return(Buffer);
      }
    }
  }
}
