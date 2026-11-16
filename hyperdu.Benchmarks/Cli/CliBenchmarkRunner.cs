using System.Diagnostics;
using System.Runtime.InteropServices;

namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Orchestrates fixture generation, companion probes, hyperfine/fallback timing, and report writing.
/// </summary>
internal static class CliBenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        BenchmarkCliOptions options;
        try
        {
            options = BenchmarkCliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(BenchmarkCliOptions.GetHelpText());
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(BenchmarkCliOptions.GetHelpText());
            return 0;
        }

        if (options.ColdCache)
        {
            try
            {
                HyperfineRunner.EnsureColdCacheSupported();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        DiscoveredTools tools = ComparatorRegistry.DiscoverTools(options);
        if (tools.HyperduNoUi == null)
        {
            Console.Error.WriteLine("Warning: hyperdu binary not found. Pass --hyperdu-bin <path> or run via tests/bench/run-bench.sh.");
        }

        using FixtureGenerator generator = new(keepFixtures: options.KeepFixtures);
        List<FixtureSpec> fixtures = new();

        if (!options.NoSynthetic)
        {
            Console.WriteLine($"Generating '{options.Suite.ToString().ToLowerInvariant()}' synthetic fixtures in {generator.BaseDirectory}...");
            fixtures.AddRange(generator.GenerateSuite(options.Suite));
        }

        foreach (string customPath in options.CustomPaths)
        {
            if (!Directory.Exists(customPath))
            {
                Console.Error.WriteLine($"Error: Custom --path directory does not exist: {customPath}");
                return 2;
            }

            string fullPath = Path.GetFullPath(customPath);
            string label = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(label))
                label = "root";

            fixtures.Add(new FixtureSpec(
                Name: $"custom-{label}",
                RootPath: fullPath,
                Description: $"Custom target path ({fullPath})",
                ExpectedFiles: 0,
                ExpectedDirs: 0,
                IsSynthetic: false));
        }

        if (fixtures.Count == 0)
        {
            Console.Error.WriteLine("Error: No fixtures or --path targets specified.");
            return 2;
        }

        IReadOnlyList<ScenarioDefinition> scenarios = ComparatorRegistry.BuildScenarios(
            tools,
            fixtures,
            options.Suite,
            options.Filter);

        bool hasHyperfine = HyperfineRunner.IsHyperfineAvailable(options);
        string timingEngine = hasHyperfine ? "hyperfine" : "dotnet-stopwatch-fallback";
        Console.WriteLine($"Running {scenarios.Count} benchmark scenarios using {timingEngine}...");

        List<ScenarioResult> scenarioResults = new(scenarios.Count);
        bool parityFailed = false;

        foreach (ScenarioDefinition scenario in scenarios)
        {
            Console.WriteLine($"  -> {scenario.Id}");
            List<CommandInvocation> runnable = scenario.Invocations.Where(i => !i.Tool.IsPending).ToList();

            // Pass 1: Companion probe (TTFL, Peak RSS, output lines, parity check)
            Dictionary<string, ProbeResult> probes = new();
            ProbeResult? refProbe = null;
            foreach (CommandInvocation inv in runnable)
            {
                ProbeResult probe = await CompanionProbe.ProbeAsync(scenario, inv, refProbe, cancellationToken);
                probes[inv.Tool.Id] = probe;
                if (inv.Tool.IsReferenceDu)
                    refProbe = probe;

                if (scenario.RequireStrictStdoutParity && !inv.Tool.IsReferenceDu && !probe.ParityMatched)
                    parityFailed = true;
            }

            // Pass 2: Pure timing runs (hyperfine or Stopwatch fallback) for implemented invocations
            List<CommandInvocation> timedInvocations = runnable
                .Where(i => !probes[i.Tool.Id].ParityNote.StartsWith("pending", StringComparison.Ordinal))
                .ToList();
            IReadOnlyList<TimingStats> timings = await HyperfineRunner.RunScenarioAsync(timedInvocations, options, cancellationToken);
            Dictionary<string, TimingStats> timingMap = new();
            for (int i = 0; i < timedInvocations.Count && i < timings.Count; i++)
            {
                timingMap[timedInvocations[i].Tool.Id] = timings[i];
            }

            TimingStats? refTiming = runnable
                .FirstOrDefault(i => i.Tool.IsReferenceDu)
                is { } refInv && timingMap.TryGetValue(refInv.Tool.Id, out TimingStats? rt)
                ? rt
                : null;

            List<BenchmarkEntryResult> entries = new(scenario.Invocations.Count);
            foreach (CommandInvocation inv in scenario.Invocations)
            {
                probes.TryGetValue(inv.Tool.Id, out ProbeResult? probe);
                if (inv.Tool.IsPending || (probe != null && probe.ParityNote.StartsWith("pending", StringComparison.Ordinal)))
                {
                    entries.Add(new BenchmarkEntryResult(
                        ToolId: inv.Tool.Id,
                        ToolName: inv.Tool.DisplayName,
                        IsReferenceDu: false,
                        Timing: null,
                        Probe: probe,
                        SpeedupVsReferenceDu: null,
                        IsPending: true,
                        PendingReason: inv.Tool.PendingReason ?? probe?.ParityNote));
                    continue;
                }

                timingMap.TryGetValue(inv.Tool.Id, out TimingStats? timing);
                if (probe != null && timing?.PeakRssKb is long hyperfineRssKb && hyperfineRssKb > 0)
                {
                    probe = probe with { PeakRssKb = hyperfineRssKb };
                }

                double? speedup = (refTiming != null && timing is { MeanMs: > 0 })
                    ? refTiming.MeanMs / timing.MeanMs
                    : null;

                entries.Add(new BenchmarkEntryResult(
                    ToolId: inv.Tool.Id,
                    ToolName: inv.Tool.DisplayName,
                    IsReferenceDu: inv.Tool.IsReferenceDu,
                    Timing: timing,
                    Probe: probe,
                    SpeedupVsReferenceDu: speedup,
                    IsPending: false,
                    PendingReason: null));
            }

            scenarioResults.Add(new ScenarioResult(
                Id: scenario.Id,
                DisplayName: scenario.DisplayName,
                FixtureName: scenario.FixtureName,
                TargetPath: scenario.TargetPath,
                Tier: scenario.Tier.ToString(),
                Entries: entries,
                PendingNote: scenario.PendingNote));
        }

        BenchmarkEnvironment env = CaptureEnvironment(tools, options, hasHyperfine, timingEngine);
        BenchmarkReport report = new(
            Environment: env,
            SuiteName: options.Suite.ToString().ToLowerInvariant(),
            Fixtures: fixtures,
            Scenarios: scenarioResults);

        (string jsonPath, string mdPath) = await ReportWriter.WriteAsync(report, options.OutputDirectory, cancellationToken);
        Console.WriteLine($"Benchmark results written to:\n  - {jsonPath}\n  - {mdPath}");

        if (parityFailed)
        {
            Console.Error.WriteLine("One or more Tier 1 parity checks failed!");
            return 1;
        }

        return 0;
    }

    private static BenchmarkEnvironment CaptureEnvironment(
        DiscoveredTools tools,
        BenchmarkCliOptions options,
        bool hasHyperfine,
        string timingEngine)
    {
        return new BenchmarkEnvironment(
            OsDescription: RuntimeInformation.OSDescription.Trim(),
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            CpuModel: ReadCpuModel(),
            LogicalCores: Environment.ProcessorCount,
            ReferenceDuVersion: tools.ReferenceDuVersion,
            HasHyperfine: hasHyperfine,
            ColdCache: options.ColdCache,
            TimingEngine: timingEngine,
            GitCommit: ReadGitCommit(),
            TimestampUtc: DateTimeOffset.UtcNow);
    }

    private static string ReadCpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = line.IndexOf(':');
                        if (colon >= 0)
                            return line[(colon + 1)..].Trim();
                    }
                }
            }
        }
        catch
        {
            // Fall back to OS architecture string.
        }

        return $"{RuntimeInformation.ProcessArchitecture} CPU";
    }

    private static string ReadGitCommit()
    {
        try
        {
            ProcessStartInfo psi = new("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--short");
            psi.ArgumentList.Add("HEAD");

            using Process? proc = Process.Start(psi);
            if (proc == null)
                return "unknown";

            string sha = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(sha) ? sha : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
