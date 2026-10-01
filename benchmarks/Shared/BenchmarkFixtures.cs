namespace Atli.Reports.Benchmarks.Shared;

/// <summary>
/// One HTML document the benchmarks convert.
/// </summary>
/// <param name="Name">The short name used on the command line and in results.</param>
/// <param name="FileName">The file in <c>benchmarks/fixtures/</c>.</param>
/// <param name="Description">What the document exercises.</param>
/// <param name="WaitsForSignal">
/// Whether the page renders with JavaScript and signals completion, so converters must wait for it.
/// </param>
internal sealed record BenchmarkFixture(
  string Name,
  string FileName,
  string Description,
  bool WaitsForSignal
);

/// <summary>
/// The fixtures every benchmark shares, plus the page settings both converters receive.
/// </summary>
internal static class BenchmarkFixtures
{
  /// <summary>
  /// The function Atli.Reports exposes to signalling pages (<c>waitForSignal</c>).
  /// </summary>
  public const string SignalName = "reportReady";

  /// <summary>
  /// The Gotenberg <c>waitForExpression</c> that is true at the same moment the page calls
  /// <see cref="SignalName"/>; see <c>fixtures/chart.html</c>.
  /// </summary>
  public const string GotenbergReadyExpression = "window.reportRendered === true";

  /// <summary>
  /// How long a signalling page may take to call the signal, in seconds.
  /// </summary>
  public const int SignalTimeoutSeconds = 30;

  /// <summary>
  /// ISO A4 width in inches; both converters print every fixture on A4.
  /// </summary>
  public const double PaperWidthInches = 8.27;

  /// <summary>
  /// ISO A4 height in inches.
  /// </summary>
  public const double PaperHeightInches = 11.69;

  /// <summary>
  /// The margin on every side, in inches (Atli.Reports' default).
  /// </summary>
  public const double MarginInches = 0.4;

  /// <summary>
  /// The environment variable that overrides where fixtures are read from.
  /// </summary>
  public const string DirectoryVariable = "ATLI_REPORTS_BENCH_FIXTURES";

  public static IReadOnlyList<BenchmarkFixture> All { get; } =
  [
    new(
      "invoice",
      "invoice.html",
      "One-page invoice: embedded CSS, inline SVG logo, line-item table.",
      WaitsForSignal: false
    ),
    new(
      "long-table",
      "long-table.html",
      "Ledger report: a 2,400-row table that paginates to ~50 A4 pages.",
      WaitsForSignal: false
    ),
    new(
      "chart",
      "chart.html",
      "Dashboard drawn by JavaScript after an async data load; signals when ready.",
      WaitsForSignal: true
    ),
    new(
      "assets",
      "assets.html",
      "Inspection report with 11 PNG images (~1.2 MB) inlined as base64 data URIs.",
      WaitsForSignal: false
    ),
  ];

  /// <summary>
  /// Returns the fixture called <paramref name="name"/>.
  /// </summary>
  /// <exception cref="ArgumentException">No fixture has that name.</exception>
  public static BenchmarkFixture Get(string name) =>
    All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
    ?? throw new ArgumentException(
      $"Unknown fixture '{name}'. Known fixtures: {string.Join(", ", All.Select(f => f.Name))}.",
      nameof(name)
    );

  /// <summary>
  /// Finds <c>benchmarks/fixtures</c>: <see cref="DirectoryVariable"/> when set, otherwise the first
  /// match walking up from the current directory and from the application directory.
  /// </summary>
  /// <exception cref="DirectoryNotFoundException">The directory cannot be found.</exception>
  public static string FindDirectory()
  {
    var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
    if (!string.IsNullOrWhiteSpace(configured))
    {
      return Directory.Exists(configured)
        ? Path.GetFullPath(configured)
        : throw new DirectoryNotFoundException(
          $"{DirectoryVariable} points at '{configured}', which does not exist."
        );
    }

    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
      for (
        var directory = new DirectoryInfo(start);
        directory is not null;
        directory = directory.Parent
      )
      {
        var candidate = Path.Combine(directory.FullName, "benchmarks", "fixtures");
        if (File.Exists(Path.Combine(candidate, "invoice.html")))
        {
          return candidate;
        }
      }
    }

    throw new DirectoryNotFoundException(
      $"Could not find benchmarks/fixtures above '{Environment.CurrentDirectory}'. Set {DirectoryVariable}."
    );
  }

  /// <summary>
  /// Reads the HTML of <paramref name="fixture"/> from <paramref name="directory"/>.
  /// </summary>
  public static string ReadHtml(BenchmarkFixture fixture, string directory) =>
    File.ReadAllText(Path.Combine(directory, fixture.FileName));
}
