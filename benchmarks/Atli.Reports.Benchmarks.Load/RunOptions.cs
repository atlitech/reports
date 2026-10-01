using System.Globalization;
using Atli.Reports.Benchmarks.Shared;

namespace Atli.Reports.Benchmarks.Load;

/// <summary>
/// The settings of a <c>run</c>, parsed from <c>--name value</c> arguments.
/// </summary>
/// <remarks>
/// The defaults are the full mode; <c>benchmarks/run.sh</c> passes every value explicitly.
/// </remarks>
internal sealed record RunOptions
{
  public string Mode { get; init; } = "custom";
  public IReadOnlyList<string> Targets { get; init; } = ["atli", "gotenberg"];
  public IReadOnlyList<string> Fixtures { get; init; } =
  [.. BenchmarkFixtures.All.Select(f => f.Name)];
  public IReadOnlyList<int> Concurrency { get; init; } = [1, 4, 16, 64];
  public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(5);
  public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Whether to measure higher concurrency levels after a level produced no valid PDF.
  /// </summary>
  public bool KeepGoing { get; init; }
  public string Cpus { get; init; } = "2";
  public string Memory { get; init; } = "2g";
  public string AtliImage { get; init; } = "atli-reports-server:bench";
  public string GotenbergImage { get; init; } =
    "gotenberg/gotenberg:8.37.0-chromium@sha256:0d28ae9a96441588ef739623726bd500ad0720b77266c6f1351a13e333fbd61c";
  public int AtliPort { get; init; } = 18080;
  public int GotenbergPort { get; init; } = 13000;
  public string? ComposeFile { get; init; }
  public string ProjectName { get; init; } = "atli-reports-bench";
  public string? OutputDirectory { get; init; }
  public string? Name { get; init; }
  public string? Note { get; init; }

  public static RunOptions Parse(IReadOnlyList<string> arguments)
  {
    RunOptions options = new();
    for (var i = 0; i < arguments.Count; i++)
    {
      var key = arguments[i];
      if (key == "--keep-going")
      {
        options = options with { KeepGoing = true };
        continue;
      }

      string Value() =>
        i + 1 < arguments.Count
          ? arguments[++i]
          : throw new ArgumentException($"Missing value for {key}.");

      options = key switch
      {
        "--mode" => options with { Mode = Value() },
        "--targets" => options with { Targets = List(Value()) },
        "--fixtures" => options with { Fixtures = List(Value()) },
        "--concurrency" => options with
        {
          Concurrency = [.. List(Value()).Select(v => int.Parse(v, CultureInfo.InvariantCulture))],
        },
        "--warmup" => options with { Warmup = Seconds(Value()) },
        "--duration" => options with { Duration = Seconds(Value()) },
        "--timeout" => options with { RequestTimeout = Seconds(Value()) },
        "--cpus" => options with { Cpus = Value() },
        "--memory" => options with { Memory = Value() },
        "--atli-image" => options with { AtliImage = Value() },
        "--gotenberg-image" => options with { GotenbergImage = Value() },
        "--atli-port" => options with
        {
          AtliPort = int.Parse(Value(), CultureInfo.InvariantCulture),
        },
        "--gotenberg-port" => options with
        {
          GotenbergPort = int.Parse(Value(), CultureInfo.InvariantCulture),
        },
        "--compose-file" => options with { ComposeFile = Value() },
        "--project-name" => options with { ProjectName = Value() },
        "--out" => options with { OutputDirectory = Value() },
        "--name" => options with { Name = Value() },
        "--note" => options with { Note = Value() },
        _ => throw new ArgumentException($"Unknown option '{key}'."),
      };
    }

    foreach (var fixture in options.Fixtures)
    {
      BenchmarkFixtures.Get(fixture);
    }

    if (options.Concurrency.Any(c => c < 1))
    {
      throw new ArgumentException("Concurrency levels must be at least 1.");
    }

    return options;
  }

  private static string[] List(string value) =>
    value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static TimeSpan Seconds(string value) =>
    TimeSpan.FromSeconds(double.Parse(value, CultureInfo.InvariantCulture));
}
