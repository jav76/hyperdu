using System.Diagnostics;

namespace hyperdu.Benchmarks.Cli;

internal sealed record DiscoveredTools(
    ToolSpec ReferenceDu,
    bool ReferenceDuIsGnu,
    string ReferenceDuVersion,
    ToolSpec? HyperduNoUi,
    ToolSpec? HyperduNoUiSingleThread,
    ToolSpec? HyperduNoUiManaged,
    ToolSpec? HyperduDuPersona,
    ToolSpec? HyperduDuSingleThread,
    ToolSpec? HyperduDuFourThreads,
    ToolSpec? UutilsDu,
    string? UutilsSubcommandPrefix,
    ToolSpec? Dust,
    ToolSpec? GoGdu,
    ToolSpec? Ncdu,
    bool HasDuPersona
);

/// <summary>
/// Discovers hyperdu, reference du, and optional Tier 1/Tier 2 comparators and builds benchmark scenario matrices.
/// </summary>
internal static class ComparatorRegistry
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnv = new Dictionary<string, string>();

    public static DiscoveredTools DiscoverTools(BenchmarkCliOptions options)
    {
        (string duBin, string duVersion, bool isGnuDu) = ResolveReferenceDu(options.DuBinary);
        ToolSpec refDu = new(
            Id: "ref-du",
            DisplayName: isGnuDu ? "GNU du (reference)" : "du (reference)",
            Tier: ComparatorTier.Tier1Parity,
            Executable: duBin,
            BaseEnvironment: EmptyEnv,
            IsReferenceDu: true,
            SupportsDuFlags: true);

        string? hyperduBin = ResolveHyperduBinary(options.HyperduBinary);
        ToolSpec? hyperduNative = null;
        ToolSpec? hyperduSingle = null;
        ToolSpec? hyperduManaged = null;
        ToolSpec? hyperduDuDefault = null;
        ToolSpec? hyperduDuOne = null;
        ToolSpec? hyperduDuFour = null;
        bool hasDuPersona = false;

        if (hyperduBin != null)
        {
            hyperduNative = new ToolSpec(
                Id: "hyperdu-native",
                DisplayName: "hyperdu (native, auto-threads)",
                Tier: ComparatorTier.Tier2Workload,
                Executable: hyperduBin,
                BaseEnvironment: new Dictionary<string, string> { ["HYPERDU_NO_UI"] = "1" },
                IsReferenceDu: false,
                SupportsDuFlags: false);

            hyperduSingle = new ToolSpec(
                Id: "hyperdu-1t",
                DisplayName: "hyperdu (native, 1-thread)",
                Tier: ComparatorTier.Tier2Workload,
                Executable: hyperduBin,
                BaseEnvironment: new Dictionary<string, string> { ["HYPERDU_NO_UI"] = "1" },
                IsReferenceDu: false,
                SupportsDuFlags: false);

            hyperduManaged = new ToolSpec(
                Id: "hyperdu-managed",
                DisplayName: "hyperdu (managed I/O fallback)",
                Tier: ComparatorTier.Tier2Workload,
                Executable: hyperduBin,
                BaseEnvironment: new Dictionary<string, string>
                {
                    ["HYPERDU_NO_UI"] = "1",
                    ["HYPERDU_NATIVE_IO"] = "0"
                },
                IsReferenceDu: false,
                SupportsDuFlags: false);

            hasDuPersona = DetectDuPersona(hyperduBin, out string duExecutable, out bool usesSubcommand);
            IReadOnlyList<string>? duPrefix = usesSubcommand ? ["du"] : null;
            if (hasDuPersona)
            {
                hyperduDuDefault = new ToolSpec(
                    Id: "hyperdu-du-auto",
                    DisplayName: "hyperdu du (default threads)",
                    Tier: ComparatorTier.Tier1Parity,
                    Executable: duExecutable,
                    BaseEnvironment: EmptyEnv,
                    IsReferenceDu: false,
                    SupportsDuFlags: true,
                    FixedPrefixArguments: duPrefix);

                hyperduDuOne = new ToolSpec(
                    Id: "hyperdu-du-1t",
                    DisplayName: "hyperdu du (HYPERDU_THREADS=1)",
                    Tier: ComparatorTier.Tier1Parity,
                    Executable: duExecutable,
                    BaseEnvironment: new Dictionary<string, string> { ["HYPERDU_THREADS"] = "1" },
                    IsReferenceDu: false,
                    SupportsDuFlags: true,
                    FixedPrefixArguments: duPrefix);

                hyperduDuFour = new ToolSpec(
                    Id: "hyperdu-du-4t",
                    DisplayName: "hyperdu du (HYPERDU_THREADS=4)",
                    Tier: ComparatorTier.Tier1Parity,
                    Executable: duExecutable,
                    BaseEnvironment: new Dictionary<string, string> { ["HYPERDU_THREADS"] = "4" },
                    IsReferenceDu: false,
                    SupportsDuFlags: true,
                    FixedPrefixArguments: duPrefix);
            }
            else
            {
                hyperduDuDefault = new ToolSpec(
                    Id: "hyperdu-du-auto",
                    DisplayName: "hyperdu du",
                    Tier: ComparatorTier.Tier1Parity,
                    Executable: hyperduBin,
                    BaseEnvironment: EmptyEnv,
                    IsReferenceDu: false,
                    SupportsDuFlags: true,
                    IsPending: true,
                    PendingReason: "Pending Phase 1 (#15)");
            }
        }

        (ToolSpec? uutilsDu, string? uutilsPrefix) = ResolveUutilsDu();
        ToolSpec? dust = ResolveSimpleTool("dust", "dust", "--version");
        ToolSpec? goGdu = ResolveGoGdu();
        ToolSpec? ncdu = ResolveSimpleTool("ncdu", "ncdu", "--version");

        return new DiscoveredTools(
            ReferenceDu: refDu,
            ReferenceDuIsGnu: isGnuDu,
            ReferenceDuVersion: duVersion,
            HyperduNoUi: hyperduNative,
            HyperduNoUiSingleThread: hyperduSingle,
            HyperduNoUiManaged: hyperduManaged,
            HyperduDuPersona: hyperduDuDefault,
            HyperduDuSingleThread: hyperduDuOne,
            HyperduDuFourThreads: hyperduDuFour,
            UutilsDu: uutilsDu,
            UutilsSubcommandPrefix: uutilsPrefix,
            Dust: dust,
            GoGdu: goGdu,
            Ncdu: ncdu,
            HasDuPersona: hasDuPersona);
    }

    /// <summary>Builds Tier 2 workload scenarios and Tier 1 du parity scenarios across the target fixtures.</summary>
    public static IReadOnlyList<ScenarioDefinition> BuildScenarios(
        DiscoveredTools tools,
        IReadOnlyList<FixtureSpec> fixtures,
        BenchmarkSuite suite,
        string? filter)
    {
        List<ScenarioDefinition> scenarios = new();

        foreach (FixtureSpec fixture in fixtures)
        {
            scenarios.Add(BuildApparentSummarizeScenario(tools, fixture));

            if (fixture.Name != "tiny-startup")
            {
                scenarios.Add(BuildDefaultSummarizeScenario(tools, fixture));
                scenarios.Add(BuildDepthOneScenario(tools, fixture));
            }
        }

        // Tier 1 du parity flag matrix runs on representative fixtures (or all custom paths).
        List<FixtureSpec> parityTargets = SelectParityFixtures(fixtures, suite);
        foreach (FixtureSpec fixture in parityTargets)
        {
            foreach ((string suffix, string[] flags, bool gnuOnly) in GetParityVectors(fixture, suite))
            {
                if (gnuOnly && !tools.ReferenceDuIsGnu)
                    continue;

                scenarios.Add(BuildParityScenario(tools, fixture, suffix, flags));
            }
        }

        if (string.IsNullOrWhiteSpace(filter))
            return scenarios;

        return scenarios
            .Where(s => s.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        s.FixtureName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ScenarioDefinition BuildApparentSummarizeScenario(DiscoveredTools tools, FixtureSpec fixture)
    {
        List<CommandInvocation> invocations = new();
        AddHyperduNoUiInvocations(tools, fixture.RootPath, invocations);

        string[] duArgs = tools.ReferenceDuIsGnu
            ? ["--apparent-size", "-B1", "-s", fixture.RootPath]
            : ["-s", fixture.RootPath];
        invocations.Add(new CommandInvocation(tools.ReferenceDu, duArgs, tools.ReferenceDu.BaseEnvironment));

        if (tools.HyperduDuPersona is { IsPending: false } duPersona)
        {
            invocations.Add(new CommandInvocation(duPersona, WithPrefix(duPersona, "--apparent-size", "-B1", "-s", fixture.RootPath), duPersona.BaseEnvironment));
        }

        if (tools.UutilsDu != null)
        {
            string[] uArgs = BuildUutilsArgs(tools.UutilsSubcommandPrefix, "--apparent-size", "-B1", "-s", fixture.RootPath);
            invocations.Add(new CommandInvocation(tools.UutilsDu, uArgs, tools.UutilsDu.BaseEnvironment));
        }

        if (tools.Dust != null)
        {
            invocations.Add(new CommandInvocation(tools.Dust, ["-s", "-d", "0", "-b", fixture.RootPath], tools.Dust.BaseEnvironment));
        }

        if (tools.GoGdu != null)
        {
            invocations.Add(new CommandInvocation(tools.GoGdu, ["-n", "-p", "-a", fixture.RootPath], tools.GoGdu.BaseEnvironment));
        }

        return new ScenarioDefinition(
            Id: $"{fixture.Name}/apparent-summarize",
            DisplayName: $"Apparent-size summary on {fixture.Name}",
            FixtureName: fixture.Name,
            TargetPath: fixture.RootPath,
            Tier: ComparatorTier.Tier2Workload,
            DuFlags: tools.ReferenceDuIsGnu ? "--apparent-size -B1 -s" : "-s",
            Invocations: invocations);
    }

    private static ScenarioDefinition BuildDefaultSummarizeScenario(DiscoveredTools tools, FixtureSpec fixture)
    {
        List<CommandInvocation> invocations = new()
        {
            new CommandInvocation(tools.ReferenceDu, ["-s", fixture.RootPath], tools.ReferenceDu.BaseEnvironment)
        };

        if (tools.HyperduDuPersona is { IsPending: false } duPersona)
        {
            invocations.Add(new CommandInvocation(duPersona, WithPrefix(duPersona, "-s", fixture.RootPath), duPersona.BaseEnvironment));
        }

        if (tools.UutilsDu != null)
        {
            invocations.Add(new CommandInvocation(tools.UutilsDu, BuildUutilsArgs(tools.UutilsSubcommandPrefix, "-s", fixture.RootPath), tools.UutilsDu.BaseEnvironment));
        }

        if (tools.Dust != null)
        {
            invocations.Add(new CommandInvocation(tools.Dust, ["-d", "0", "-b", fixture.RootPath], tools.Dust.BaseEnvironment));
        }

        if (tools.GoGdu != null)
        {
            invocations.Add(new CommandInvocation(tools.GoGdu, ["-n", "-p", fixture.RootPath], tools.GoGdu.BaseEnvironment));
        }

        if (tools.Ncdu != null)
        {
            invocations.Add(new CommandInvocation(tools.Ncdu, ["-0", "-o", "-", fixture.RootPath], tools.Ncdu.BaseEnvironment));
        }

        AddHyperduNoUiInvocations(tools, fixture.RootPath, invocations);

        return new ScenarioDefinition(
            Id: $"{fixture.Name}/summarize",
            DisplayName: $"Disk summary (du -s) on {fixture.Name}",
            FixtureName: fixture.Name,
            TargetPath: fixture.RootPath,
            Tier: ComparatorTier.Tier2Workload,
            DuFlags: "-s",
            Invocations: invocations);
    }

    private static ScenarioDefinition BuildDepthOneScenario(DiscoveredTools tools, FixtureSpec fixture)
    {
        List<CommandInvocation> invocations = new()
        {
            new CommandInvocation(tools.ReferenceDu, ["-d", "1", fixture.RootPath], tools.ReferenceDu.BaseEnvironment)
        };

        if (tools.HyperduDuPersona is { IsPending: false } duPersona)
        {
            invocations.Add(new CommandInvocation(duPersona, WithPrefix(duPersona, "-d", "1", fixture.RootPath), duPersona.BaseEnvironment));
        }

        if (tools.UutilsDu != null)
        {
            invocations.Add(new CommandInvocation(tools.UutilsDu, BuildUutilsArgs(tools.UutilsSubcommandPrefix, "-d", "1", fixture.RootPath), tools.UutilsDu.BaseEnvironment));
        }

        if (tools.Dust != null)
        {
            invocations.Add(new CommandInvocation(tools.Dust, ["-d", "1", "-b", fixture.RootPath], tools.Dust.BaseEnvironment));
        }

        if (tools.GoGdu != null)
        {
            invocations.Add(new CommandInvocation(tools.GoGdu, ["-n", "-p", fixture.RootPath], tools.GoGdu.BaseEnvironment));
        }

        AddHyperduNoUiInvocations(tools, fixture.RootPath, invocations);

        return new ScenarioDefinition(
            Id: $"{fixture.Name}/depth-1",
            DisplayName: $"Depth-1 breakdown (du -d 1) on {fixture.Name}",
            FixtureName: fixture.Name,
            TargetPath: fixture.RootPath,
            Tier: ComparatorTier.Tier2Workload,
            DuFlags: "-d 1",
            Invocations: invocations);
    }

    private static ScenarioDefinition BuildParityScenario(
        DiscoveredTools tools,
        FixtureSpec fixture,
        string suffix,
        string[] flags)
    {
        string[] argsWithTarget = [.. flags, fixture.RootPath];
        List<CommandInvocation> invocations = new()
        {
            new CommandInvocation(tools.ReferenceDu, argsWithTarget, tools.ReferenceDu.BaseEnvironment)
        };

        if (tools.UutilsDu != null)
        {
            string[] uArgs = tools.UutilsSubcommandPrefix != null
                ? [tools.UutilsSubcommandPrefix, .. argsWithTarget]
                : argsWithTarget;
            invocations.Add(new CommandInvocation(tools.UutilsDu, uArgs, tools.UutilsDu.BaseEnvironment));
        }

        if (tools.HyperduDuPersona != null)
        {
            invocations.Add(new CommandInvocation(tools.HyperduDuPersona, WithPrefix(tools.HyperduDuPersona, argsWithTarget), tools.HyperduDuPersona.BaseEnvironment));
        }

        if (tools.HyperduDuSingleThread != null)
        {
            invocations.Add(new CommandInvocation(tools.HyperduDuSingleThread, WithPrefix(tools.HyperduDuSingleThread, argsWithTarget), tools.HyperduDuSingleThread.BaseEnvironment));
        }

        if (tools.HyperduDuFourThreads != null)
        {
            invocations.Add(new CommandInvocation(tools.HyperduDuFourThreads, WithPrefix(tools.HyperduDuFourThreads, argsWithTarget), tools.HyperduDuFourThreads.BaseEnvironment));
        }

        return new ScenarioDefinition(
            Id: $"{fixture.Name}/du-{suffix}",
            DisplayName: $"du parity ({string.Join(" ", flags)}) on {fixture.Name}",
            FixtureName: fixture.Name,
            TargetPath: fixture.RootPath,
            Tier: ComparatorTier.Tier1Parity,
            DuFlags: string.Join(" ", flags),
            Invocations: invocations,
            RequireStrictStdoutParity: true,
            PendingNote: tools.HasDuPersona ? null : "hyperdu du persona pending Phase 1 (#15)");
    }

    private static void AddHyperduNoUiInvocations(DiscoveredTools tools, string targetPath, List<CommandInvocation> invocations)
    {
        if (tools.HyperduNoUi != null)
            invocations.Add(new CommandInvocation(tools.HyperduNoUi, [targetPath], tools.HyperduNoUi.BaseEnvironment));

        if (tools.HyperduNoUiSingleThread != null)
            invocations.Add(new CommandInvocation(tools.HyperduNoUiSingleThread, ["-t", "1", targetPath], tools.HyperduNoUiSingleThread.BaseEnvironment));

        if (tools.HyperduNoUiManaged != null)
            invocations.Add(new CommandInvocation(tools.HyperduNoUiManaged, [targetPath], tools.HyperduNoUiManaged.BaseEnvironment));
    }

    private static List<FixtureSpec> SelectParityFixtures(IReadOnlyList<FixtureSpec> fixtures, BenchmarkSuite suite)
    {
        List<FixtureSpec> selected = new();
        foreach (FixtureSpec fixture in fixtures)
        {
            if (!fixture.IsSynthetic ||
                fixture.Name is "balanced-tree" or "wide-dir-20k" or "hardlinks-heavy" or "million-files")
            {
                selected.Add(fixture);
            }
        }

        if (selected.Count == 0 && fixtures.Count > 0)
            selected.Add(fixtures[0]);

        return selected;
    }

    private static IReadOnlyList<(string Suffix, string[] Flags, bool GnuOnly)> GetParityVectors(
        FixtureSpec fixture,
        BenchmarkSuite suite)
    {
        List<(string, string[], bool)> vectors = new()
        {
            ("s", ["-s"], false),
            ("a", ["-a"], false),
            ("sh", ["-sh"], false),
            ("d1", ["-d", "1"], false),
            ("apparent", ["--apparent-size", "-s"], true),
            ("count-links", ["-l", "-s"], true),
            ("deref", ["-L", "-s"], false),
            ("one-fs", ["-x", "-s"], false),
            ("inodes", ["--inodes", "-s"], true),
            ("time", ["--time", "-s"], true),
            ("exclude", ["--exclude=*.tmp", "-s"], true)
        };

        if (suite == BenchmarkSuite.Quick || fixture.Name == "million-files")
        {
            // Keep quick and 1M-file runs focused on key vectors (-s, -a, -d 1, --apparent-size).
            return vectors.Where(v => v.Item1 is "s" or "a" or "d1" or "apparent").ToList();
        }

        return vectors;
    }

    private static string[] WithPrefix(ToolSpec tool, params string[] args) =>
        tool.FixedPrefixArguments is { Count: > 0 } prefix
            ? [.. prefix, .. args]
            : args;

    private static string[] BuildUutilsArgs(string? prefix, params string[] rest) =>
        prefix != null ? [prefix, .. rest] : rest;

    private static (string Executable, string VersionLine, bool IsGnu) ResolveReferenceDu(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            (string stdout, _) = RunProbeProcess(overridePath, ["--version"]);
            string line = FirstLine(stdout) ?? Path.GetFileName(overridePath);
            bool isGnu = stdout.Contains("GNU coreutils", StringComparison.OrdinalIgnoreCase);
            return (overridePath, line, isGnu);
        }

        if (OperatingSystem.IsMacOS())
        {
            string? gduPath = FindOnPath("gdu");
            if (gduPath != null)
            {
                (string stdout, _) = RunProbeProcess(gduPath, ["--version"]);
                if (stdout.Contains("GNU coreutils", StringComparison.OrdinalIgnoreCase))
                    return (gduPath, FirstLine(stdout) ?? "GNU du (gdu)", true);
            }
        }

        string duPath = FindOnPath("du") ?? "du";
        (string verOut, _) = RunProbeProcess(duPath, ["--version"]);
        bool gnu = verOut.Contains("GNU coreutils", StringComparison.OrdinalIgnoreCase);
        string verLine = FirstLine(verOut) ?? (OperatingSystem.IsMacOS() ? "BSD du" : "system du");
        return (duPath, verLine, gnu);
    }

    private static string? ResolveHyperduBinary(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : FindOnPath(overridePath);

        string exeName = OperatingSystem.IsWindows() ? "hyperdu.exe" : "hyperdu";
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "bench-bin", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "hyperdu.Cli", "bin", "Release", "net10.0", "publish", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "hyperdu.Cli", "bin", "Release", "net10.0", exeName)
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return FindOnPath("hyperdu");
    }

    private static bool DetectDuPersona(string hyperduBin, out string duExecutable, out bool usesSubcommand)
    {
        string? dir = Path.GetDirectoryName(hyperduBin);
        if (dir != null)
        {
            string duSibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "du.exe" : "du");
            if (File.Exists(duSibling))
            {
                (string symlinkOut, int symlinkCode) = RunProbeProcess(duSibling, ["--version"]);
                if (symlinkCode == 0 && symlinkOut.Contains("du (hyperdu)", StringComparison.OrdinalIgnoreCase))
                {
                    duExecutable = duSibling;
                    usesSubcommand = false;
                    return true;
                }
            }
        }

        (string subOut, int subCode) = RunProbeProcess(hyperduBin, ["du", "--version"]);
        if (subCode == 0 && subOut.Contains("du (hyperdu)", StringComparison.OrdinalIgnoreCase))
        {
            duExecutable = hyperduBin;
            usesSubcommand = true;
            return true;
        }

        duExecutable = hyperduBin;
        usesSubcommand = false;
        return false;
    }

    private static (ToolSpec? Tool, string? Prefix) ResolveUutilsDu()
    {
        string? uuDu = FindOnPath("uu-du");
        if (uuDu != null)
        {
            return (new ToolSpec("uutils-du", "uutils du", ComparatorTier.Tier1Parity, uuDu, EmptyEnv, false, true), null);
        }

        string? coreutils = FindOnPath("coreutils");
        if (coreutils != null)
        {
            (string ver, int code) = RunProbeProcess(coreutils, ["du", "--version"]);
            if (code == 0 && ver.Contains("uutils", StringComparison.OrdinalIgnoreCase))
            {
                return (new ToolSpec("uutils-du", "uutils du", ComparatorTier.Tier1Parity, coreutils, EmptyEnv, false, true), "du");
            }
        }

        return (null, null);
    }

    private static ToolSpec? ResolveGoGdu()
    {
        string? gdu = FindOnPath("gdu");
        if (gdu == null)
            return null;

        (string outText, int exitCode) = RunProbeProcess(gdu, ["--version"]);
        if (exitCode != 0 ||
            outText.Contains("GNU coreutils", StringComparison.OrdinalIgnoreCase) ||
            outText.Contains("hyperdu", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ToolSpec("gdu", "gdu (Go)", ComparatorTier.Tier2Workload, gdu, EmptyEnv, false, false);
    }

    private static ToolSpec? ResolveSimpleTool(string id, string command, string versionArg)
    {
        string? bin = FindOnPath(command);
        if (bin == null)
            return null;

        (_, int exitCode) = RunProbeProcess(bin, [versionArg]);
        if (exitCode != 0)
            return null;

        return new ToolSpec(id, command, ComparatorTier.Tier2Workload, bin, EmptyEnv, false, false);
    }

    internal static string? FindOnPath(string command)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                string full = Path.Combine(dir, command + ext);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    private static (string Stdout, int ExitCode) RunProbeProcess(string fileName, string[] args)
    {
        try
        {
            ProcessStartInfo psi = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using Process? proc = Process.Start(psi);
            if (proc == null)
                return (string.Empty, -1);

            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(true); } catch { }
                return (string.Empty, -1);
            }

            return (stdout, proc.ExitCode);
        }
        catch
        {
            return (string.Empty, -1);
        }
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int nl = text.IndexOfAny(['\r', '\n']);
        return (nl >= 0 ? text[..nl] : text).Trim();
    }
}
