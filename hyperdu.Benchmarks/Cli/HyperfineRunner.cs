using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Executes wall-clock and CPU benchmarks via hyperfine (when available) or a high-resolution Stopwatch fallback.
/// </summary>
internal static class HyperfineRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<bool> ShellNoneSupported = new(ProbeShellNoneSupport);

    public static bool IsHyperfineAvailable(BenchmarkCliOptions options) =>
        !options.NoHyperfine && ComparatorRegistry.FindOnPath("hyperfine") != null;

    /// <summary>Verifies whether cold-cache page dropping is permitted on this host.</summary>
    public static void EnsureColdCacheSupported()
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("--cold-cache is only supported on Linux and macOS.");

        if (OperatingSystem.IsLinux())
        {
            if (IsRootUser() || CanRunPasswordlessSudo())
                return;

            throw new InvalidOperationException(
                "--cold-cache on Linux requires root or passwordless sudo for writing /proc/sys/vm/drop_caches.");
        }

        if (OperatingSystem.IsMacOS())
        {
            if (IsRootUser() || CanRunPasswordlessSudo())
                return;

            throw new InvalidOperationException(
                "--cold-cache on macOS requires root or passwordless sudo for 'purge'.");
        }
    }

    public static async Task<IReadOnlyList<TimingStats>> RunScenarioAsync(
        IReadOnlyList<CommandInvocation> invocations,
        BenchmarkCliOptions options,
        CancellationToken cancellationToken = default)
    {
        if (invocations.Count == 0)
            return [];

        if (IsHyperfineAvailable(options))
        {
            return await RunWithHyperfineAsync(invocations, options, cancellationToken);
        }

        return await RunWithStopwatchFallbackAsync(invocations, options, cancellationToken);
    }

    private static async Task<IReadOnlyList<TimingStats>> RunWithHyperfineAsync(
        IReadOnlyList<CommandInvocation> invocations,
        BenchmarkCliOptions options,
        CancellationToken cancellationToken)
    {
        bool useShellNoneBatching = !options.ColdCache && ShellNoneSupported.Value;
        if (!useShellNoneBatching)
            return await ExecuteHyperfineBatchAsync(invocations, options, shellNone: false, cancellationToken);

        List<int> directIndices = [];
        List<CommandInvocation> directBatch = [];
        List<int> envIndices = [];
        List<CommandInvocation> envBatch = [];

        for (int i = 0; i < invocations.Count; i++)
        {
            if (invocations[i].EnvironmentVariables.Count == 0)
            {
                directIndices.Add(i);
                directBatch.Add(invocations[i]);
            }
            else
            {
                envIndices.Add(i);
                envBatch.Add(invocations[i]);
            }
        }

        if (directBatch.Count == 0 || envBatch.Count == 0)
        {
            return await ExecuteHyperfineBatchAsync(
                invocations,
                options,
                shellNone: directBatch.Count > 0,
                cancellationToken);
        }

        TimingStats[] ordered = new TimingStats[invocations.Count];
        IReadOnlyList<TimingStats> directResults =
            await ExecuteHyperfineBatchAsync(directBatch, options, shellNone: true, cancellationToken);
        for (int i = 0; i < directIndices.Count && i < directResults.Count; i++)
            ordered[directIndices[i]] = directResults[i];

        IReadOnlyList<TimingStats> envResults =
            await ExecuteHyperfineBatchAsync(envBatch, options, shellNone: false, cancellationToken);
        for (int i = 0; i < envIndices.Count && i < envResults.Count; i++)
            ordered[envIndices[i]] = envResults[i];

        return ordered;
    }

    private static async Task<IReadOnlyList<TimingStats>> ExecuteHyperfineBatchAsync(
        IReadOnlyList<CommandInvocation> invocations,
        BenchmarkCliOptions options,
        bool shellNone,
        CancellationToken cancellationToken)
    {
        string jsonFile = Path.Combine(Path.GetTempPath(), $"hyperdu-hyperfine-{Guid.NewGuid():N}.json");
        try
        {
            ProcessStartInfo psi = new("hyperfine")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("--ignore-failure");
            if (shellNone)
                psi.ArgumentList.Add("--shell=none");
            psi.ArgumentList.Add("--warmup");
            psi.ArgumentList.Add(options.EffectiveWarmup.ToString());
            psi.ArgumentList.Add("--runs");
            psi.ArgumentList.Add(options.EffectiveRuns.ToString());
            psi.ArgumentList.Add("--export-json");
            psi.ArgumentList.Add(jsonFile);

            if (options.ColdCache)
            {
                psi.ArgumentList.Add("--prepare");
                psi.ArgumentList.Add(GetColdCachePrepareShellCommand());
            }

            foreach (CommandInvocation inv in invocations)
            {
                psi.ArgumentList.Add(BuildShellCommand(inv));
            }

            using Process? process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to launch hyperfine.");

            Task<string> outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outTask, errTask, process.WaitForExitAsync(cancellationToken));

            if (process.ExitCode != 0 || !File.Exists(jsonFile))
            {
                throw new InvalidOperationException(
                    $"hyperfine exited with code {process.ExitCode}: {errTask.Result}");
            }

            string jsonText = await File.ReadAllTextAsync(jsonFile, cancellationToken);
            return ParseHyperfineJson(jsonText, options.EffectiveRuns);
        }
        finally
        {
            try { if (File.Exists(jsonFile)) File.Delete(jsonFile); } catch { }
        }
    }

    private static bool ProbeShellNoneSupport()
    {
        try
        {
            ProcessStartInfo psi = new("hyperfine")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--help");
            using Process? proc = Process.Start(psi);
            if (proc == null)
                return false;

            string help = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && help.Contains("--shell", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses both hyperfine v1 and v2 (--export-json) result schemas into TimingStats.</summary>
    internal static IReadOnlyList<TimingStats> ParseHyperfineJson(string jsonText, int defaultRuns)
    {
        HyperfineExport parsed = JsonSerializer.Deserialize<HyperfineExport>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("hyperfine produced empty JSON output.");

        List<TimingStats> results = new(parsed.Results.Count);
        bool isV2 = parsed.SchemaVersion >= 2;

        foreach (HyperfineEntry entry in parsed.Results)
        {
            if (isV2 || entry.Summary?.TimeWallClock != null)
            {
                HyperfineMetricSummary wall = entry.Summary?.TimeWallClock
                    ?? throw new InvalidOperationException("hyperfine v2 JSON entry is missing summary.time_wall_clock.");
                long? peakRssKb = entry.Summary?.MemoryPeakResident?.Max is double maxBytes && maxBytes > 0
                    ? (long)Math.Round(maxBytes / 1024.0)
                    : null;

                results.Add(new TimingStats(
                    MeanMs: wall.Mean * 1000.0,
                    StdDevMs: wall.Stddev * 1000.0,
                    MedianMs: wall.Median * 1000.0,
                    MinMs: wall.Min * 1000.0,
                    MaxMs: wall.Max * 1000.0,
                    UserCpuMs: entry.Summary?.TimeUser != null ? entry.Summary.TimeUser.Mean * 1000.0 : null,
                    SystemCpuMs: entry.Summary?.TimeSystem != null ? entry.Summary.TimeSystem.Mean * 1000.0 : null,
                    Runs: wall.Count > 0 ? wall.Count : defaultRuns,
                    Engine: "hyperfine",
                    PeakRssKb: peakRssKb));
            }
            else
            {
                results.Add(new TimingStats(
                    MeanMs: entry.Mean * 1000.0,
                    StdDevMs: entry.Stddev * 1000.0,
                    MedianMs: entry.Median * 1000.0,
                    MinMs: entry.Min * 1000.0,
                    MaxMs: entry.Max * 1000.0,
                    UserCpuMs: entry.User * 1000.0,
                    SystemCpuMs: entry.System * 1000.0,
                    Runs: entry.Times?.Count ?? defaultRuns,
                    Engine: "hyperfine"));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<TimingStats>> RunWithStopwatchFallbackAsync(
        IReadOnlyList<CommandInvocation> invocations,
        BenchmarkCliOptions options,
        CancellationToken cancellationToken)
    {
        List<TimingStats> results = new(invocations.Count);
        int warmup = options.EffectiveWarmup;
        int runs = options.EffectiveRuns;

        foreach (CommandInvocation inv in invocations)
        {
            for (int w = 0; w < warmup; w++)
            {
                if (options.ColdCache)
                    await RunColdCachePrepareAsync(cancellationToken);
                _ = await RunSingleFallbackIterationAsync(inv, cancellationToken);
            }

            double[] wallTimes = new double[runs];
            double?[] userTimes = new double?[runs];
            double?[] sysTimes = new double?[runs];

            for (int r = 0; r < runs; r++)
            {
                if (options.ColdCache)
                    await RunColdCachePrepareAsync(cancellationToken);

                (wallTimes[r], userTimes[r], sysTimes[r]) = await RunSingleFallbackIterationAsync(inv, cancellationToken);
            }

            results.Add(SummarizeSamples(wallTimes, userTimes, sysTimes, "dotnet-stopwatch-fallback"));
        }

        return results;
    }

    private static async Task<(double WallMs, double? UserMs, double? SysMs)> RunSingleFallbackIterationAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = CompanionProbe.BuildStartInfo(invocation, redirectStdout: true, redirectStderr: true);
        long startTs = Stopwatch.GetTimestamp();

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{invocation.Tool.Executable}'.");

        Task drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        Task drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        await Task.WhenAll(drainOut, drainErr, process.WaitForExitAsync(cancellationToken));

        double wallMs = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
        (double? userMs, double? sysMs) = TryReadCpuTimes(process);
        return (wallMs, userMs, sysMs);
    }

    private static (double? UserMs, double? SysMs) TryReadCpuTimes(Process process)
    {
        try
        {
            return (process.UserProcessorTime.TotalMilliseconds, process.PrivilegedProcessorTime.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
        catch (NotSupportedException)
        {
            return (null, null);
        }
    }

    internal static TimingStats SummarizeSamples(
        double[] wallTimes,
        double?[] userTimes,
        double?[] sysTimes,
        string engine)
    {
        double mean = wallTimes.Average();
        double? stdDev = null;
        if (wallTimes.Length > 1)
        {
            double sumSq = wallTimes.Sum(t => (t - mean) * (t - mean));
            stdDev = Math.Sqrt(sumSq / (wallTimes.Length - 1));
        }

        double[] sorted = [.. wallTimes];
        Array.Sort(sorted);
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

        double? userMean = userTimes.All(u => u.HasValue) ? userTimes.Average(u => u!.Value) : null;
        double? sysMean = sysTimes.All(s => s.HasValue) ? sysTimes.Average(s => s!.Value) : null;

        return new TimingStats(
            MeanMs: mean,
            StdDevMs: stdDev,
            MedianMs: median,
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            UserCpuMs: userMean,
            SystemCpuMs: sysMean,
            Runs: wallTimes.Length,
            Engine: engine);
    }

    internal static string BuildShellCommand(CommandInvocation invocation)
    {
        StringBuilder sb = new();
        if (invocation.EnvironmentVariables.Count > 0)
        {
            sb.Append("env ");
            foreach ((string key, string value) in invocation.EnvironmentVariables)
            {
                sb.Append(key).Append('=').Append(QuotePosix(value)).Append(' ');
            }
        }

        sb.Append(QuotePosix(invocation.Tool.Executable));
        foreach (string arg in invocation.Arguments)
        {
            sb.Append(' ').Append(QuotePosix(arg));
        }

        return sb.ToString();
    }

    private static string QuotePosix(string value)
    {
        if (value.Length == 0)
            return "''";

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string GetColdCachePrepareShellCommand()
    {
        if (OperatingSystem.IsMacOS())
            return IsRootUser() ? "sync && purge" : "sync && sudo -n purge";

        return IsRootUser()
            ? "sync && echo 3 > /proc/sys/vm/drop_caches"
            : "sync && echo 3 | sudo -n tee /proc/sys/vm/drop_caches >/dev/null";
    }

    private static async Task RunColdCachePrepareAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(GetColdCachePrepareShellCommand());

        using Process? proc = Process.Start(psi);
        if (proc == null)
            return;

        await proc.WaitForExitAsync(cancellationToken);
    }

    private static bool IsRootUser()
    {
        if (OperatingSystem.IsWindows())
            return false;

        return string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
    }

    private static bool CanRunPasswordlessSudo()
    {
        try
        {
            ProcessStartInfo psi = new("sudo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("true");
            using Process? proc = Process.Start(psi);
            if (proc == null)
                return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class HyperfineExport
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("results")]
        public List<HyperfineEntry> Results { get; set; } = new();
    }

    private sealed class HyperfineEntry
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("mean")]
        public double Mean { get; set; }

        [JsonPropertyName("stddev")]
        public double? Stddev { get; set; }

        [JsonPropertyName("median")]
        public double Median { get; set; }

        [JsonPropertyName("user")]
        public double User { get; set; }

        [JsonPropertyName("system")]
        public double System { get; set; }

        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; }

        [JsonPropertyName("times")]
        public List<double>? Times { get; set; }

        [JsonPropertyName("summary")]
        public HyperfineV2Summary? Summary { get; set; }
    }

    private sealed class HyperfineV2Summary
    {
        [JsonPropertyName("time_wall_clock")]
        public HyperfineMetricSummary? TimeWallClock { get; set; }

        [JsonPropertyName("time_user")]
        public HyperfineMetricSummary? TimeUser { get; set; }

        [JsonPropertyName("time_system")]
        public HyperfineMetricSummary? TimeSystem { get; set; }

        [JsonPropertyName("memory_peak_resident")]
        public HyperfineMetricSummary? MemoryPeakResident { get; set; }
    }

    private sealed class HyperfineMetricSummary
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("mean")]
        public double Mean { get; set; }

        [JsonPropertyName("stddev")]
        public double? Stddev { get; set; }

        [JsonPropertyName("median")]
        public double Median { get; set; }

        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; }
    }
}
