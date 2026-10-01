using System.Text;

namespace Atli.Reports.AppHost.Tests.Support;

internal static class Responses
{
  /// <summary>
  /// Reads the PDF a request returned, and fails the test unless the request succeeded with
  /// <c>application/pdf</c>.
  /// </summary>
  public static async Task<byte[]> ReadPdfAsync(
    HttpResponseMessage response,
    CancellationToken cancellationToken
  )
  {
    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

    await Assert
      .That(response.IsSuccessStatusCode)
      .IsTrue()
      .Because(
        $"the response was {(int)response.StatusCode} {response.ReasonPhrase}: "
          + Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 2000)))
      );
    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pdf");
    return body;
  }
}
