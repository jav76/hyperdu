namespace hyperdu.Benchmarks.Cli;

internal sealed class BenchmarkCliOptions
{
    public BenchmarkSuite Suite { get; init; } = BenchmarkSuite.Standard;
    public List<string> CustomPaths { get; init; } = new();
    public bool NoSynthetic { get; init; }
    public bool ColdCache { get; init; }
    public string? HyperduBinary { get; init; }
    public string? DuBinary { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "benchmarks");
    public int? Warmup { get; init; }
    public int? Runs { get; init; }
    public string? Filter { get; init; }
    public bool NoHyperfine { get; init; }
    public bool KeepFixtures { get; init; }
    public bool ShowHelp { get; init; }

    public int EffectiveWarmup => Warmup ?? (Suite == BenchmarkSuite.Full ? 1 : 2);
    public int EffectiveRuns => Runs ?? (Suite == BenchmarkSuite.Full ? 3 : 10);

    /// <summary>Parses CLI arguments for the benchmark harness.</summary>
    public static BenchmarkCliOptions Parse(string[] args)
    {
        BenchmarkSuite suite = BenchmarkSuite.Standard;
        List<string> customPaths = new();
        bool noSynthetic = false;
        bool coldCache = false;
        string? hyperduBin = null;
        string? duBin = null;
        string outputDir = Path.Combine("artifacts", "benchmarks");
        int? warmup = null;
        int? runs = null;
        string? filter = null;
        bool noHyperfine = false;
        bool keepFixtures = false;
        bool showHelp = false;

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    i++;
                    break;
                case "--suite":
                    suite = ParseSuite(RequireValue(args, ref i, "--suite"));
                    i++;
                    break;
                case "--path":
                    customPaths.Add(RequireValue(args, ref i, "--path"));
                    i++;
                    break;
                case "--no-synthetic":
                    noSynthetic = true;
                    i++;
                    break;
                case "--cold-cache":
                    coldCache = true;
                    i++;
                    break;
                case "--hyperdu-bin":
                    hyperduBin = RequireValue(args, ref i, "--hyperdu-bin");
                    i++;
                    break;
                case "--du-bin":
                    duBin = RequireValue(args, ref i, "--du-bin");
                    i++;
                    break;
                case "--output-dir":
                    outputDir = RequireValue(args, ref i, "--output-dir");
                    i++;
                    break;
                case "--warmup":
                    warmup = ParseNonNegativeInt(RequireValue(args, ref i, "--warmup"), "--warmup");
                    i++;
                    break;
                case "--runs":
                    runs = ParsePositiveInt(RequireValue(args, ref i, "--runs"), "--runs");
                    i++;
                    break;
                case "--filter":
                    filter = RequireValue(args, ref i, "--filter");
                    i++;
                    break;
                case "--no-hyperfine":
                    noHyperfine = true;
                    i++;
                    break;
                case "--keep-fixtures":
                    keepFixtures = true;
                    i++;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return new BenchmarkCliOptions
        {
            Suite = suite,
            CustomPaths = customPaths,
            NoSynthetic = noSynthetic,
            ColdCache = coldCache,
            HyperduBinary = hyperduBin,
            DuBinary = duBin,
            OutputDirectory = outputDir,
            Warmup = warmup,
            Runs = runs,
            Filter = filter,
            NoHyperfine = noHyperfine,
            KeepFixtures = keepFixtures,
            ShowHelp = showHelp
        };
    }

    public static string GetHelpText() =>
        """
        hyperdu.Benchmarks - Automated CLI and microbenchmark suite

        Usage:
          hyperdu.Benchmarks [cli] [options]
          hyperdu.Benchmarks micro [BenchmarkDotNet args]

        CLI Options:
          --suite <quick|standard|full>  Synthetic corpus scale (default: standard)
          --path <dir>                   Benchmark a real directory/mount (repeatable)
          --no-synthetic                 Skip synthetic fixtures (use with --path)
          --cold-cache                   Drop OS page caches before each timing run
          --hyperdu-bin <path>           Path to compiled hyperdu binary
          --du-bin <path>                Path to reference du binary (auto-detected)
          --output-dir <dir>             Output directory for results.json and report.md
          --warmup <N>                   Warmup runs per command
          --runs <N>                     Measured runs per command
          --filter <text>                Run only scenarios matching <text>
          --no-hyperfine                 Force built-in Stopwatch timing engine
          --keep-fixtures                Preserve generated synthetic fixtures after run
          -h, --help                     Show this help
        """;

    private static string RequireValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {flag}.");

        index++;
        return args[index];
    }

    private static BenchmarkSuite ParseSuite(string value) => value.ToLowerInvariant() switch
    {
        "quick" => BenchmarkSuite.Quick,
        "standard" => BenchmarkSuite.Standard,
        "full" => BenchmarkSuite.Full,
        _ => throw new ArgumentException($"Invalid suite '{value}'. Expected quick, standard, or full.")
    };

    private static int ParseNonNegativeInt(string value, string flag)
    {
        if (!int.TryParse(value, out int parsed) || parsed < 0)
            throw new ArgumentException($"Invalid non-negative integer '{value}' for {flag}.");
        return parsed;
    }

    private static int ParsePositiveInt(string value, string flag)
    {
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
            throw new ArgumentException($"Invalid positive integer '{value}' for {flag}.");
        return parsed;
    }
}
