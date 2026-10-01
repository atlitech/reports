using Atli.Reports.Benchmarks.Shared;
using Atli.Reports.Engine;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atli.Reports.Benchmarks;

/// <summary>
/// Converts each fixture through both public <see cref="IHtmlToPdfConverter"/> overloads.
/// </summary>
/// <remarks>
/// <para>
/// One converter is built per fixture in <see cref="SetupAsync"/> and reused for every operation, the
/// way an application uses the registered singleton. Its first conversion runs in the setup, so a
/// browser the engine keeps alive between conversions starts outside the measurement; today's engine
/// launches a browser for every conversion, and that launch is part of each operation.
/// </para>
/// <para>
/// Engine options bind from <c>ReportsEngine__*</c> environment variables, exactly like the server, for
/// example <c>ReportsEngine__Browser__NoSandbox=true</c> on Linux hosts that block Chromium's sandbox.
/// </para>
/// <para>
/// Allocations are the benchmark process's managed allocations only; the browser's memory is not
/// visible to BenchmarkDotNet.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ConversionBenchmarks
{
  private ServiceProvider? _provider;
  private IHtmlToPdfConverter? _converter;
  private string _html = "";
  private PdfOptions _options = new();

  public static IEnumerable<string> FixtureNames => BenchmarkFixtures.All.Select(f => f.Name);

  [ParamsSource(nameof(FixtureNames))]
  public string Fixture { get; set; } = "";

  [GlobalSetup]
  public async Task SetupAsync()
  {
    var fixture = BenchmarkFixtures.Get(Fixture);
    _html = BenchmarkFixtures.ReadHtml(fixture, BenchmarkFixtures.FindDirectory());
    _options = new PdfOptions
    {
      PaperSize = PaperSize.A4,
      Margins = Margins.Default,
      PrintBackground = true,
      WaitForSignal = fixture.WaitsForSignal ? BenchmarkFixtures.SignalName : null,
      WaitTimeout = TimeSpan.FromSeconds(BenchmarkFixtures.SignalTimeoutSeconds),
    };

    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    _provider = new ServiceCollection()
      .AddReportsEngine(configuration.GetSection(ReportsEngineOptions.SectionName))
      .BuildServiceProvider();
    _converter = _provider.GetRequiredService<IHtmlToPdfConverter>();

    // Fail fast, with the engine's reason, instead of measuring a failing conversion.
    var bytes = await ToMemoryStream();
    if (bytes < 1024)
    {
      throw new InvalidOperationException($"'{Fixture}' produced only {bytes} bytes.");
    }
  }

  [GlobalCleanup]
  public async Task CleanupAsync()
  {
    if (_provider is not null)
    {
      await _provider.DisposeAsync();
    }
  }

  /// <summary>
  /// <c>ConvertAsync(html, options)</c>: the PDF is returned as an in-memory stream.
  /// </summary>
  [Benchmark(Baseline = true)]
  public async Task<long> ToMemoryStream()
  {
    var result = await Converter.ConvertAsync(_html, _options);
    if (result.TryPickT1(out var error, out var pdf))
    {
      throw new InvalidOperationException($"Conversion failed: {error.Kind}: {error.Message}");
    }

    await using (pdf)
    {
      return pdf.Length;
    }
  }

  /// <summary>
  /// <c>ConvertAsync(html, destination, options)</c>: the PDF is written to a caller's stream.
  /// </summary>
  [Benchmark]
  public async Task<bool> ToDestinationStream()
  {
    var result = await Converter.ConvertAsync(_html, Stream.Null, _options);
    return result.TryPickT1(out var error, out _)
      ? throw new InvalidOperationException($"Conversion failed: {error.Kind}: {error.Message}")
      : true;
  }

  private IHtmlToPdfConverter Converter =>
    _converter ?? throw new InvalidOperationException("SetupAsync has not run.");
}
