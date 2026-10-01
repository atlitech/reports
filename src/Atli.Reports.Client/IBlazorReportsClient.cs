namespace Atli.Reports.Client;

public interface IBlazorReportsClient
{
  Task<Stream> GetReport(string reportName, CancellationToken cancellationToken = default);
  Task<Stream> GetReport<T>(
    string reportName,
    T reportData,
    CancellationToken cancellationToken = default
  );
}
