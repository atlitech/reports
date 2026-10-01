using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Atli.Reports.Benchmarks.Load.Results;

/// <summary>
/// Writes a run as Markdown (for people), JSON (everything), and CSV (one row per cell).
/// </summary>
internal static class ReportWriter
{
  private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

  public static async Task WriteAsync(
    RunResult run,
    string markdownPath,
    string jsonPath,
    string csvPath
  )
  {
    await File.WriteAllTextAsync(
      jsonPath,
      JsonSerializer.Serialize(run, ResultsJsonContext.Default.RunResult) + "\n"
    );
    await File.WriteAllTextAsync(csvPath, BuildCsv(run));
    await File.WriteAllTextAsync(
      markdownPath,
      BuildMarkdown(run, Path.GetFileName(jsonPath), Path.GetFileName(csvPath))
    );
  }

  public static string BuildMarkdown(RunResult run, string jsonName, string csvName)
  {
    var env = run.Environment;
    var settings = run.Settings;
    StringBuilder md = new();
    md.AppendLine(
      Invariant,
      $"# Load benchmark: Atli.Reports vs Gotenberg — {env.StartedAt:yyyy-MM-dd} ({env.GitCommit})"
    );
    md.AppendLine();
    if (!string.IsNullOrWhiteSpace(settings.Note))
    {
      md.AppendLine(Invariant, $"> {settings.Note}");
      md.AppendLine();
    }

    md.AppendLine(
      Invariant,
      $"- **Mode:** {settings.Mode} · **targets:** {string.Join(", ", settings.Targets)} · **engine commit:** `{env.EngineCommit}` (HEAD `{env.GitCommit}` on `{env.GitBranch}`{(env.GitDirty ? ", **uncommitted changes**" : "")})"
    );
    md.AppendLine(
      Invariant,
      $"- **Per target container:** {settings.Cpus} CPUs, {settings.Memory} memory (swap off), started fresh for every cell; one container at a time"
    );
    md.AppendLine(
      Invariant,
      $"- **Each cell:** fresh container → {settings.WarmupSeconds:0.#} s warm-up (at most 4 workers, drained) → measured phase: N closed-loop workers start requests for {settings.DurationSeconds:0.#} s, then in-flight requests finish; client timeout {settings.RequestTimeoutSeconds:0.#} s"
    );
    md.AppendLine(
      Invariant,
      $"- **Host:** {env.HostCpu}, {env.HostLogicalCores} logical cores, {env.HostMemory}, {env.HostOs}; load average {env.HostLoadStart} at start, {env.HostLoadEnd} at end"
    );
    md.AppendLine();
    md.AppendLine(
      "How to read this: `docs/s` is the number of valid PDFs the measured phase produced divided by its wall time (drain included). Latency percentiles cover the successful requests; failed requests are counted under *errors*, by class (`http_<status>`, `client_timeout`, `transport_*`, `invalid_pdf`). *CPU s/doc* is the container's CPU time per valid PDF — the cost of a document, and the number least distorted by other load on the machine. *Peak mem* is the highest `docker stats` reading (usage minus page cache, ~1 s sampling). See `benchmarks/README.md` for the full methodology."
    );
    md.AppendLine();

    AppendSummary(md, run);
    AppendFidelity(md, run);
    AppendAnomalies(md, run);
    AppendDetail(md, run);
    AppendEnvironment(md, run);

    md.AppendLine("## Raw data");
    md.AppendLine();
    md.AppendLine(
      Invariant,
      $"- [`{jsonName}`]({jsonName}) — every setting, environment fact, and cell result"
    );
    md.AppendLine(Invariant, $"- [`{csvName}`]({csvName}) — one row per cell");
    return md.ToString();
  }

  private static void AppendSummary(StringBuilder md, RunResult run)
  {
    var targets = run.Settings.Targets;
    md.AppendLine("## Summary");
    md.AppendLine();
    foreach (var fixture in run.Fixtures)
    {
      var cells = run.Cells.Where(c => c.Fixture == fixture.Name).ToList();
      if (cells.Count == 0)
      {
        continue;
      }

      md.AppendLine(
        Invariant,
        $"### {fixture.Name} — {fixture.Description} ({FormatBytes(fixture.HtmlBytes)} HTML)"
      );
      md.AppendLine();
      StringBuilder header = new("| Concurrency |");
      StringBuilder rule = new("|---:|");
      foreach (
        var metric in new[] { "docs/s", "p50 ms", "p95 ms", "CPU s/doc", "errors", "peak mem" }
      )
      {
        foreach (var target in targets)
        {
          header.Append(Invariant, $" {target} {metric} |");
          rule.Append("---:|");
        }
      }

      md.AppendLine(header.ToString());
      md.AppendLine(rule.ToString());
      foreach (var concurrency in cells.Select(c => c.Concurrency).Distinct().Order())
      {
        StringBuilder row = new();
        row.Append(Invariant, $"| {concurrency} |");
        var byTarget = targets.ToDictionary(
          t => t,
          t => cells.FirstOrDefault(c => c.Target == t && c.Concurrency == concurrency)
        );
        AppendCells(
          row,
          targets,
          byTarget,
          c => c.Failure is null ? c.DocsPerSecond.ToString("0.00", Invariant) : "—"
        );
        AppendCells(row, targets, byTarget, c => FormatMs(c.Latency?.P50));
        AppendCells(row, targets, byTarget, c => FormatMs(c.Latency?.P95));
        AppendCells(row, targets, byTarget, c => Format(c.CpuSecondsPerDoc, "0.00"));
        AppendCells(row, targets, byTarget, FormatErrorCount);
        AppendCells(row, targets, byTarget, c => FormatBytes(c.MemoryPeakSampledBytes));
        md.AppendLine(row.ToString());
      }

      md.AppendLine();
    }
  }

  private static void AppendCells(
    StringBuilder row,
    IReadOnlyList<string> targets,
    Dictionary<string, CellResult?> byTarget,
    Func<CellResult, string> format
  )
  {
    foreach (var target in targets)
    {
      row.Append(Invariant, $" {(byTarget[target] is { } cell ? format(cell) : "")} |");
    }
  }

  private static void AppendFidelity(StringBuilder md, RunResult run)
  {
    md.AppendLine("## Output check");
    md.AppendLine();
    md.AppendLine(
      "Every response is checked for a `%PDF-` header and `%%EOF` trailer and its pages are counted. Page counts and sizes that differ between targets mean the two converters did not print the same document."
    );
    md.AppendLine();
    var targets = run.Settings.Targets;
    StringBuilder header = new("| Fixture |");
    StringBuilder rule = new("|---|");
    foreach (var target in targets)
    {
      header.Append(Invariant, $" {target} pages | {target} median PDF |");
      rule.Append("---:|---:|");
    }

    md.AppendLine(header.ToString());
    md.AppendLine(rule.ToString());
    foreach (var fixture in run.Fixtures)
    {
      StringBuilder row = new();
      row.Append(Invariant, $"| {fixture.Name} |");
      foreach (var target in targets)
      {
        var cells = run.Cells.Where(c => c.Fixture == fixture.Name && c.Target == target).ToList();
        var pages = cells
          .SelectMany(c => c.PageCounts.Keys)
          .Distinct()
          .Select(p => int.Parse(p, Invariant))
          .Order()
          .ToList();
        var medians = cells
          .Where(c => c.MedianPdfBytes is not null)
          .Select(c => c.MedianPdfBytes!.Value)
          .ToList();
        row.Append(
          Invariant,
          $" {(pages.Count == 0 ? "—" : string.Join(" / ", pages))} | {(medians.Count == 0 ? "—" : FormatBytes(medians.Order().ElementAt((medians.Count - 1) / 2)))} |"
        );
      }

      md.AppendLine(row.ToString());
    }

    md.AppendLine();
  }

  private static void AppendAnomalies(StringBuilder md, RunResult run)
  {
    List<string> notes = [];
    foreach (var cell in run.Cells)
    {
      var id = $"{cell.Target} · {cell.Fixture} · c={cell.Concurrency}";
      if (cell.Failure is not null)
      {
        notes.Add($"{id}: not measured — {cell.Failure}");
        continue;
      }

      if (cell.Succeeded == 0 && !run.Settings.KeepGoing)
      {
        notes.Add($"{id}: produced no valid PDF, so higher concurrency levels were skipped");
      }

      if (cell.Errors.Count > 0)
      {
        notes.Add(
          $"{id}: {cell.Errors.Values.Sum()} of {cell.Requests} requests failed ({string.Join(", ", cell.Errors.Select(e => $"{e.Key} ×{e.Value}"))})"
            + Details(cell, cell.Errors.Keys)
        );
      }

      if (cell.WarmupFailures > 0)
      {
        notes.Add(
          $"{id}: {cell.WarmupFailures} of {cell.WarmupRequests} warm-up requests failed"
            + Details(cell, cell.ErrorDetails.Keys.Except(cell.Errors.Keys))
        );
      }

      if (cell.OomKills is > 0)
      {
        notes.Add(
          $"{id}: the kernel OOM killer killed {cell.OomKills} process(es) in the container"
        );
      }

      if (
        cell.ContainerAfter is { } state
        && (!state.Running || state.OomKilled || state.RestartCount > 0)
      )
      {
        notes.Add(
          $"{id}: container ended the cell running={state.Running}, OOMKilled={state.OomKilled}, exit code {state.ExitCode}, restarts {state.RestartCount}"
        );
      }

      if (cell.PageCounts.Count > 1)
      {
        notes.Add(
          $"{id}: page counts varied between responses ({string.Join(", ", cell.PageCounts.Select(p => $"{p.Key} pages ×{p.Value}"))})"
        );
      }

      if (cell.Requests > 0 && cell.Succeeded == 0)
      {
        notes.Add($"{id}: no request succeeded");
      }
    }

    foreach (var fixture in run.Fixtures)
    {
      var modes = run
        .Settings.Targets.Select(t =>
          (
            Target: t,
            Pages: run.Cells.Where(c => c.Fixture == fixture.Name && c.Target == t)
              .SelectMany(c => c.PageCounts)
              .GroupBy(p => p.Key)
              .OrderByDescending(g => g.Sum(p => p.Value))
              .Select(g => g.Key)
              .FirstOrDefault()
          )
        )
        .Where(m => m.Pages is not null)
        .ToList();
      if (modes.Select(m => m.Pages).Distinct().Count() > 1)
      {
        notes.Add(
          $"{fixture.Name}: targets printed different page counts ({string.Join(", ", modes.Select(m => $"{m.Target} {m.Pages}"))})"
        );
      }
    }

    md.AppendLine("## Anomalies");
    md.AppendLine();
    if (notes.Count == 0)
    {
      md.AppendLine("None detected: no errors, OOM kills, container exits, or output mismatches.");
    }
    else
    {
      foreach (var note in notes)
      {
        md.AppendLine(Invariant, $"- {note}");
      }
    }

    md.AppendLine();
  }

  private static string Details(CellResult cell, IEnumerable<string> outcomes)
  {
    var details = outcomes
      .Where(cell.ErrorDetails.ContainsKey)
      .Select(o => $"{o}: “{cell.ErrorDetails[o].Replace("|", "\\|", StringComparison.Ordinal)}”")
      .ToList();
    return details.Count == 0 ? "" : $"; first error {string.Join("; ", details)}";
  }

  private static void AppendDetail(StringBuilder md, RunResult run)
  {
    md.AppendLine("## Detail");
    md.AppendLine();
    md.AppendLine(
      "| Target | Fixture | c | requests | ok | docs/s | p50 | p90 | p95 | p99 | max | phase s (drain) | warm-up ok | CPU cores (avg) | CPU s/doc | CPU % (peak) | peak mem | cgroup memory.peak | OOM kills | Docker load start → end |"
    );
    md.AppendLine(
      "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"
    );
    foreach (var cell in run.Cells)
    {
      var latency = cell.Latency;
      md.AppendLine(
        Invariant,
        $"| {cell.Target} | {cell.Fixture} | {cell.Concurrency} | {cell.Requests} | {cell.Succeeded} | {cell.DocsPerSecond:0.00} | {FormatMs(latency?.P50)} | {FormatMs(latency?.P90)} | {FormatMs(latency?.P95)} | {FormatMs(latency?.P99)} | {FormatMs(latency?.Max)} | {cell.MeasuredSeconds:0.0} ({cell.DrainSeconds:0.0}) | {cell.WarmupRequests - cell.WarmupFailures}/{cell.WarmupRequests} | {Format(cell.CpuCoresAverage, "0.00")} | {Format(cell.CpuSecondsPerDoc, "0.00")} | {Format(cell.CpuPercentPeakSampled, "0")} | {FormatBytes(cell.MemoryPeakSampledBytes)} | {FormatBytes(cell.MemoryPeakCgroupBytes)} | {cell.OomKills?.ToString(Invariant) ?? "—"} | {FirstLoad(cell.DockerLoadStart)} → {FirstLoad(cell.DockerLoadEnd)} |"
      );
    }

    md.AppendLine();
  }

  private static void AppendEnvironment(StringBuilder md, RunResult run)
  {
    var env = run.Environment;
    var settings = run.Settings;
    md.AppendLine("## Environment and settings");
    md.AppendLine();
    md.AppendLine(
      Invariant,
      $"- Run: {env.StartedAt:yyyy-MM-dd HH:mm:ss} → {env.FinishedAt:HH:mm:ss} UTC"
    );
    md.AppendLine(
      Invariant,
      $"- Host: {env.HostOs}; {env.HostCpu}; {env.HostLogicalCores} logical cores; {env.HostMemory}"
    );
    md.AppendLine(
      Invariant,
      $"- Host load average (1/5/15 min): {env.HostLoadStart} at start, {env.HostLoadEnd} at end"
    );
    md.AppendLine(Invariant, $"- Docker: {env.DockerServer}; Compose {env.DockerCompose}");
    md.AppendLine(
      Invariant,
      $"- Other containers running on the Docker host: {(env.OtherContainers.Count == 0 ? "none" : $"{env.OtherContainers.Count} ({string.Join(", ", env.OtherContainers)})")}"
    );
    md.AppendLine(
      Invariant,
      $"- Load driver: {env.DotnetRuntime}, on the host, HTTP/1.1, one connection per worker"
    );
    md.AppendLine(
      Invariant,
      $"- Limits per target: `cpus: {settings.Cpus}`, `mem_limit: {settings.Memory}`, `memswap_limit: {settings.Memory}`"
    );
    md.AppendLine(
      Invariant,
      $"- Concurrency levels: {string.Join(", ", settings.Concurrency)}; fixtures: {string.Join(", ", settings.Fixtures)}"
    );
    md.AppendLine();
    md.AppendLine("| Target | Image | Image ID | Browser |");
    md.AppendLine("|---|---|---|---|");
    foreach (var target in settings.Targets)
    {
      settings.Images.TryGetValue(target, out var image);
      env.ImageIds.TryGetValue(target, out var imageId);
      settings.TargetConfiguration.TryGetValue(target, out var configuration);
      md.AppendLine(
        Invariant,
        $"| {target} | `{image}` | `{imageId}` | {configuration?.BrowserVersion ?? "—"} |"
      );
    }

    md.AppendLine();
    foreach (var (target, configuration) in settings.TargetConfiguration)
    {
      var tuning = configuration
        .Environment.Where(e =>
          e.StartsWith("ReportsEngine__", StringComparison.Ordinal)
          || e.StartsWith("ASPNETCORE_", StringComparison.Ordinal)
          || e.StartsWith("Logging__", StringComparison.Ordinal)
          || e.StartsWith("GOTENBERG", StringComparison.Ordinal)
        )
        .ToList();
      md.AppendLine(
        Invariant,
        $"- {target} command: `{string.Join(' ', configuration.Command)}`; tuning environment: {(tuning.Count == 0 ? "none" : string.Join(", ", tuning.Select(e => $"`{e}`")))}"
      );
    }

    md.AppendLine();
  }

  private static string BuildCsv(RunResult run)
  {
    StringBuilder csv = new();
    csv.AppendLine(
      "target,fixture,concurrency,started_at,issue_s,measured_s,warmup_concurrency,warmup_requests,warmup_failures,requests,succeeded,docs_per_s,drain_s,p50_ms,p90_ms,p95_ms,p99_ms,max_ms,mean_ms,errors,page_counts,median_pdf_bytes,cpu_cores_avg,cpu_s_per_doc,cpu_percent_peak,mem_peak_sampled_bytes,mem_peak_cgroup_bytes,oom_kills,docker_load_start,docker_load_end,failure"
    );
    foreach (var c in run.Cells)
    {
      var l = c.Latency;
      string[] fields =
      [
        c.Target,
        c.Fixture,
        c.Concurrency.ToString(Invariant),
        c.StartedAt.ToString("O", Invariant),
        c.IssueSeconds.ToString(Invariant),
        c.MeasuredSeconds.ToString(Invariant),
        c.WarmupConcurrency.ToString(Invariant),
        c.WarmupRequests.ToString(Invariant),
        c.WarmupFailures.ToString(Invariant),
        c.Requests.ToString(Invariant),
        c.Succeeded.ToString(Invariant),
        c.DocsPerSecond.ToString(Invariant),
        c.DrainSeconds.ToString(Invariant),
        l?.P50.ToString(Invariant) ?? "",
        l?.P90.ToString(Invariant) ?? "",
        l?.P95.ToString(Invariant) ?? "",
        l?.P99.ToString(Invariant) ?? "",
        l?.Max.ToString(Invariant) ?? "",
        l?.Mean.ToString(Invariant) ?? "",
        string.Join(';', c.Errors.Select(e => $"{e.Key}={e.Value}")),
        string.Join(';', c.PageCounts.Select(p => $"{p.Key}={p.Value}")),
        c.MedianPdfBytes?.ToString(Invariant) ?? "",
        c.CpuCoresAverage?.ToString(Invariant) ?? "",
        c.CpuSecondsPerDoc?.ToString(Invariant) ?? "",
        c.CpuPercentPeakSampled?.ToString(Invariant) ?? "",
        c.MemoryPeakSampledBytes?.ToString(Invariant) ?? "",
        c.MemoryPeakCgroupBytes?.ToString(Invariant) ?? "",
        c.OomKills?.ToString(Invariant) ?? "",
        c.DockerLoadStart ?? "",
        c.DockerLoadEnd ?? "",
        c.Failure ?? "",
      ];
      csv.AppendLine(string.Join(',', fields.Select(Quote)));
    }

    return csv.ToString();

    static string Quote(string value) =>
      value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : value;
  }

  private static string FormatErrorCount(CellResult cell)
  {
    if (cell.Failure is not null)
    {
      return cell.Failure.StartsWith("skipped", StringComparison.Ordinal) ? "skipped" : "not run";
    }

    var errors = cell.Errors.Values.Sum();
    if (errors == 0)
    {
      return "0";
    }

    var top = cell.Errors.OrderByDescending(e => e.Value).First();
    return cell.Errors.Count == 1
      ? $"{errors} ({top.Key})"
      : $"{errors} ({top.Key}, +{cell.Errors.Count - 1} more)";
  }

  private static string FirstLoad(string? loadAverage) =>
    loadAverage?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "—";

  private static string FormatMs(double? value) =>
    value is { } ms ? ms.ToString(ms >= 100 ? "0" : "0.0", Invariant) : "—";

  private static string Format(double? value, string format) =>
    value?.ToString(format, Invariant) ?? "—";

  internal static string FormatBytes(long? bytes) =>
    bytes switch
    {
      null => "—",
      >= 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024 * 1024):0.00} GiB",
      >= 1024L * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MiB",
      >= 1024 => $"{bytes.Value / 1024d:0} KiB",
      _ => $"{bytes.Value} B",
    };
}
