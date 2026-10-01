using System.Text.Json;
using Atli.Reports.Benchmarks.Load;
using Atli.Reports.Benchmarks.Load.Fixtures;
using Atli.Reports.Benchmarks.Load.Results;
using Atli.Reports.Benchmarks.Shared;

const string Usage = """
  Atli.Reports load benchmark driver. Normally started by benchmarks/run.sh.

  Commands:
    run [options]                 Start each target in Docker, drive it, write results.
      --mode <label>              Label recorded in the results (quick, full, custom).
      --targets atli,gotenberg    Targets to measure.
      --fixtures a,b              Fixtures: invoice, long-table, chart, assets.
      --concurrency 1,4,16,64     Closed-loop worker counts.
      --warmup <s> --duration <s> Warm-up and measured window per cell.
      --timeout <s>               Client-side deadline per request (default 30, Gotenberg's API timeout).
      --keep-going                Measure higher concurrency levels even after a level produced no PDF.
      --cpus <n> --memory <size>  Limits for each target container (compose cpus/mem_limit).
      --atli-image <ref> --gotenberg-image <ref>
      --atli-port <n> --gotenberg-port <n>
      --compose-file <path> --project-name <name>
      --out <dir> --name <base>   Results directory and file base name.
      --note <text>               Free text printed at the top of the report.
    generate-fixtures [--out <dir>]
                                  Rewrite long-table.html and assets.html.
    report <results.json>         Re-render the Markdown and CSV from a results JSON file.
  """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
  Console.WriteLine(Usage);
  return args.Length == 0 ? 1 : 0;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
  e.Cancel = true;
  Console.Error.WriteLine("Canceling: finishing the current request and stopping containers...");
  cancellation.Cancel();
};

try
{
  switch (args[0])
  {
    case "run":
      return await new BenchmarkRun(RunOptions.Parse(args[1..])).ExecuteAsync(cancellation.Token);

    case "generate-fixtures":
      var directory =
        args.Length >= 3 && args[1] == "--out" ? args[2] : BenchmarkFixtures.FindDirectory();
      foreach (var path in FixtureGenerator.Generate(directory))
      {
        Console.WriteLine($"Wrote {path} ({new FileInfo(path).Length:N0} bytes)");
      }

      return 0;

    case "report" when args.Length == 2:
      var json = args[1];
      var run =
        JsonSerializer.Deserialize(
          await File.ReadAllTextAsync(json),
          ResultsJsonContext.Default.RunResult
        ) ?? throw new InvalidOperationException($"{json} holds no results.");
      var basePath = Path.ChangeExtension(Path.GetFullPath(json), null);
      await ReportWriter.WriteAsync(run, basePath + ".md", basePath + ".json", basePath + ".csv");
      Console.WriteLine($"Wrote {basePath}.md and .csv");
      return 0;

    default:
      Console.Error.WriteLine(Usage);
      return 1;
  }
}
catch (ArgumentException exception)
{
  Console.Error.WriteLine(exception.Message);
  return 2;
}
catch (OperationCanceledException)
{
  Console.Error.WriteLine("Canceled.");
  return 130;
}
