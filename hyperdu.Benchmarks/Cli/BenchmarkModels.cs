namespace hyperdu.Benchmarks.Cli;

internal enum BenchmarkSuite
{
    Quick,
    Standard,
    Full
}

internal enum ComparatorTier
{
    Tier1Parity,
    Tier2Workload
}

internal sealed record FixtureSpec(
    string Name,
    string RootPath,
    string Description,
    int ExpectedFiles,
    int ExpectedDirs,
    bool IsSynthetic
);

internal sealed record ToolSpec(
    string Id,
    string DisplayName,
    ComparatorTier Tier,
    string Executable,
    IReadOnlyDictionary<string, string> BaseEnvironment,
    bool IsReferenceDu,
    bool SupportsDuFlags,
    bool IsPending = false,
    string? PendingReason = null,
    IReadOnlyList<string>? FixedPrefixArguments = null
);

internal sealed record CommandInvocation(
    ToolSpec Tool,
    string[] Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables
);

internal sealed record ScenarioDefinition(
    string Id,
    string DisplayName,
    string FixtureName,
    string TargetPath,
    ComparatorTier Tier,
    string? DuFlags,
    IReadOnlyList<CommandInvocation> Invocations,
    bool RequireStrictStdoutParity = false,
    string? PendingNote = null
);

internal sealed record ProbeResult(
    int ExitCode,
    double? TtflMs,
    long PeakRssKb,
    long OutputLines,
    string StdoutHash,
    string StdoutSample,
    string StderrSample,
    bool ParityMatched,
    string ParityNote
);

internal sealed record TimingStats(
    double MeanMs,
    double? StdDevMs,
    double MedianMs,
    double MinMs,
    double MaxMs,
    double? UserCpuMs,
    double? SystemCpuMs,
    int Runs,
    string Engine
);

internal sealed record BenchmarkEntryResult(
    string ToolId,
    string ToolName,
    bool IsReferenceDu,
    TimingStats? Timing,
    ProbeResult? Probe,
    double? SpeedupVsReferenceDu,
    bool IsPending,
    string? PendingReason
);

internal sealed record ScenarioResult(
    string Id,
    string DisplayName,
    string FixtureName,
    string TargetPath,
    string Tier,
    IReadOnlyList<BenchmarkEntryResult> Entries,
    string? PendingNote
);

internal sealed record BenchmarkEnvironment(
    string OsDescription,
    string Architecture,
    string CpuModel,
    int LogicalCores,
    string ReferenceDuVersion,
    bool HasHyperfine,
    bool ColdCache,
    string TimingEngine,
    string GitCommit,
    DateTimeOffset TimestampUtc
);

internal sealed record BenchmarkReport(
    BenchmarkEnvironment Environment,
    string SuiteName,
    IReadOnlyList<FixtureSpec> Fixtures,
    IReadOnlyList<ScenarioResult> Scenarios
);
