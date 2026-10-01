namespace RemoteReportServer;

/// <summary>
/// The body of <c>POST /html-to-pdf</c>.
/// </summary>
/// <param name="Html">The complete HTML document to convert.</param>
public record HtmlDocument(string Html);
