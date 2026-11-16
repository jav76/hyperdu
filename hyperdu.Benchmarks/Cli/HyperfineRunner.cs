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
            HyperfineExport? parsed = JsonSerializer.Deserialize<HyperfineExport>(jsonText, JsonOptions)
                ?? throw new InvalidOperationException("hyperfine produced empty JSON output.");

            List<TimingStats> results = new(parsed.Results.Count);
            foreach (HyperfineEntry entry in parsed.Results)
            {
                results.Add(new TimingStats(
                    MeanMs: entry.Mean * 1000.0,
                    StdDevMs: entry.Stddev * 1000.0,
                    MedianMs: entry.Median * 1000.0,
                    MinMs: entry.Min * 1000.0,
                    MaxMs: entry.Max * 1000.0,
                    UserCpuMs: entry.User * 1000.0,
                    SystemCpuMs: entry.System * 1000.0,
                    Runs: entry.Times?.Count ?? options.EffectiveRuns,
                    Engine: "hyperfine"));
            }

            return results;
        }
        finally
        {
            try { if (File.Exists(jsonFile)) File.Delete(jsonFile); } catch { }
        }
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
    }
}
