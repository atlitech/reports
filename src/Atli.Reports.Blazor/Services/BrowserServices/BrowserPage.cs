using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Atli.Reports.Blazor.Enums;
using Atli.Reports.Blazor.Models;
using Atli.Reports.Blazor.Services.BrowserServices.Logs;
using Atli.Reports.Blazor.Services.BrowserServices.Requests;
using Atli.Reports.Blazor.Services.BrowserServices.Responses;
using Microsoft.Extensions.Logging;

namespace Atli.Reports.Blazor.Services.BrowserServices;

/// <summary>
/// Represents a page in the browser
/// </summary>
/// <remarks>
/// Creates a new instance of the BrowserPage
/// </remarks>
/// <param name="logger"> The logger</param>
/// <param name="targetId"> The id of the page in the browser</param>
/// <param name="connection"> The connection to the browser</param>
internal sealed class BrowserPage(
  ILogger<BrowserPage> logger,
  string targetId,
  Connection connection
) : IAsyncDisposable
{
  /// <summary>
  /// The id of the page in the browser
  /// </summary>
  internal readonly string TargetId = targetId;
  private readonly CustomFromBase64Transform _transform = new(
    FromBase64TransformMode.IgnoreWhiteSpaces
  );

  /// <summary>
  /// Displays the HTML in the browser
  /// </summary>
  /// <param name="html"> HTML to display</param>
  /// <param name="stoppingToken"> Cancellation token</param>
  /// <exception cref="ArgumentException"> Thrown when the HTML is null or whitespace</exception>
  internal async Task DisplayHtml(string html, CancellationToken stoppingToken = default)
  {
    await connection.ConnectAsync(stoppingToken);
    if (string.IsNullOrWhiteSpace(html))
    {
      throw new ArgumentException("Value cannot be null or whitespace.", nameof(html));
    }

    // Enables or disables the cache
    BrowserMessage cacheMessage = new("Network.setCacheDisabled");
    cacheMessage.Parameters.Add("cacheDisabled", false);
    connection.SendAsync(cacheMessage);

    BrowserMessage getPageFrameTreeMessage = new("Page.getFrameTree");
    await connection.SendAsync(
      getPageFrameTreeMessage,
      PageGetFrameTreeResponseSerializationContext
        .Default
        .BrowserResultResponsePageGetFrameTreeResponse,
      response =>
      {
        BrowserMessage pageSetDocumentContentMessage = new("Page.setDocumentContent");
        pageSetDocumentContentMessage.Parameters.Add("frameId", response.Result.FrameTree.Frame.Id);
        pageSetDocumentContentMessage.Parameters.Add("html", html);
        connection.SendAsync(pageSetDocumentContentMessage);
      },
      stoppingToken
    );
  }

  internal async ValueTask ConvertPageToPdf(
    PipeWriter pipeWriter,
    BlazorReportsPageSettings pageSettings,
    CancellationToken stoppingToken = default
  )
  {
    await connection.ConnectAsync(stoppingToken);
    var message = CreatePrintToPdfBrowserMessage(pageSettings);

    await connection.SendAsync(
      message,
      PagePrintToPdfResponseSerializationContext
        .Default
        .BrowserResultResponsePagePrintToPdfResponse,
      async pagePrintToPdfResponse =>
      {
        if (string.IsNullOrEmpty(pagePrintToPdfResponse.Result.Stream))
        {
          return;
        }

        BrowserMessage ioReadMessage = new("IO.read");
        ioReadMessage.Parameters.Add("handle", pagePrintToPdfResponse.Result.Stream);
        ioReadMessage.Parameters.Add("size", 50 * 1024);

        var finished = false;
        while (true)
        {
          if (finished)
          {
            break;
          }

          await connection.SendAsync(
            ioReadMessage,
            IoReadResponseSerializationContext.Default.BrowserResultResponseIoReadResponse,
            async ioReadResponse =>
            {
              if (ioReadResponse.Result.Eof)
              {
                await ClosePdfStream(pagePrintToPdfResponse.Result.Stream, stoppingToken);
                finished = true;
                return;
              }

              await ReadAndTransform(
                ioReadResponse.Result.Data.AsMemory(),
                pipeWriter,
                stoppingToken
              );
            },
            stoppingToken
          );
        }

        // Notify the PipeReader that there is no more data to be written
        await pipeWriter.CompleteAsync();
      },
      stoppingToken
    );
  }

  private static BrowserMessage CreatePrintToPdfBrowserMessage(
    BlazorReportsPageSettings pageSettings
  )
  {
    BrowserMessage message = new("Page.printToPDF");
    message.Parameters.Add(
      "landscape",
      pageSettings.Orientation == BlazorReportsPageOrientation.Landscape
    );
    message.Parameters.Add("paperHeight", pageSettings.PaperHeight);
    message.Parameters.Add("paperWidth", pageSettings.PaperWidth);
    message.Parameters.Add("marginTop", pageSettings.MarginTop);
    message.Parameters.Add("marginBottom", pageSettings.MarginBottom);
    message.Parameters.Add("marginLeft", pageSettings.MarginLeft);
    message.Parameters.Add("marginRight", pageSettings.MarginRight);
    message.Parameters.Add("printBackground", !pageSettings.IgnoreBackground);
    message.Parameters.Add("transferMode", "ReturnAsStream");

    return message;
  }

  private async ValueTask ReadAndTransform(
    ReadOnlyMemory<char> data,
    PipeWriter writer,
    CancellationToken stoppingToken
  )
  {
    if (data.Length == 0)
    {
      return;
    }

    var sharedPool = ArrayPool<byte>.Shared;
    var dataBytes = sharedPool.Rent(data.Length);
    var inputBlock = sharedPool.Rent(CustomFromBase64Transform.InputBlockSize);

    try
    {
      var totalBytes = Encoding.UTF8.GetBytes(data.Span, dataBytes);
      var index = 0;
      var writerBuffer = writer.GetMemory(CustomFromBase64Transform.OutputBlockSize);
      var dataBytesSpan = dataBytes.AsMemory();

      while (index < totalBytes)
      {
        stoppingToken.ThrowIfCancellationRequested();
        var bytesRead = Math.Min(totalBytes - index, CustomFromBase64Transform.InputBlockSize);
        var inputBlockMemory = dataBytesSpan.Slice(index, bytesRead);
        index += bytesRead;

        var count = _transform.TransformBlock(
          inputBlockMemory.Span,
          0,
          bytesRead,
          writerBuffer.Span,
          0
        );
        writer.Advance(count);
        var flushResult = await writer.FlushAsync(stoppingToken);
        if (flushResult.IsCanceled || flushResult.IsCompleted)
        {
          break;
        }

        writerBuffer = writer.GetMemory(count); // Get a new buffer after advancing
      }
    }
    finally
    {
      sharedPool.Return(dataBytes);
      sharedPool.Return(inputBlock);
      _transform.Reset();
    }
  }

  private async ValueTask ClosePdfStream(string stream, CancellationToken stoppingToken = default)
  {
    await connection.ConnectAsync(stoppingToken);
    BrowserMessage ioCloseMessage = new("IO.close");
    ioCloseMessage.Parameters.Add("handle", stream);
    connection.SendAsync(ioCloseMessage);
  }

  /// <summary>
  /// Disposes the BrowserPage
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    LogMessages.BrowserPageDispose(logger, TargetId);
    await connection.DisposeAsync();
    _transform.Dispose();
  }
}
