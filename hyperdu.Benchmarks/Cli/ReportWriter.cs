using System.Globalization;
using System.Text;
using System.Text.Json;

namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Writes machine-readable results.json and GitHub-Flavored Markdown report.md.
/// </summary>
internal static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        BenchmarkReport report,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Combine(outputDirectory, "results.json");
        string mdPath = Path.Combine(outputDirectory, "report.md");

        string json = JsonSerializer.Serialize(report, JsonOptions);
        string markdown = FormatMarkdown(report);

        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(mdPath, markdown, Encoding.UTF8, cancellationToken);

        return (jsonPath, mdPath);
    }

    internal static string FormatMarkdown(BenchmarkReport report)
    {
        StringBuilder sb = new();
        BenchmarkEnvironment env = report.Environment;

        sb.AppendLine("# hyperdu Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"- **Suite**: `{report.SuiteName}`");
        sb.AppendLine($"- **Timestamp (UTC)**: {env.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **Git Commit**: `{env.GitCommit}`");
        sb.AppendLine($"- **OS / Arch**: {env.OsDescription} ({env.Architecture})");
        sb.AppendLine($"- **CPU**: {env.CpuModel} ({env.LogicalCores} logical cores)");
        sb.AppendLine($"- **Reference `du`**: `{env.ReferenceDuVersion}`");
        sb.AppendLine($"- **Timing Engine**: `{env.TimingEngine}` ({(env.ColdCache ? "cold cache" : "warm cache")})");
        sb.AppendLine();

        sb.AppendLine("## Datasets");
        sb.AppendLine();
        sb.AppendLine("| Fixture | Type | Files | Directories | Description |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | :--- |");
        foreach (FixtureSpec fixture in report.Fixtures)
        {
            string kind = fixture.IsSynthetic ? "synthetic" : "custom path";
            string filesText = fixture.IsSynthetic ? fixture.ExpectedFiles.ToString("N0", CultureInfo.InvariantCulture) : "n/a";
            string dirsText = fixture.IsSynthetic ? fixture.ExpectedDirs.ToString("N0", CultureInfo.InvariantCulture) : "n/a";
            sb.AppendLine($"| `{fixture.Name}` | {kind} | {filesText} | {dirsText} | {fixture.Description} |");
        }
        sb.AppendLine();

        AppendOverallPerformanceComparison(sb, report.Scenarios);

        sb.AppendLine("## Summary vs Reference `du`");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Ref `du` Mean (ms) | `hyperdu` Mean (ms) | Speedup | `hyperdu` Peak RSS (MiB) | Ref `du` Peak RSS (MiB) | Parity |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | :--- |");

        foreach (ScenarioResult scenario in report.Scenarios)
        {
            BenchmarkEntryResult? refEntry = scenario.Entries.FirstOrDefault(e => e.IsReferenceDu);
            BenchmarkEntryResult? hyperduEntry = PickHeadlineHyperduEntry(scenario.Entries);

            string refMean = FormatTimingSimple(refEntry?.Timing);
            string hyperMean = hyperduEntry == null
                ? "n/a"
                : hyperduEntry.IsPending
                    ? (hyperduEntry.PendingReason ?? "pending")
                    : FormatTimingSimple(hyperduEntry.Timing);
            string speedup = FormatSpeedup(hyperduEntry?.SpeedupVsReferenceDu);
            string hyperRss = FormatRssMib(hyperduEntry?.Probe?.PeakRssKb);
            string refRss = FormatRssMib(refEntry?.Probe?.PeakRssKb);
            string parity = hyperduEntry == null
                ? "n/a"
                : hyperduEntry.IsPending
                    ? (hyperduEntry.PendingReason ?? "pending")
                    : FormatParity(hyperduEntry.Probe);

            sb.AppendLine($"| `{scenario.Id}` | {refMean} | {hyperMean} | {speedup} | {hyperRss} | {refRss} | {parity} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Detailed Scenario Results");
        sb.AppendLine();

        foreach (ScenarioResult scenario in report.Scenarios)
        {
            sb.AppendLine($"### {scenario.DisplayName} (`{scenario.Id}`)");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(scenario.PendingNote))
            {
                sb.AppendLine($"> Note: {scenario.PendingNote}");
                sb.AppendLine();
            }

            sb.AppendLine("| Tool | Mean ± StdDev (ms) | Median (ms) | Speedup vs Ref | User / Sys CPU (ms) | TTFL (ms) | Head-100 Exit (ms) | Peak RSS (MiB) | Lines | Parity |");
            sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |");

            foreach (BenchmarkEntryResult entry in scenario.Entries)
            {
                if (entry.IsPending)
                {
                    string reason = entry.PendingReason ?? "Pending";
                    sb.AppendLine($"| {entry.ToolName} | {reason} | - | - | - | - | - | - | - | {reason} |");
                    continue;
                }

                string meanStd = FormatMeanStdDev(entry.Timing);
                string median = entry.Timing != null ? entry.Timing.MedianMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
                string speedup = entry.IsReferenceDu ? "1.00x (ref)" : FormatSpeedup(entry.SpeedupVsReferenceDu);
                string cpu = entry.Timing is { UserCpuMs: double userMs, SystemCpuMs: double sysMs }
                    ? $"{userMs.ToString("F2", CultureInfo.InvariantCulture)} / {sysMs.ToString("F2", CultureInfo.InvariantCulture)}"
                    : "-";
                string ttfl = entry.Probe?.TtflMs is double t ? t.ToString("F2", CultureInfo.InvariantCulture) : "-";
                string head100 = entry.Probe?.EarlyExitHead100Ms is double h ? h.ToString("F2", CultureInfo.InvariantCulture) : "-";
                string rss = FormatRssMib(entry.Probe?.PeakRssKb);
                string lines = entry.Probe != null ? entry.Probe.OutputLines.ToString("N0", CultureInfo.InvariantCulture) : "-";
                string parity = FormatParity(entry.Probe);

                sb.AppendLine($"| {entry.ToolName} | {meanStd} | {median} | {speedup} | {cpu} | {ttfl} | {head100} | {rss} | {lines} | {parity} |");
            }

            sb.AppendLine();
        }

        AppendOverallBenchmarkScore(sb, report.Scenarios);

        return sb.ToString();
    }

    private sealed record CategoryMetrics(
        string Name,
        int TotalScenarios,
        int ActiveScenarios,
        int ParityPassed,
        int PendingScenarios,
        double RefTotalMs,
        double CandidateTotalMs,
        double? AvgSpeedup,
        double? GeoMeanSpeedup,
        double? AvgHyperduRssMib,
        double? AvgRefRssMib);

    private static readonly string[] CategoryOrder =
    [
        "Startup Latency",
        "Summary (-s / -sh)",
        "Apparent Size (--apparent-size)",
        "Depth-Limited (-d 1)",
        "Full Listing (-a)",
        "Traversal & Filtering"
    ];

    private static void AppendOverallPerformanceComparison(StringBuilder sb, IReadOnlyList<ScenarioResult> scenarios)
    {
        sb.AppendLine("## Overall Performance Comparison");
        sb.AppendLine();
        sb.AppendLine("Average speedup per category compares each headline `hyperdu` run (`hyperdu du`, falling back to `hyperdu` native) against the reference `du` (`> 1.00x` means `hyperdu` is faster).");
        sb.AppendLine();

        List<CategoryMetrics> byCategory = scenarios
            .GroupBy(ClassifyWorkloadCategory)
            .OrderBy(g =>
            {
                int idx = Array.IndexOf(CategoryOrder, g.Key);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ComputeMetrics(g.Key, g.ToList(), PickHeadlineHyperduEntry))
            .ToList();

        sb.AppendLine("### Average Speedup by Workload Category");
        sb.AppendLine();
        AppendMetricsTable(sb, "Workload Category", byCategory);
        sb.AppendLine();

        List<CategoryMetrics> byDataset = scenarios
            .GroupBy(s => s.FixtureName)
            .Select(g => ComputeMetrics(g.Key, g.ToList(), PickHeadlineHyperduEntry))
            .ToList();

        sb.AppendLine("### Average Speedup by Dataset");
        sb.AppendLine();
        AppendMetricsTable(sb, "Dataset", byDataset);
        sb.AppendLine();
    }

    private static void AppendMetricsTable(StringBuilder sb, string labelColumn, IReadOnlyList<CategoryMetrics> rows)
    {
        sb.AppendLine($"| {labelColumn} | Active / Total | Ref `du` Total (ms) | `hyperdu` Total (ms) | Avg Speedup | GeoMean Speedup | Avg `hyperdu` RSS (MiB) | Avg Ref RSS (MiB) |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (CategoryMetrics m in rows)
        {
            string activeCol = $"{m.ActiveScenarios} / {m.TotalScenarios}";
            string refTotal = m.ActiveScenarios > 0 ? m.RefTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string candTotal = m.ActiveScenarios > 0 ? m.CandidateTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string avgSpd = FormatSpeedup(m.AvgSpeedup);
            string geoSpd = FormatSpeedup(m.GeoMeanSpeedup);
            string hyperRss = m.AvgHyperduRssMib.HasValue ? m.AvgHyperduRssMib.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string refRss = m.AvgRefRssMib.HasValue ? m.AvgRefRssMib.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";

            sb.AppendLine($"| {m.Name} | {activeCol} | {refTotal} | {candTotal} | {avgSpd} | {geoSpd} | {hyperRss} | {refRss} |");
        }
    }

    private static void AppendOverallBenchmarkScore(StringBuilder sb, IReadOnlyList<ScenarioResult> scenarios)
    {
        CategoryMetrics headline = ComputeMetrics("Headline", scenarios, PickHeadlineHyperduEntry);
        CategoryMetrics? duPersona = ComputeMetricsForTool("hyperdu du", scenarios, "hyperdu-du-auto");
        CategoryMetrics? nativeTool = ComputeMetricsForTool("hyperdu (native, auto-threads)", scenarios, "hyperdu-native");

        sb.AppendLine("## Overall Benchmark Score");
        sb.AppendLine();
        sb.AppendLine("Score Index normalizes the Reference `du` baseline to `100` (`round(GeoMean Speedup * 100)`); higher is faster.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Active / Total | Avg Speedup | GeoMean Speedup | Score Index (Ref = 100) | Ref Total (ms) | `hyperdu` Total (ms) | Parity Passed |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | :--- |");

        AppendScoreRow(sb, "Overall Headline (`hyperdu`)", headline);
        if (duPersona is { TotalScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu du` Persona", duPersona);
        if (nativeTool is { TotalScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu` Native Engine (Tier 2)", nativeTool);

        sb.AppendLine();

        string scoreText = headline.GeoMeanSpeedup.HasValue
            ? $"{Math.Round(headline.GeoMeanSpeedup.Value * 100.0).ToString("F0", CultureInfo.InvariantCulture)} / 100 baseline ({FormatSpeedup(headline.GeoMeanSpeedup)} geomean, {FormatSpeedup(headline.AvgSpeedup)} mean across {headline.ActiveScenarios}/{headline.TotalScenarios} scenarios)"
            : "n/a (no active scenarios)";

        sb.AppendLine($"- **Overall Score**: **{scoreText}**");
        sb.AppendLine($"- **Parity Summary**: {headline.ParityPassed} / {headline.ActiveScenarios} active matched ({headline.PendingScenarios} pending)");
        sb.AppendLine();
    }

    private static void AppendScoreRow(StringBuilder sb, string label, CategoryMetrics m)
    {
        string activeCol = $"{m.ActiveScenarios} / {m.TotalScenarios}";
        string avgSpd = FormatSpeedup(m.AvgSpeedup);
        string geoSpd = FormatSpeedup(m.GeoMeanSpeedup);
        string scoreIdx = m.GeoMeanSpeedup.HasValue
            ? Math.Round(m.GeoMeanSpeedup.Value * 100.0).ToString("F0", CultureInfo.InvariantCulture)
            : "-";
        string refTotal = m.ActiveScenarios > 0 ? m.RefTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
        string candTotal = m.ActiveScenarios > 0 ? m.CandidateTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
        string parityCol = m.ActiveScenarios > 0
            ? $"{m.ParityPassed} / {m.ActiveScenarios} ({(100.0 * m.ParityPassed / m.ActiveScenarios).ToString("F1", CultureInfo.InvariantCulture)}%)"
            : $"0 / 0 ({m.PendingScenarios} pending)";

        sb.AppendLine($"| {label} | {activeCol} | {avgSpd} | {geoSpd} | {scoreIdx} | {refTotal} | {candTotal} | {parityCol} |");
    }

    private static CategoryMetrics? ComputeMetricsForTool(string name, IReadOnlyList<ScenarioResult> scenarios, string toolId)
    {
        List<ScenarioResult> relevant = scenarios
            .Where(s => s.Entries.Any(e => e.ToolId == toolId))
            .ToList();
        return relevant.Count == 0
            ? null
            : ComputeMetrics(name, relevant, entries => entries.FirstOrDefault(e => e.ToolId == toolId));
    }

    private static CategoryMetrics ComputeMetrics(
        string name,
        IReadOnlyList<ScenarioResult> scenarios,
        Func<IReadOnlyList<BenchmarkEntryResult>, BenchmarkEntryResult?> selector)
    {
        int active = 0;
        int passed = 0;
        int pending = 0;
        double refSum = 0;
        double candSum = 0;
        List<double> speedups = [];
        List<double> hyperRss = [];
        List<double> refRss = [];

        foreach (ScenarioResult scenario in scenarios)
        {
            BenchmarkEntryResult? refEntry = scenario.Entries.FirstOrDefault(e => e.IsReferenceDu);
            BenchmarkEntryResult? candEntry = selector(scenario.Entries);

            if (candEntry == null)
                continue;

            if (candEntry.IsPending)
            {
                pending++;
                continue;
            }

            if (candEntry.Probe?.ParityMatched == true)
                passed++;

            if (candEntry.SpeedupVsReferenceDu is double spd && spd > 0 &&
                refEntry?.Timing is { MeanMs: double rMs } &&
                candEntry.Timing is { MeanMs: double cMs })
            {
                active++;
                refSum += rMs;
                candSum += cMs;
                speedups.Add(spd);

                if (candEntry.Probe?.PeakRssKb is long hKb && hKb > 0)
                    hyperRss.Add(hKb / 1024.0);
                if (refEntry.Probe?.PeakRssKb is long rKb && rKb > 0)
                    refRss.Add(rKb / 1024.0);
            }
        }

        double? avgSpeedup = speedups.Count > 0 ? speedups.Average() : null;
        double? geoSpeedup = speedups.Count > 0
            ? Math.Exp(speedups.Sum(s => Math.Log(s)) / speedups.Count)
            : null;
        double? avgHyperRss = hyperRss.Count > 0 ? hyperRss.Average() : null;
        double? avgRefRss = refRss.Count > 0 ? refRss.Average() : null;

        return new CategoryMetrics(
            Name: name,
            TotalScenarios: scenarios.Count,
            ActiveScenarios: active,
            ParityPassed: passed,
            PendingScenarios: pending,
            RefTotalMs: refSum,
            CandidateTotalMs: candSum,
            AvgSpeedup: avgSpeedup,
            GeoMeanSpeedup: geoSpeedup,
            AvgHyperduRssMib: avgHyperRss,
            AvgRefRssMib: avgRefRss);
    }

    internal static string ClassifyWorkloadCategory(ScenarioResult scenario)
    {
        if (string.Equals(scenario.FixtureName, "tiny-startup", StringComparison.Ordinal))
            return "Startup Latency";

        int slash = scenario.Id.LastIndexOf('/');
        string suffix = slash >= 0 ? scenario.Id[(slash + 1)..] : scenario.Id;

        return suffix switch
        {
            "summarize" or "du-s" or "du-sh" => "Summary (-s / -sh)",
            "apparent-summarize" or "du-apparent" => "Apparent Size (--apparent-size)",
            "depth-1" or "du-d1" => "Depth-Limited (-d 1)",
            "du-a" => "Full Listing (-a)",
            _ => "Traversal & Filtering"
        };
    }

    private static BenchmarkEntryResult? PickHeadlineHyperduEntry(IReadOnlyList<BenchmarkEntryResult> entries) =>
        entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-auto" && !e.IsPending)
        ?? entries.FirstOrDefault(e => e.ToolId == "hyperdu-native" && !e.IsPending)
        ?? entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-auto")
        ?? entries.FirstOrDefault(e => e.ToolId == "hyperdu-native");

    private static string FormatTimingSimple(TimingStats? timing) =>
        timing == null ? "-" : timing.MeanMs.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatMeanStdDev(TimingStats? timing)
    {
        if (timing == null)
            return "-";

        string mean = timing.MeanMs.ToString("F2", CultureInfo.InvariantCulture);
        if (!timing.StdDevMs.HasValue)
            return mean;

        return $"{mean} +/- {timing.StdDevMs.Value.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    private static string FormatSpeedup(double? speedup) =>
        speedup.HasValue ? $"{speedup.Value.ToString("F2", CultureInfo.InvariantCulture)}x" : "-";

    private static string FormatRssMib(long? rssKb)
    {
        if (!rssKb.HasValue || rssKb.Value <= 0)
            return "-";

        return (rssKb.Value / 1024.0).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string FormatParity(ProbeResult? probe)
    {
        if (probe == null)
            return "-";

        return probe.ParityMatched ? $"PASS ({probe.ParityNote})" : $"FAIL ({probe.ParityNote})";
    }
}
