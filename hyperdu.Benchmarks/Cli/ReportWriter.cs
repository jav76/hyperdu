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

            sb.AppendLine("| Tool | Mean ± StdDev (ms) | Median (ms) | Speedup vs Ref | User / Sys CPU (ms) | TTFL (ms) | Peak RSS (MiB) | Lines | Parity |");
            sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |");

            foreach (BenchmarkEntryResult entry in scenario.Entries)
            {
                if (entry.IsPending)
                {
                    string reason = entry.PendingReason ?? "Pending";
                    sb.AppendLine($"| {entry.ToolName} | {reason} | - | - | - | - | - | - | {reason} |");
                    continue;
                }

                string meanStd = FormatMeanStdDev(entry.Timing);
                string median = entry.Timing != null ? entry.Timing.MedianMs.ToString("F2", CultureInfo.InvariantCulture) : "-";
                string speedup = entry.IsReferenceDu ? "1.00x (ref)" : FormatSpeedup(entry.SpeedupVsReferenceDu);
                string cpu = entry.Timing is { UserCpuMs: double userMs, SystemCpuMs: double sysMs }
                    ? $"{userMs.ToString("F2", CultureInfo.InvariantCulture)} / {sysMs.ToString("F2", CultureInfo.InvariantCulture)}"
                    : "-";
                string ttfl = entry.Probe?.TtflMs is double t ? t.ToString("F2", CultureInfo.InvariantCulture) : "-";
                string rss = FormatRssMib(entry.Probe?.PeakRssKb);
                string lines = entry.Probe != null ? entry.Probe.OutputLines.ToString("N0", CultureInfo.InvariantCulture) : "-";
                string parity = FormatParity(entry.Probe);

                sb.AppendLine($"| {entry.ToolName} | {meanStd} | {median} | {speedup} | {cpu} | {ttfl} | {rss} | {lines} | {parity} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static BenchmarkEntryResult? PickHeadlineHyperduEntry(IReadOnlyList<BenchmarkEntryResult> entries) =>
        entries.FirstOrDefault(e => e.ToolId == "hyperdu-du-auto")
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
