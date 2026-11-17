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

        Dictionary<string, long> fixtureEntries = BuildFixtureEntryMap(report.Fixtures);

        AppendOverallPerformanceComparison(sb, report.Scenarios, fixtureEntries);

        sb.AppendLine("## Summary vs Reference `du`");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Ref `du` Median (ms) | `hyperdu` Median (ms) | Speedup | StdDev (%) | `hyperdu` Peak RSS (MiB) | Ref `du` Peak RSS (MiB) | Parity |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | :--- |");

        foreach (ScenarioResult scenario in report.Scenarios)
        {
            BenchmarkEntryResult? refEntry = scenario.Entries.FirstOrDefault(e => e.IsReferenceDu);
            BenchmarkEntryResult? hyperduEntry = PickHeadlineHyperduEntry(scenario.Entries);

            string refMedian = FormatTimingMedian(refEntry?.Timing);
            string hyperMedian = hyperduEntry == null
                ? "n/a"
                : hyperduEntry.IsPending
                    ? (hyperduEntry.PendingReason ?? "pending")
                    : FormatTimingMedian(hyperduEntry.Timing);
            string speedup = FormatSpeedup(hyperduEntry?.SpeedupVsReferenceDu);
            string stdDevPct = FormatStdDevPct(hyperduEntry?.Timing);
            string hyperRss = FormatRssMib(hyperduEntry?.Probe?.PeakRssKb);
            string refRss = FormatRssMib(refEntry?.Probe?.PeakRssKb);
            string parity = hyperduEntry == null
                ? "n/a"
                : hyperduEntry.IsPending
                    ? (hyperduEntry.PendingReason ?? "pending")
                    : FormatParity(hyperduEntry.Probe);

            sb.AppendLine($"| `{scenario.Id}` | {refMedian} | {hyperMedian} | {speedup} | {stdDevPct} | {hyperRss} | {refRss} | {parity} |");
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

            sb.AppendLine("| Tool | Mean +/- StdDev (ms) | Median (ms) | Speedup vs Ref | User / Sys CPU (ms) | TTFL (ms) | Head-100 Exit (ms) | Peak RSS (MiB) | Lines | Parity |");
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

        AppendOverallBenchmarkScore(sb, report.Scenarios, fixtureEntries);

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
        double? TimeWeightedSpeedup,
        double? MedianSpeedup,
        double? AvgSpeedup,
        double? GeoMeanSpeedup,
        double? ThroughputKEntriesPerSec,
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

    private static Dictionary<string, long> BuildFixtureEntryMap(IReadOnlyList<FixtureSpec> fixtures)
    {
        Dictionary<string, long> map = new(StringComparer.Ordinal);
        foreach (FixtureSpec f in fixtures)
        {
            long total = (long)f.ExpectedFiles + f.ExpectedDirs;
            if (f.IsSynthetic && total > 0)
                map.TryAdd(f.Name, total);
        }

        return map;
    }

    private static void AppendOverallPerformanceComparison(
        StringBuilder sb,
        IReadOnlyList<ScenarioResult> scenarios,
        IReadOnlyDictionary<string, long> fixtureEntries)
    {
        sb.AppendLine("## Overall Performance Comparison");
        sb.AppendLine();
        sb.AppendLine("Speedup per category compares each headline `hyperdu` run (`hyperdu du`, falling back to `hyperdu` native) against the reference `du` using median runtimes (`> 1.00x` means `hyperdu` is faster).");
        sb.AppendLine();

        List<CategoryMetrics> byCategory = scenarios
            .GroupBy(ClassifyWorkloadCategory)
            .OrderBy(g =>
            {
                int idx = Array.IndexOf(CategoryOrder, g.Key);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ComputeMetrics(g.Key, g.ToList(), PickHeadlineHyperduEntry, fixtureEntries))
            .ToList();

        sb.AppendLine("### Average Speedup by Workload Category");
        sb.AppendLine();
        AppendMetricsTable(sb, "Workload Category", byCategory);
        sb.AppendLine();

        List<CategoryMetrics> byDataset = scenarios
            .GroupBy(s => s.FixtureName)
            .Select(g => ComputeMetrics(g.Key, g.ToList(), PickHeadlineHyperduEntry, fixtureEntries))
            .ToList();

        sb.AppendLine("### Average Speedup by Dataset");
        sb.AppendLine();
        AppendMetricsTable(sb, "Dataset", byDataset);
        sb.AppendLine();
    }

    private static void AppendMetricsTable(StringBuilder sb, string labelColumn, IReadOnlyList<CategoryMetrics> rows)
    {
        sb.AppendLine($"| {labelColumn} | Active / Total | Ref `du` Total (ms) | `hyperdu` Total (ms) | Time-Wtd Speedup | Avg Speedup | GeoMean Speedup | Throughput (k/s) | Avg `hyperdu` RSS (MiB) | Avg Ref RSS (MiB) |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (CategoryMetrics m in rows)
        {
            string activeCol = $"{m.ActiveScenarios} / {m.TotalScenarios}";
            string refTotal = m.ActiveScenarios > 0 ? m.RefTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string candTotal = m.ActiveScenarios > 0 ? m.CandidateTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string timeWtd = FormatSpeedup(m.TimeWeightedSpeedup);
            string avgSpd = FormatSpeedup(m.AvgSpeedup);
            string geoSpd = FormatSpeedup(m.GeoMeanSpeedup);
            string throughput = m.ThroughputKEntriesPerSec.HasValue
                ? m.ThroughputKEntriesPerSec.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "-";
            string hyperRss = m.AvgHyperduRssMib.HasValue ? m.AvgHyperduRssMib.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";
            string refRss = m.AvgRefRssMib.HasValue ? m.AvgRefRssMib.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";

            sb.AppendLine($"| {m.Name} | {activeCol} | {refTotal} | {candTotal} | {timeWtd} | {avgSpd} | {geoSpd} | {throughput} | {hyperRss} | {refRss} |");
        }
    }

    private static void AppendOverallBenchmarkScore(
        StringBuilder sb,
        IReadOnlyList<ScenarioResult> scenarios,
        IReadOnlyDictionary<string, long> fixtureEntries)
    {
        List<ScenarioResult> workloadScenarios = scenarios.Where(s => !IsStartupScenario(s)).ToList();
        if (workloadScenarios.Count == 0)
            workloadScenarios = scenarios.ToList();

        List<ScenarioResult> tier2Workloads = workloadScenarios
            .Where(s => string.Equals(s.Tier, nameof(ComparatorTier.Tier2Workload), StringComparison.Ordinal))
            .ToList();
        List<ScenarioResult> tier1Parity = workloadScenarios
            .Where(s => string.Equals(s.Tier, nameof(ComparatorTier.Tier1Parity), StringComparison.Ordinal))
            .ToList();

        CategoryMetrics categoryBalanced = ComputeCategoryBalancedMetrics(
            "Workload Score (Category-Balanced, excl. startup)",
            workloadScenarios,
            PickHeadlineHyperduEntry,
            fixtureEntries);

        CategoryMetrics? tier2Du = ComputeMetricsForTool(
            "`hyperdu du` (Tier 2 Core Workloads)",
            tier2Workloads,
            "hyperdu-du-auto",
            fixtureEntries);
        CategoryMetrics? tier1Du = ComputeMetricsForTool(
            "`hyperdu du` (Tier 1 Parity Suite)",
            tier1Parity,
            "hyperdu-du-auto",
            fixtureEntries);
        CategoryMetrics? du1t = ComputeMetricsForTool(
            "`hyperdu du` (1-thread)",
            workloadScenarios,
            "hyperdu-du-1t",
            fixtureEntries);
        CategoryMetrics? du4t = ComputeMetricsForTool(
            "`hyperdu du` (4-threads)",
            workloadScenarios,
            "hyperdu-du-4t",
            fixtureEntries);
        CategoryMetrics? nativeTool = ComputeMetricsForTool(
            "`hyperdu` Native Engine (Tier 2, excl. startup)",
            workloadScenarios,
            "hyperdu-native",
            fixtureEntries);
        CategoryMetrics? native1t = ComputeMetricsForTool(
            "`hyperdu` Native Engine (1-thread)",
            workloadScenarios,
            "hyperdu-1t",
            fixtureEntries);

        sb.AppendLine("## Overall Benchmark Score");
        sb.AppendLine();
        sb.AppendLine("Score Index normalizes the Reference `du` baseline to `100` (`round(GeoMean Speedup * 100)`); higher is faster. Workload rows exclude `tiny-startup` so fixed process startup is tracked separately from traversal throughput.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Active / Total | Time-Wtd Speedup | Median Speedup | Avg Speedup | GeoMean Speedup | Score Index (Ref = 100) | Ref Total (ms) | Cand Total (ms) | Avg RSS (MiB) | Parity Passed |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |");

        bool hasActiveDu = (tier2Du?.ActiveScenarios ?? 0) > 0 || (tier1Du?.ActiveScenarios ?? 0) > 0;
        if (hasActiveDu)
        {
            AppendScoreRow(sb, "Workload Score (Category-Balanced)", categoryBalanced);
        }
        else
        {
            CategoryMetrics fallbackHeadline = ComputeMetrics("Overall Headline (`hyperdu`)", workloadScenarios, PickHeadlineHyperduEntry, fixtureEntries);
            AppendScoreRow(sb, "Overall Headline (`hyperdu`)", fallbackHeadline);
        }

        if (tier2Du is { TotalScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu du` (Tier 2 Workloads)", tier2Du);
        if (tier1Du is { TotalScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu du` (Tier 1 Parity Suite)", tier1Du);
        if (du1t is { ActiveScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu du` (1-thread)", du1t);
        if (du4t is { ActiveScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu du` (4-threads)", du4t);
        if (nativeTool is { TotalScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu` Native Engine (Tier 2)", nativeTool);
        if (native1t is { ActiveScenarios: > 0 })
            AppendScoreRow(sb, "`hyperdu` Native Engine (1-thread)", native1t);

        AppendOptionalComparatorRow(sb, workloadScenarios, fixtureEntries, "uutils-du", "`uutils du`");
        AppendOptionalComparatorRow(sb, workloadScenarios, fixtureEntries, "dust", "`dust`");
        AppendOptionalComparatorRow(sb, workloadScenarios, fixtureEntries, "gdu", "`gdu` (Go)");
        AppendOptionalComparatorRow(sb, workloadScenarios, fixtureEntries, "ncdu", "`ncdu`");

        sb.AppendLine();

        CategoryMetrics allParity = ComputeMetrics("Parity", scenarios, PickHeadlineHyperduEntry, fixtureEntries);
        string scoreText = categoryBalanced.GeoMeanSpeedup.HasValue
            ? $"{Math.Round(categoryBalanced.GeoMeanSpeedup.Value * 100.0).ToString("F0", CultureInfo.InvariantCulture)} / 100 baseline ({FormatSpeedup(categoryBalanced.GeoMeanSpeedup)} category-balanced geomean, {FormatSpeedup(categoryBalanced.TimeWeightedSpeedup)} time-weighted across {categoryBalanced.ActiveScenarios}/{categoryBalanced.TotalScenarios} workload scenarios)"
            : "n/a (no active scenarios)";

        sb.AppendLine($"- **Overall Score**: **{scoreText}**");
        sb.AppendLine($"- **Parity Summary**: {allParity.ParityPassed} / {allParity.ActiveScenarios} active matched ({allParity.PendingScenarios} pending)");

        AppendStartupSummaryBullet(sb, scenarios);
        AppendThreadScalingBullet(sb, workloadScenarios);
        sb.AppendLine();
    }

    private static void AppendOptionalComparatorRow(
        StringBuilder sb,
        IReadOnlyList<ScenarioResult> scenarios,
        IReadOnlyDictionary<string, long> fixtureEntries,
        string toolId,
        string label)
    {
        CategoryMetrics? metrics = ComputeMetricsForTool(label, scenarios, toolId, fixtureEntries);
        if (metrics is { ActiveScenarios: > 0 })
            AppendScoreRow(sb, label, metrics);
    }

    private static void AppendStartupSummaryBullet(StringBuilder sb, IReadOnlyList<ScenarioResult> scenarios)
    {
        List<ScenarioResult> startup = scenarios.Where(IsStartupScenario).ToList();
        if (startup.Count == 0)
            return;

        ScenarioResult first = startup[0];
        BenchmarkEntryResult? refEntry = first.Entries.FirstOrDefault(e => e.IsReferenceDu);
        BenchmarkEntryResult? duEntry = first.Entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-auto" && !e.IsPending);
        BenchmarkEntryResult? nativeEntry = first.Entries.FirstOrDefault(e => e.ToolId == "hyperdu-native" && !e.IsPending);

        List<string> parts = [];
        if (refEntry?.Timing != null)
            parts.Add($"ref `du` {FormatTimingMedian(refEntry.Timing)} ms");
        if (duEntry?.Timing != null)
            parts.Add($"`hyperdu du` {FormatTimingMedian(duEntry.Timing)} ms ({FormatSpeedup(duEntry.SpeedupVsReferenceDu)})");
        if (nativeEntry?.Timing != null)
            parts.Add($"`hyperdu --no-ui` {FormatTimingMedian(nativeEntry.Timing)} ms ({FormatSpeedup(nativeEntry.SpeedupVsReferenceDu)})");

        if (parts.Count > 0)
            sb.AppendLine($"- **Startup Latency (`{first.Id}`)**: {string.Join(", ", parts)}");
    }

    private static void AppendThreadScalingBullet(StringBuilder sb, IReadOnlyList<ScenarioResult> workloadScenarios)
    {
        double sum1t = 0;
        double sumAuto = 0;
        int count = 0;
        foreach (ScenarioResult s in workloadScenarios)
        {
            BenchmarkEntryResult? e1t = s.Entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-1t" && !e.IsPending);
            BenchmarkEntryResult? eAuto = s.Entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-auto" && !e.IsPending);
            if (e1t?.Timing != null && eAuto?.Timing != null)
            {
                double t1 = RepresentativeMs(e1t.Timing);
                double tA = RepresentativeMs(eAuto.Timing);
                if (t1 > 0 && tA > 0)
                {
                    sum1t += t1;
                    sumAuto += tA;
                    count++;
                }
            }
        }

        if (count > 0 && sumAuto > 0)
        {
            double scaling = sum1t / sumAuto;
            sb.AppendLine($"- **Parallel Scaling (`hyperdu du` 1-thread vs default)**: {scaling.ToString("F2", CultureInfo.InvariantCulture)}x across {count} shared scenarios ({sum1t.ToString("F2", CultureInfo.InvariantCulture)} ms -> {sumAuto.ToString("F2", CultureInfo.InvariantCulture)} ms)");
        }
    }

    private static CategoryMetrics ComputeCategoryBalancedMetrics(
        string name,
        IReadOnlyList<ScenarioResult> workloadScenarios,
        Func<IReadOnlyList<BenchmarkEntryResult>, BenchmarkEntryResult?> selector,
        IReadOnlyDictionary<string, long> fixtureEntries)
    {
        CategoryMetrics overall = ComputeMetrics(name, workloadScenarios, selector, fixtureEntries);
        List<double> categoryGeoMeans = workloadScenarios
            .GroupBy(ClassifyWorkloadCategory)
            .Select(g => ComputeMetrics(g.Key, g.ToList(), selector, fixtureEntries).GeoMeanSpeedup)
            .Where(g => g is > 0)
            .Select(g => g!.Value)
            .ToList();

        double? balancedGeo = categoryGeoMeans.Count > 0
            ? Math.Exp(categoryGeoMeans.Sum(g => Math.Log(g)) / categoryGeoMeans.Count)
            : overall.GeoMeanSpeedup;

        return overall with { GeoMeanSpeedup = balancedGeo };
    }

    private static void AppendScoreRow(StringBuilder sb, string label, CategoryMetrics m)
    {
        string activeCol = $"{m.ActiveScenarios} / {m.TotalScenarios}";
        string timeWtd = FormatSpeedup(m.TimeWeightedSpeedup);
        string medSpd = FormatSpeedup(m.MedianSpeedup);
        string avgSpd = FormatSpeedup(m.AvgSpeedup);
        string geoSpd = FormatSpeedup(m.GeoMeanSpeedup);
        string scoreIdx = m.GeoMeanSpeedup.HasValue
            ? Math.Round(m.GeoMeanSpeedup.Value * 100.0).ToString("F0", CultureInfo.InvariantCulture)
            : "-";
        string refTotal = m.ActiveScenarios > 0 ? m.RefTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
        string candTotal = m.ActiveScenarios > 0 ? m.CandidateTotalMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
        string avgRss = m.AvgHyperduRssMib.HasValue ? m.AvgHyperduRssMib.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";
        string parityCol = m.ActiveScenarios > 0
            ? $"{m.ParityPassed} / {m.ActiveScenarios} ({(100.0 * m.ParityPassed / m.ActiveScenarios).ToString("F1", CultureInfo.InvariantCulture)}%)"
            : $"0 / 0 ({m.PendingScenarios} pending)";

        sb.AppendLine($"| {label} | {activeCol} | {timeWtd} | {medSpd} | {avgSpd} | {geoSpd} | {scoreIdx} | {refTotal} | {candTotal} | {avgRss} | {parityCol} |");
    }

    private static CategoryMetrics? ComputeMetricsForTool(
        string name,
        IReadOnlyList<ScenarioResult> scenarios,
        string toolId,
        IReadOnlyDictionary<string, long> fixtureEntries)
    {
        List<ScenarioResult> relevant = scenarios
            .Where(s => s.Entries.Any(e => e.ToolId == toolId))
            .ToList();
        return relevant.Count == 0
            ? null
            : ComputeMetrics(name, relevant, entries => entries.FirstOrDefault(e => e.ToolId == toolId), fixtureEntries);
    }

    private static CategoryMetrics ComputeMetrics(
        string name,
        IReadOnlyList<ScenarioResult> scenarios,
        Func<IReadOnlyList<BenchmarkEntryResult>, BenchmarkEntryResult?> selector,
        IReadOnlyDictionary<string, long> fixtureEntries)
    {
        int active = 0;
        int passed = 0;
        int pending = 0;
        double refSum = 0;
        double candSum = 0;
        long totalEntries = 0;
        double entryTimeMs = 0;
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
                refEntry?.Timing != null &&
                candEntry.Timing != null)
            {
                double rMs = RepresentativeMs(refEntry.Timing);
                double cMs = RepresentativeMs(candEntry.Timing);
                if (rMs <= 0 || cMs <= 0)
                    continue;

                active++;
                refSum += rMs;
                candSum += cMs;
                speedups.Add(spd);

                if (fixtureEntries.TryGetValue(scenario.FixtureName, out long entriesCount) && entriesCount > 0)
                {
                    totalEntries += entriesCount;
                    entryTimeMs += cMs;
                }

                if (candEntry.Probe?.PeakRssKb is long hKb && hKb > 0)
                    hyperRss.Add(hKb / 1024.0);
                if (refEntry.Probe?.PeakRssKb is long rKb && rKb > 0)
                    refRss.Add(rKb / 1024.0);
            }
        }

        double? timeWtd = candSum > 0 ? refSum / candSum : null;
        double? medianSpeedup = ComputeMedian(speedups);
        double? avgSpeedup = speedups.Count > 0 ? speedups.Average() : null;
        double? geoSpeedup = speedups.Count > 0
            ? Math.Exp(speedups.Sum(s => Math.Log(s)) / speedups.Count)
            : null;
        double? throughputK = totalEntries > 0 && entryTimeMs > 0
            ? (totalEntries / (entryTimeMs / 1000.0)) / 1000.0
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
            TimeWeightedSpeedup: timeWtd,
            MedianSpeedup: medianSpeedup,
            AvgSpeedup: avgSpeedup,
            GeoMeanSpeedup: geoSpeedup,
            ThroughputKEntriesPerSec: throughputK,
            AvgHyperduRssMib: avgHyperRss,
            AvgRefRssMib: avgRefRss);
    }

    private static double RepresentativeMs(TimingStats timing) =>
        timing.MedianMs > 0 ? timing.MedianMs : timing.MeanMs;

    private static double? ComputeMedian(List<double> values)
    {
        if (values.Count == 0)
            return null;

        double[] sorted = [.. values];
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static bool IsStartupScenario(ScenarioResult scenario) =>
        string.Equals(scenario.FixtureName, "tiny-startup", StringComparison.Ordinal);

    internal static string ClassifyWorkloadCategory(ScenarioResult scenario)
    {
        if (IsStartupScenario(scenario))
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

    private static string FormatTimingMedian(TimingStats? timing) =>
        timing == null ? "-" : RepresentativeMs(timing).ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatStdDevPct(TimingStats? timing)
    {
        if (timing?.StdDevMs is not double sd || timing.MeanMs <= 0)
            return "-";

        double pct = (sd / timing.MeanMs) * 100.0;
        return $"{pct.ToString("F1", CultureInfo.InvariantCulture)}%";
    }

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
