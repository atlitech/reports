using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Atli.Reports.Engine.Chromium.Page;
using Atli.Reports.Engine.Chromium.Protocol;
using Atli.Reports.Engine.Chromium.Protocol.Messages;
using Atli.Reports.Engine.Chromium.Protocol.Responses;
using Atli.Reports.Engine.Conversion;
using Atli.Reports.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace Atli.Reports.Engine.Pdf;

/// <summary>
/// Prints a Chromium page to PDF and streams the result into a <see cref="PipeWriter"/>.
/// </summary>
internal sealed class ChromiumPdfGenerator(ILogger<ChromiumPdfGenerator> logger)
{
  /// <summary>
  /// The number of bytes requested per <c>IO.read</c> call.
  /// </summary>
  private const int ReadChunkSize = 50 * 1024;

  /// <summary>
  /// Generates a PDF from a Chromium page
  /// </summary>
  public async ValueTask<OneOf<Success, ConversionError>> GeneratePdfAsync(
    ChromiumPage page,
    PipeWriter pipeWriter,
    PdfOptions options,
    CancellationToken ct = default
  )
  {
    try
    {
      var message = CreatePrintToPdfMessage(options);

      await page.SendDevToolsCommandAsync(
        message,
        PrintToPdfResponseSerializationContext.Default.DevToolsResponsePrintToPdfResponse,
        async pagePrintToPdfResponse =>
        {
          // Invalid options (for example a malformed page range or an out-of-range scale) come
          // back as a protocol error; without this check they produced an empty "PDF".
          if (pagePrintToPdfResponse.Error is { } printError)
          {
            throw new DevToolsProtocolException(message.Method, printError);
          }

          if (string.IsNullOrEmpty(pagePrintToPdfResponse.Result?.Stream))
          {
            throw new DevToolsProtocolException($"{message.Method} returned no PDF stream.");
          }

          DevToolsMessage ioReadMessage = new("IO.read");
          ioReadMessage.Parameters.Add("handle", pagePrintToPdfResponse.Result.Stream);
          ioReadMessage.Parameters.Add("size", ReadChunkSize);

          var finished = false;
          while (!finished)
          {
            await page.SendDevToolsCommandAsync(
              ioReadMessage,
              IoReadResponseSerializationContext.Default.DevToolsResponseIoReadResponse,
              async ioReadResponse =>
              {
                if (ioReadResponse.Error is { } readError)
                {
                  throw new DevToolsProtocolException(ioReadMessage.Method, readError);
                }

                if (ioReadResponse.Result?.Eof == true)
                {
                  ClosePdfStream(page, pagePrintToPdfResponse.Result!.Stream);
                  finished = true;
                  return Task.CompletedTask;
                }

                if (ioReadResponse.Result?.Data != null)
                {
                  await ReadAndTransform(ioReadResponse.Result.Data.AsMemory(), pipeWriter, ct);
                }

                return Task.CompletedTask;
              },
              ct
            );
          }

          await pipeWriter.CompleteAsync();
          return Task.CompletedTask;
        },
        ct
      );

      return new Success();
    }
    catch (Exception ex)
    {
      LogMessages.PdfGenerationFailed(logger, ex, page.PageId);
      return ConversionErrors.FromException(
        ex is DevToolsProtocolException
          ? $"PDF generation failed: {ex.Message}"
          : "PDF generation failed",
        ex,
        ConversionErrorKind.RenderFailed,
        ct
      );
    }
  }

  /// <summary>
  /// Maps <paramref name="options"/> onto a <c>Page.printToPDF</c> command.
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

    return message;
  }

  private static async ValueTask ReadAndTransform(
    ReadOnlyMemory<char> data,
    PipeWriter writer,
    CancellationToken ct
  )
  {
    if (data.Length == 0)
    {
      return;
    }

    using Base64StreamTransformer transformer = new(FromBase64TransformMode.IgnoreWhiteSpaces);
    var sharedPool = ArrayPool<byte>.Shared;
    var dataBytes = sharedPool.Rent(data.Length);

    try
    {
      var totalBytes = Encoding.UTF8.GetBytes(data.Span, dataBytes);
      var index = 0;
      var writerBuffer = writer.GetMemory(Base64StreamTransformer.OutputBlockSize);
      var dataBytesSpan = dataBytes.AsMemory();

      while (index < totalBytes)
      {
        ct.ThrowIfCancellationRequested();

        var bytesRead = Math.Min(totalBytes - index, Base64StreamTransformer.InputBlockSize);
        var inputBlockMemory = dataBytesSpan.Slice(index, bytesRead);
        index += bytesRead;

        var count = transformer.TransformBlock(
          inputBlockMemory.Span,
          0,
          bytesRead,
          writerBuffer.Span,
          0
        );

        writer.Advance(count);
        var flushResult = await writer.FlushAsync(ct);

        if (flushResult.IsCanceled || flushResult.IsCompleted)
        {
          break;
        }

        writerBuffer = writer.GetMemory(count);
      }
    }
    finally
    {
      sharedPool.Return(dataBytes);
      transformer.Reset();
    }
  }

  private static void ClosePdfStream(ChromiumPage page, string stream)
  {
    DevToolsMessage ioCloseMessage = new("IO.close");
    ioCloseMessage.Parameters.Add("handle", stream);
    page.SendDevToolsCommand(ioCloseMessage);
  }
}
