using System.Text.Json;
using hyperdu.Benchmarks.Cli;

namespace hyperdu.Tests;

public class BenchmarkHarnessTests
{
    [Fact]
    public void BenchmarkCliOptions_ParsesAllFlags()
    {
        BenchmarkCliOptions options = BenchmarkCliOptions.Parse([
            "--suite", "full",
            "--path", "/tmp/a",
            "--path", "/tmp/b",
            "--no-synthetic",
            "--cold-cache",
            "--hyperdu-bin", "/opt/hyperdu",
            "--du-bin", "/usr/bin/du",
            "--output-dir", "out/bench",
            "--warmup", "1",
            "--runs", "4",
            "--filter", "balanced",
            "--no-hyperfine",
            "--keep-fixtures"
        ]);

        Assert.Equal(BenchmarkSuite.Full, options.Suite);
        Assert.Equal(new[] { "/tmp/a", "/tmp/b" }, options.CustomPaths);
        Assert.True(options.NoSynthetic);
        Assert.True(options.ColdCache);
        Assert.Equal("/opt/hyperdu", options.HyperduBinary);
        Assert.Equal("/usr/bin/du", options.DuBinary);
        Assert.Equal("out/bench", options.OutputDirectory);
        Assert.Equal(1, options.EffectiveWarmup);
        Assert.Equal(4, options.EffectiveRuns);
        Assert.Equal("balanced", options.Filter);
        Assert.True(options.NoHyperfine);
        Assert.True(options.KeepFixtures);
    }

    [Fact]
    public void BenchmarkCliOptions_RejectsInvalidSuiteOrUnknownFlag()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkCliOptions.Parse(["--suite", "ultra"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCliOptions.Parse(["--unknown-flag"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCliOptions.Parse(["--runs", "0"]));
    }

    [Fact]
    public void FixtureGenerator_CreatesExpectedTreesAndCleansUp()
    {
        string baseDir;
        using (FixtureGenerator generator = new())
        {
            baseDir = generator.BaseDirectory;
            FixtureSpec tiny = generator.CreateTinyStartup();
            FixtureSpec balanced = generator.CreateBalancedTree(dirCount: 15, fileCount: 120);
            FixtureSpec links = generator.CreateHardlinksHeavy(uniqueFiles: 10, hardlinkCount: 10, symlinkCount: 4);

            Assert.Equal(5, Directory.GetFiles(tiny.RootPath, "*", SearchOption.AllDirectories).Length);
            Assert.Equal(120, Directory.GetFiles(balanced.RootPath, "*", SearchOption.AllDirectories).Length);
            Assert.Contains(Directory.GetFiles(balanced.RootPath, "*.tmp", SearchOption.AllDirectories), _ => true);

            // Symlinks + base files + hardlinks
            int totalEntries = Directory.GetFileSystemEntries(links.RootPath, "*", SearchOption.AllDirectories).Length;
            Assert.True(totalEntries >= 24);
        }

        Assert.False(Directory.Exists(baseDir));
    }

    [Fact]
    public void FixtureGenerator_CreateDevMonorepoCreatesExpectedLayout()
    {
        using FixtureGenerator generator = new();
        FixtureSpec monorepo = generator.CreateDevMonorepo(packageCount: 4, filesPerPackage: 5, gitObjectsCount: 16, srcFileCount: 10);

        Assert.True(Directory.Exists(Path.Combine(monorepo.RootPath, "node_modules", ".bin")));
        Assert.True(Directory.Exists(Path.Combine(monorepo.RootPath, ".git", "objects", "pack")));
        Assert.True(Directory.Exists(Path.Combine(monorepo.RootPath, "src", "bin", "Debug")));
        Assert.Equal(4 * 5 + 4 + 16 + 4 + 10 + 40, monorepo.ExpectedFiles);
    }

    [UnixFact]
    public void ComparatorRegistry_IncludesDevMonorepoExcludeAndEarlyExitFlags()
    {
        using FixtureGenerator generator = new();
        FixtureSpec monorepo = generator.CreateDevMonorepo(packageCount: 2, filesPerPackage: 3, gitObjectsCount: 4, srcFileCount: 4);

        DiscoveredTools tools = ComparatorRegistry.DiscoverTools(new BenchmarkCliOptions { Suite = BenchmarkSuite.Standard });
        IReadOnlyList<ScenarioDefinition> scenarios =
            ComparatorRegistry.BuildScenarios(tools, [monorepo], BenchmarkSuite.Standard, filter: null);

        Assert.Contains(scenarios, s => s.Id == "dev-monorepo/exclude-node-modules");
        ScenarioDefinition duA = Assert.Single(scenarios, s => s.Id == "dev-monorepo/du-a");
        Assert.True(duA.MeasureEarlyExit);

        if (tools.ReferenceDuIsGnu)
        {
            Assert.Contains(scenarios, s => s.Id == "dev-monorepo/du-exclude-dir");
        }
    }

    [UnixFact]
    public async Task CompanionProbe_MeasuresEarlyExitHead100()
    {
        ToolSpec tool = new(
            Id: "stream-test",
            DisplayName: "stream-test",
            Tier: ComparatorTier.Tier1Parity,
            Executable: "/bin/sh",
            BaseEnvironment: new Dictionary<string, string>(),
            IsReferenceDu: false,
            SupportsDuFlags: true);

        CommandInvocation inv = new(
            tool,
            ["-c", "seq 1 5000"],
            tool.BaseEnvironment);

        ScenarioDefinition scenario = new(
            Id: "head-test",
            DisplayName: "Head 100 test",
            FixtureName: "test",
            TargetPath: "/tmp",
            Tier: ComparatorTier.Tier1Parity,
            DuFlags: "-a",
            Invocations: [inv],
            RequireStrictStdoutParity: true,
            MeasureEarlyExit: true);

        ProbeResult probe = await CompanionProbe.ProbeAsync(scenario, inv, referenceProbe: null);

        Assert.Equal(0, probe.ExitCode);
        Assert.Equal(5000, probe.OutputLines);
        Assert.NotNull(probe.EarlyExitHead100Ms);
        Assert.True(probe.EarlyExitHead100Ms > 0);
    }

    [UnixFact]
    public void FixtureGenerator_DeepChainCreatesAndDeletesBeyondPathMax()
    {
        string baseDir;
        using (FixtureGenerator generator = new())
        {
            baseDir = generator.BaseDirectory;
            FixtureSpec deep = generator.CreateDeepChain(depth: 260);
            Assert.Equal(261, deep.ExpectedDirs);
            Assert.True(deep.ExpectedFiles > 0);
        }

        Assert.False(Directory.Exists(baseDir));
    }

    [UnixFact]
    public void ComparatorRegistry_DiscoversDuAndBuildsBothTiers()
    {
        using FixtureGenerator generator = new();
        FixtureSpec balanced = generator.CreateBalancedTree(dirCount: 5, fileCount: 25);

        BenchmarkCliOptions opts = new() { Suite = BenchmarkSuite.Quick };
        DiscoveredTools tools = ComparatorRegistry.DiscoverTools(opts);
        IReadOnlyList<ScenarioDefinition> scenarios =
            ComparatorRegistry.BuildScenarios(tools, [balanced], BenchmarkSuite.Quick, filter: null);

        Assert.NotNull(tools.ReferenceDu);
        Assert.Contains(scenarios, s => s.Tier == ComparatorTier.Tier2Workload && s.Id == "balanced-tree/summarize");
        Assert.Contains(scenarios, s => s.Tier == ComparatorTier.Tier1Parity && s.Id == "balanced-tree/du-s");
        Assert.Contains(scenarios, s => s.Tier == ComparatorTier.Tier1Parity && s.Id == "balanced-tree/du-a");

        IReadOnlyList<ScenarioDefinition> filtered =
            ComparatorRegistry.BuildScenarios(tools, [balanced], BenchmarkSuite.Quick, filter: "du-apparent");
        Assert.Single(filtered);
        Assert.Equal("balanced-tree/du-apparent", filtered[0].Id);
    }

    [UnixFact]
    public async Task CompanionProbe_CapturesTtflRssAndParity()
    {
        ToolSpec tool = new(
            Id: "echo-test",
            DisplayName: "echo-test",
            Tier: ComparatorTier.Tier1Parity,
            Executable: "/bin/sh",
            BaseEnvironment: new Dictionary<string, string>(),
            IsReferenceDu: false,
            SupportsDuFlags: true);

        CommandInvocation inv = new(
            tool,
            ["-c", "printf '4\t/a\\n8\t/b\\n'"],
            tool.BaseEnvironment);

        ScenarioDefinition scenario = new(
            Id: "probe-test",
            DisplayName: "Probe Test",
            FixtureName: "test",
            TargetPath: "/tmp",
            Tier: ComparatorTier.Tier1Parity,
            DuFlags: "-s",
            Invocations: [inv],
            RequireStrictStdoutParity: true);

        ProbeResult probe = await CompanionProbe.ProbeAsync(scenario, inv, referenceProbe: null);

        Assert.Equal(0, probe.ExitCode);
        Assert.Equal(2, probe.OutputLines);
        Assert.NotNull(probe.TtflMs);
        Assert.True(probe.TtflMs >= 0);
        Assert.True(probe.PeakRssKb > 0);
        Assert.False(string.IsNullOrEmpty(probe.StdoutHash));

        (bool exactMatch, string exactNote) = CompanionProbe.EvaluateParity(scenario, inv, probe, probe);
        Assert.True(exactMatch);
        Assert.Equal("exact match", exactNote);

        ProbeResult mismatched = probe with { StdoutHash = "0000" };
        (bool mismatch, string mismatchNote) = CompanionProbe.EvaluateParity(scenario, inv, mismatched, probe);
        Assert.False(mismatch);
        Assert.Contains("stdout diff", mismatchNote);
    }

    [Fact]
    public void HyperfineRunner_ShellQuotingAndSampleStatsAreAccurate()
    {
        ToolSpec tool = new(
            Id: "t",
            DisplayName: "t",
            Tier: ComparatorTier.Tier2Workload,
            Executable: "/usr/bin/hyperdu",
            BaseEnvironment: new Dictionary<string, string>(),
            IsReferenceDu: false,
            SupportsDuFlags: false);

        CommandInvocation inv = new(
            tool,
            ["/tmp/a 'quoted' path"],
            new Dictionary<string, string> { ["HYPERDU_NO_UI"] = "1" });

        string cmd = HyperfineRunner.BuildShellCommand(inv);
        Assert.Equal("env HYPERDU_NO_UI='1' '/usr/bin/hyperdu' '/tmp/a '\"'\"'quoted'\"'\"' path'", cmd);

        TimingStats stats = HyperfineRunner.SummarizeSamples([10.0, 20.0, 30.0, 40.0], [2.0, 4.0, 6.0, 8.0], [1.0, 1.0, 1.0, 1.0], "test");
        Assert.Equal(25.0, stats.MeanMs, precision: 6);
        Assert.Equal(25.0, stats.MedianMs, precision: 6);
        Assert.Equal(10.0, stats.MinMs, precision: 6);
        Assert.Equal(40.0, stats.MaxMs, precision: 6);
        Assert.Equal(5.0, stats.UserCpuMs!.Value, precision: 6);
        Assert.Equal(1.0, stats.SystemCpuMs!.Value, precision: 6);
        Assert.NotNull(stats.StdDevMs);
        Assert.Equal(Math.Sqrt(500.0 / 3.0), stats.StdDevMs.Value, precision: 6);
    }

    [UnixFact]
    public async Task HyperfineRunner_StopwatchFallbackRunsCleanly()
    {
        ToolSpec tool = new(
            Id: "true-tool",
            DisplayName: "true",
            Tier: ComparatorTier.Tier2Workload,
            Executable: "/bin/sh",
            BaseEnvironment: new Dictionary<string, string>(),
            IsReferenceDu: false,
            SupportsDuFlags: false);

        CommandInvocation inv = new(tool, ["-c", "exit 0"], tool.BaseEnvironment);
        BenchmarkCliOptions opts = new() { NoHyperfine = true, Warmup = 1, Runs = 2 };

        IReadOnlyList<TimingStats> stats = await HyperfineRunner.RunScenarioAsync([inv], opts);

        Assert.Single(stats);
        Assert.Equal("dotnet-stopwatch-fallback", stats[0].Engine);
        Assert.Equal(2, stats[0].Runs);
        Assert.True(stats[0].MeanMs >= 0);
    }

    [Fact]
    public void HyperfineRunner_ParsesBothV1AndV2JsonSchemas()
    {
        const string v1Json = """
            {
              "results": [
                {
                  "command": "du -s .",
                  "mean": 0.012,
                  "stddev": 0.002,
                  "median": 0.011,
                  "user": 0.004,
                  "system": 0.007,
                  "min": 0.010,
                  "max": 0.015,
                  "times": [0.010, 0.012, 0.015]
                }
              ]
            }
            """;

        IReadOnlyList<TimingStats> v1Stats = HyperfineRunner.ParseHyperfineJson(v1Json, defaultRuns: 3);
        Assert.Single(v1Stats);
        Assert.Equal(12.0, v1Stats[0].MeanMs, precision: 6);
        Assert.Equal(2.0, v1Stats[0].StdDevMs!.Value, precision: 6);
        Assert.Equal(4.0, v1Stats[0].UserCpuMs!.Value, precision: 6);
        Assert.Equal(7.0, v1Stats[0].SystemCpuMs!.Value, precision: 6);
        Assert.Equal(3, v1Stats[0].Runs);
        Assert.Null(v1Stats[0].PeakRssKb);

        const string v2Json = """
            {
              "schema_version": 2,
              "results": [
                {
                  "command": "du -s .",
                  "summary": {
                    "time_wall_clock": { "count": 5, "mean": 0.005, "stddev": 0.0005, "median": 0.0049, "min": 0.0045, "max": 0.0056 },
                    "time_user": { "mean": 0.001 },
                    "time_system": { "mean": 0.004 },
                    "memory_peak_resident": { "max": 2981888.0 }
                  }
                }
              ]
            }
            """;

        IReadOnlyList<TimingStats> v2Stats = HyperfineRunner.ParseHyperfineJson(v2Json, defaultRuns: 5);
        Assert.Single(v2Stats);
        Assert.Equal(5.0, v2Stats[0].MeanMs, precision: 6);
        Assert.Equal(0.5, v2Stats[0].StdDevMs!.Value, precision: 6);
        Assert.Equal(1.0, v2Stats[0].UserCpuMs!.Value, precision: 6);
        Assert.Equal(4.0, v2Stats[0].SystemCpuMs!.Value, precision: 6);
        Assert.Equal(5, v2Stats[0].Runs);
        Assert.Equal(2912L, v2Stats[0].PeakRssKb);
    }

    [Fact]
    public async Task ReportWriter_EmitsJsonAndMarkdown()
    {
        BenchmarkEnvironment env = new(
            OsDescription: "Linux Test",
            Architecture: "X64",
            CpuModel: "Test CPU",
            LogicalCores: 8,
            ReferenceDuVersion: "du (GNU coreutils) 9.7",
            HasHyperfine: true,
            ColdCache: false,
            TimingEngine: "hyperfine",
            GitCommit: "abc1234",
            TimestampUtc: new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        FixtureSpec fixture = new("balanced-tree", "/tmp/balanced", "500 dirs, 50,000 files", 50_000, 500, true);
        ScenarioResult scenario = new(
            Id: "balanced-tree/summarize",
            DisplayName: "Disk summary on balanced-tree",
            FixtureName: "balanced-tree",
            TargetPath: "/tmp/balanced",
            Tier: "Tier2Workload",
            Entries:
            [
                new BenchmarkEntryResult(
                    "ref-du",
                    "GNU du (reference)",
                    true,
                    new TimingStats(20.0, 1.5, 19.8, 18.0, 22.0, 10.0, 9.0, 5, "hyperfine"),
                    new ProbeResult(0, 19.5, 4096, 1, "abc", "100\t/tmp/balanced", string.Empty, true, "baseline"),
                    1.0,
                    false,
                    null),
                new BenchmarkEntryResult(
                    "hyperdu-native",
                    "hyperdu (native, auto-threads)",
                    false,
                    new TimingStats(10.0, 0.5, 9.9, 9.2, 11.0, 15.0, 12.0, 5, "hyperfine"),
                    new ProbeResult(0, 9.8, 16384, 1, "def", "100\t/tmp/balanced", string.Empty, true, "ok (exit 0)"),
                    2.0,
                    false,
                    null),
                new BenchmarkEntryResult(
                    "hyperdu-du-auto",
                    "hyperdu du",
                    false,
                    null,
                    null,
                    null,
                    true,
                    "Pending Phase 1 (#15)")
            ],
            PendingNote: null);

        BenchmarkReport report = new(env, "standard", [fixture], [scenario]);

        string outDir = Path.Combine(Path.GetTempPath(), $"hyperdu-report-test-{Guid.NewGuid():N}");
        try
        {
            (string jsonPath, string mdPath) = await ReportWriter.WriteAsync(report, outDir);
            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(mdPath));

            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            Assert.Equal("standard", doc.RootElement.GetProperty("suiteName").GetString());

            string md = await File.ReadAllTextAsync(mdPath);
            Assert.Contains("2.00x", md);
            Assert.Contains("16.00", md); // 16384 KiB -> 16.00 MiB
            Assert.Contains("Pending Phase 1 (#15)", md);
            Assert.DoesNotContain("\u2014", md); // No em-dashes
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);
        }
    }
}
