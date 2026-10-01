namespace SimpleReportServer;

/// <summary>
/// Data for <see cref="AsyncJavaScriptReport"/>.
/// </summary>
/// <param name="Title">The report title.</param>
/// <param name="DelayMilliseconds">How long the report's JavaScript works before it is done.</param>
public record AsyncJavaScriptReportData(
  string Title = "Sales by region",
  int DelayMilliseconds = 500
);
