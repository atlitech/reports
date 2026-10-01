using Atli.Reports.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// One job, run in-process: each operation is dominated by the browser (hundreds of milliseconds), so
// BenchmarkDotNet's per-process isolation and its pilot stage would add minutes without improving
// accuracy. "--quick" trades iterations for time; every other argument goes to BenchmarkDotNet
// (for example --filter '*ToMemoryStream*' or --artifacts <dir>).
var quick = args.Contains("--quick");
List<string> benchmarkArguments = [.. args.Where(a => a != "--quick")];
if (!benchmarkArguments.Contains("--filter"))
{
  // Without a filter BenchmarkDotNet asks interactively which benchmarks to run.
  benchmarkArguments.AddRange(["--filter", "*"]);
}

var job = Job
  .Default.WithToolchain(InProcessEmitToolchain.Instance)
  .WithLaunchCount(1)
  .WithWarmupCount(quick ? 2 : 3)
  .WithIterationCount(quick ? 6 : 15)
  .WithInvocationCount(1)
  .WithUnrollFactor(1)
  .WithId(quick ? "quick" : "full");

var config = DefaultConfig.Instance.AddJob(job).WithOptions(ConfigOptions.JoinSummary);

BenchmarkDotNet
  .Running.BenchmarkSwitcher.FromAssembly(typeof(ConversionBenchmarks).Assembly)
  .Run([.. benchmarkArguments], config);
