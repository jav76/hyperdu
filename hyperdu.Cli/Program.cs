using System.Globalization;
using System.Reflection;
using System.Text;
using hyperdu.Cli.UI;
using hyperdu.Core.Du;
using hyperdu.Core.Native;
using hyperdu.Core.Scanning;
using Spectre.Console;

namespace hyperdu.Cli;

public static class Program
{
    private const int OptApparentSize = 256;
    private const int OptExclude = 257;
    private const int OptSi = 258;
    private const int OptSkipHidden = 259;
    private const int OptHelp = 260;
    private const int OptNoUi = 261;

    private const string ShortOptions = "j:t:hbLlxVve:X:";

    private const string LegacyVersionMigrationMessage =
        "'-v' was renamed to '-V' (or '--version'); use '-V' to print version";

    private const string LegacyThreadsMigrationNote =
        "Note: worker thread count moved from '-t <N>' to '-j <N>' (or '--threads <N>').\n";

    private const string LegacyExcludeMigrationMessage =
        "'-e' was replaced by glob '--exclude=PATTERN' (or '-X FILE'); migrate '-e <path>' to '--exclude=<pattern>'";

    private static readonly LongOption[] LongOptions =
    [
        new("apparent-size", ArgumentRequirement.NoArgument, OptApparentSize),
        new("bytes", ArgumentRequirement.NoArgument, 'b'),
        new("count-links", ArgumentRequirement.NoArgument, 'l'),
        new("dereference", ArgumentRequirement.NoArgument, 'L'),
        new("exclude", ArgumentRequirement.RequiredArgument, OptExclude),
        new("exclude-from", ArgumentRequirement.RequiredArgument, 'X'),
        new("help", ArgumentRequirement.NoArgument, OptHelp),
        new("human-readable", ArgumentRequirement.NoArgument, 'h'),
        new("no-ui", ArgumentRequirement.NoArgument, OptNoUi),
        new("one-file-system", ArgumentRequirement.NoArgument, 'x'),
        new("si", ArgumentRequirement.NoArgument, OptSi),
        new("skip-hidden", ArgumentRequirement.NoArgument, OptSkipHidden),
        new("threads", ArgumentRequirement.RequiredArgument, 'j'),
        new("threshold", ArgumentRequirement.RequiredArgument, 't'),
        new("version", ArgumentRequirement.NoArgument, 'V')
    ];

    internal sealed record TuiCliOptions(
        string TargetPath,
        int? CustomThreads,
        bool SkipHidden,
        bool ApparentSize,
        bool CountLinks,
        bool OneFileSystem,
        bool FollowSymlinks,
        ThresholdSpec? Threshold,
        BlockSizeSpec DisplayBlockSize,
        bool HasExplicitHumanUnit,
        ExcludeMatcher Excludes,
        List<string> ExcludeSummary,
        bool NoUi = false
    )
    {
        internal ScanOptions CreateScanOptions(int workerCount, bool useNativeIo)
        {
            ScanOptions scan = new()
            {
                WorkerCount = workerCount,
                SkipHidden = SkipHidden,
                ApparentSize = ApparentSize,
                CountLinks = CountLinks,
                OneFileSystem = OneFileSystem,
                FollowSymlinks = FollowSymlinks,
                Threshold = Threshold,
                DisplayBlockSize = DisplayBlockSize,
                UseNativeIo = useNativeIo
            };
            scan.ExcludeMatcher.CopyFrom(Excludes);
            return scan;
        }
    }

    internal static DuParseOutcome ParseArguments(
        string[] args,
        string programName,
        string version,
        Stream stdin,
        Stream stdout,
        Stream stderr,
        out TuiCliOptions? options)
    {
        options = null;

        using MemoryStream getoptErr = new();
        if (!GetoptLong.TryParse(args, ShortOptions, LongOptions, programName, getoptErr, out List<ParsedOption> opts, out List<string> operands))
        {
            byte[] errBytes = getoptErr.ToArray();
            stderr.Write(errBytes);
            AppendLegacyMigrationHint(errBytes, stderr);
            Messages.WriteTryHelp(stderr, programName);
            return DuParseOutcome.ExitFailure;
        }

        int? customThreads = null;
        bool skipHidden = false;
        bool apparentSize = false;
        bool countLinks = false;
        bool oneFileSystem = false;
        bool followSymlinks = false;
        ThresholdSpec? threshold = null;
        bool? humanOverride = null;
        bool hasExplicitBytes = false;
        ExcludeMatcher excludes = new();
        List<string> excludeSummary = new();
        bool noUi = false;

        foreach (ParsedOption opt in opts)
        {
            switch (opt.Value)
            {
                case OptHelp:
                    Messages.WriteUtf8(stdout, FormatHelp(programName));
                    stdout.Flush();
                    return DuParseOutcome.ExitSuccess;

                case 'V':
                    Messages.WriteUtf8(stdout, $"{programName} {version}\n");
                    stdout.Flush();
                    return DuParseOutcome.ExitSuccess;

                case 'v':
                    Messages.WriteError(stderr, programName, LegacyVersionMigrationMessage);
                    Messages.WriteTryHelp(stderr, programName);
                    return DuParseOutcome.ExitFailure;

                case 'e':
                    Messages.WriteError(stderr, programName, LegacyExcludeMigrationMessage);
                    Messages.WriteTryHelp(stderr, programName);
                    return DuParseOutcome.ExitFailure;

                case 'j':
                {
                    string rawThreads = opt.Argument ?? string.Empty;
                    if (!int.TryParse(rawThreads, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedThreads) ||
                        parsedThreads < 1)
                    {
                        string optLabel = opt.LongName != null ? $"--{opt.LongName}" : "-j";
                        Messages.WriteError(stderr, programName, $"invalid {optLabel} argument {Messages.QuoteAscii(rawThreads)}");
                        Messages.WriteTryHelp(stderr, programName);
                        return DuParseOutcome.ExitFailure;
                    }

                    customThreads = parsedThreads;
                    break;
                }

                case 't':
                {
                    string rawThreshold = opt.Argument ?? string.Empty;
                    if (!BlockSize.TryParseThreshold(rawThreshold, out ThresholdSpec parsedThreshold, out BlockSizeParseStatus status))
                    {
                        string optLabel = opt.LongName != null ? $"--{opt.LongName}" : "-t";
                        Messages.WriteError(stderr, programName, BlockSize.FormatErrorMessage(optLabel, rawThreshold, status));
                        if (opt.LongName == null)
                            Messages.WriteUtf8(stderr, LegacyThreadsMigrationNote);
                        Messages.WriteTryHelp(stderr, programName);
                        return DuParseOutcome.ExitFailure;
                    }

                    threshold = parsedThreshold;
                    break;
                }

                case 'h':
                    humanOverride = true;
                    hasExplicitBytes = false;
                    break;

                case OptSi:
                    humanOverride = false;
                    hasExplicitBytes = false;
                    break;

                case 'b':
                    apparentSize = true;
                    hasExplicitBytes = true;
                    humanOverride = null;
                    break;

                case OptApparentSize:
                    apparentSize = true;
                    break;

                case 'l':
                    countLinks = true;
                    break;

                case 'x':
                    oneFileSystem = true;
                    break;

                case 'L':
                    followSymlinks = true;
                    break;

                case OptSkipHidden:
                    skipHidden = true;
                    break;

                case OptNoUi:
                    noUi = true;
                    break;

                case OptExclude:
                {
                    string pattern = opt.Argument ?? string.Empty;
                    excludes.AddPattern(pattern);
                    excludeSummary.Add(pattern);
                    break;
                }

                case 'X':
                {
                    string file = opt.Argument ?? string.Empty;
                    if (!excludes.TryAddFromFile(file, stdin, programName, stderr))
                        return DuParseOutcome.ExitFailure;

                    excludeSummary.Add($"@{file}");
                    break;
                }
            }
        }

        if (operands.Count > 1)
        {
            Messages.WriteError(stderr, programName, $"extra operand {Messages.QuoteAscii(operands[1])}");
            Messages.WriteTryHelp(stderr, programName);
            return DuParseOutcome.ExitFailure;
        }

        string targetPath = operands.Count == 1 ? operands[0] : Directory.GetCurrentDirectory();
        BlockSizeSpec displayBlockSize = hasExplicitBytes
            ? BlockSizeSpec.Bytes
            : humanOverride == false
                ? BlockSizeSpec.HumanSi
                : BlockSizeSpec.HumanBinary;

        options = new TuiCliOptions(
            targetPath,
            customThreads,
            skipHidden,
            apparentSize,
            countLinks,
            oneFileSystem,
            followSymlinks,
            threshold,
            displayBlockSize,
            humanOverride.HasValue,
            excludes,
            excludeSummary,
            noUi);
        return DuParseOutcome.Run;
    }

    private static void AppendLegacyMigrationHint(byte[] errBytes, Stream stderr)
    {
        string errText = Encoding.UTF8.GetString(errBytes);
        if (errText.Contains("-- 'v'", StringComparison.Ordinal))
        {
            Messages.WriteUtf8(stderr, $"Note: {LegacyVersionMigrationMessage}.\n");
            return;
        }

        if (errText.Contains("-- 'e'", StringComparison.Ordinal))
        {
            Messages.WriteUtf8(stderr, $"Note: {LegacyExcludeMigrationMessage}.\n");
            return;
        }

        if (errText.Contains("-- 't'", StringComparison.Ordinal))
        {
            Messages.WriteUtf8(stderr, LegacyThreadsMigrationNote);
        }
    }

    private static void PrintScannerConfig(TuiCliOptions parsed, ScanOptions options, bool isNetwork, bool isRotational)
    {
        if (!parsed.CustomThreads.HasValue)
        {
            if (isNetwork)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Network mount detected. Automatically optimized scanner workers to {options.WorkerCount} to hide latency.[/]");
            }
            else if (isRotational)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Rotational HDD detected. Automatically optimized scanner workers to {options.WorkerCount} to prevent disk head thrashing.[/]");
            }
        }

        string unitLabel = options.DisplayBlockSize.Mode switch
        {
            HumanMode.Si => "SI (powers of 1000)",
            HumanMode.None => "Bytes (-b)",
            _ => "Human binary (powers of 1024)"
        };
        string thresholdLabel = options.Threshold is ThresholdSpec t
            ? $"{(t.IsNegative ? "<=" : ">=")} {ExplorerRenderer.FormatSize(t.Value, options.DisplayBlockSize)}"
            : "None";

        AnsiConsole.MarkupLine($"Scanner Workers:  [green]{options.WorkerCount}[/]");
        AnsiConsole.MarkupLine($"Accounting:       [green]{(options.ApparentSize ? "Apparent Bytes" : "Allocated Blocks")}[/]");
        AnsiConsole.MarkupLine($"Display Units:    [green]{unitLabel}[/]");
        AnsiConsole.MarkupLine($"Threshold:        [green]{thresholdLabel}[/]");
        AnsiConsole.MarkupLine($"Skip Hidden:      [green]{options.SkipHidden}[/]");
        AnsiConsole.MarkupLine(
            $"Excluded:         [green]{(parsed.ExcludeSummary.Count > 0 ? Markup.Escape(string.Join(", ", parsed.ExcludeSummary)) : "None")}[/]");
        AnsiConsole.MarkupLine("Press [yellow]Ctrl+C[/] to cancel scan at any time.\n");
    }

    private static (ScanOptions Options, bool IsNetwork, bool IsRotational) BuildScanContext(
        TuiCliOptions parsed,
        string targetPath)
    {
        int finalWorkers = MountHeuristics.ResolveWorkerBudget(
            parsed.CustomThreads,
            targetPath,
            out bool isNetwork,
            out bool isRotational);

        ScanOptions options = parsed.CreateScanOptions(
            finalWorkers,
            Environment.GetEnvironmentVariable("HYPERDU_NATIVE_IO") != "0");

        return (options, isNetwork, isRotational);
    }

    private static void ConfigureThreadPool(int workerCount)
    {
        // Since scanner workers perform synchronous blocking I/O (stat/network latency),
        // adjust ThreadPool minimum threads to avoid slow ramp-up (ThreadPool starvation).
        ThreadPool.GetMinThreads(out int _, out int minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerCount, Environment.ProcessorCount), minCompletionPortThreads);
    }

    private static async Task<int> RunHeadlessScanAsync(TuiCliOptions parsed, string programName)
    {
        if (parsed.Excludes.IsExcludedManagedPath(parsed.TargetPath))
            return 0;

        string targetPath = parsed.TargetPath;
        if (!ParallelScanner.DirectoryExistsPathSafe(targetPath))
        {
            Console.Error.WriteLine($"{programName}: target directory does not exist: {targetPath}");
            return 1;
        }

        targetPath = Path.GetFullPath(targetPath);
        int initialWorkers = parsed.CustomThreads.HasValue
            ? Math.Max(1, parsed.CustomThreads.Value)
            : Environment.ProcessorCount;
        ScanOptions options = parsed.CreateScanOptions(
            initialWorkers,
            Environment.GetEnvironmentVariable("HYPERDU_NATIVE_IO") != "0");
        options.AutoDetectWorkerCount = !parsed.CustomThreads.HasValue;
        options.Headless = true;

        ParallelScanner scanner = new ParallelScanner(options);
        var rootNode = await scanner.ScanAsync(targetPath, null, CancellationToken.None);
        if (scanner.LastScanExcludedRoot)
            return 0;

        ulong totalUnsigned = (ulong)Math.Max(0L, rootNode.TotalSize);
        if (options.Threshold is ThresholdSpec t && !t.Matches(totalUnsigned))
            return 0;

        string formattedTotal = parsed.HasExplicitHumanUnit
            ? HumanFormat.Format(totalUnsigned, options.DisplayBlockSize)
            : rootNode.TotalSize.ToString(CultureInfo.InvariantCulture);
        Console.Write($"{formattedTotal}\t{targetPath}\n");
        return 0;
    }

    private static bool TryDispatchDuPersona(string[] args, out int exitCode)
    {
        string rawArgv0 = ReadRawArgv0();
        string baseName = Path.GetFileName(rawArgv0);
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^4];

        StringComparison personaCmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(baseName, "du", personaCmp) ||
            string.Equals(baseName, "gdu", personaCmp))
        {
            string programName = string.IsNullOrEmpty(rawArgv0) ? baseName : rawArgv0;
            exitCode = DuCommand.Run(args, programName, GetDisplayVersion());
            return true;
        }

        if (args.Length > 0 &&
            (string.Equals(args[0], "du", StringComparison.Ordinal) ||
             string.Equals(args[0], "gdu", StringComparison.Ordinal)))
        {
            exitCode = DuCommand.Run(args[1..], args[0], GetDisplayVersion());
            return true;
        }

        exitCode = 0;
        return false;
    }

    private static string ReadRawArgv0()
    {
        if (OperatingSystem.IsMacOS())
        {
            string? macArgv0 = NativeIo.TryGetMacOsRawArgv0();
            if (!string.IsNullOrEmpty(macArgv0))
                return macArgv0;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                using FileStream fs = File.OpenRead("/proc/self/cmdline");
                Span<byte> buf = stackalloc byte[512];
                int read = fs.Read(buf);
                if (read > 0)
                {
                    int nul = buf[..read].IndexOf((byte)0);
                    int len = nul >= 0 ? nul : read;
                    if (len > 0)
                        return Encoding.UTF8.GetString(buf[..len]);
                }
            }
            catch
            {
                // Fall back to managed argv[0] when /proc is unavailable.
            }
        }

        string[] cmdArgs = Environment.GetCommandLineArgs();
        return cmdArgs.Length > 0 ? cmdArgs[0] : "hyperdu";
    }

    public static int Main(string[] args)
    {
        if (TryDispatchDuPersona(args, out int duExitCode))
        {
            return duExitCode;
        }

        return RunTuiMainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunTuiMainAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        const string programName = "hyperdu";
        string version = GetDisplayVersion();

        DuParseOutcome outcome;
        TuiCliOptions? parsed;
        using (Stream stdin = Console.OpenStandardInput())
        using (Stream stdout = Console.OpenStandardOutput())
        using (Stream stderr = Console.OpenStandardError())
        {
            outcome = ParseArguments(args, programName, version, stdin, stdout, stderr, out parsed);
        }

        if (outcome == DuParseOutcome.ExitSuccess)
            return 0;

        if (outcome != DuParseOutcome.Run || parsed == null)
            return 1;

        if (parsed.NoUi || Environment.GetEnvironmentVariable("HYPERDU_NO_UI") == "1")
        {
            return await RunHeadlessScanAsync(parsed, programName);
        }

        AnsiConsole.Write(new FigletText("hyperdu").Color(Color.DeepSkyBlue4));
        AnsiConsole.Write(new Markup($"[bold teal]High-Performance Directory Space Analyzer v{Markup.Escape(version)}[/]\n\n"));

        string targetPath = parsed.TargetPath;

        if (!ParallelScanner.DirectoryExistsPathSafe(targetPath))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error: Target directory does not exist:[/] [white]{Markup.Escape(targetPath)}[/]");
            return 0;
        }

        targetPath = Path.GetFullPath(targetPath);
        AnsiConsole.MarkupLine($"Target Directory: [cyan]{Markup.Escape(targetPath)}[/]");

        (ScanOptions options, bool isNetwork, bool isRotational) = BuildScanContext(parsed, targetPath);

        PrintScannerConfig(parsed, options, isNetwork, isRotational);

        ConfigureThreadPool(options.WorkerCount);

        ParallelScanner scanner = new ParallelScanner(options);

        using CancellationTokenSource scanCts = new CancellationTokenSource();
        Task scanTask = Task.Run(async () =>
        {
            try
            {
                await scanner.ScanAsync(targetPath, null, scanCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when scan is canceled.
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]Fatal error during scan:[/] {Markup.Escape(ex.Message)}");
            }
        });

        while (scanner.RootNode == null)
        {
            await Task.Delay(10);
        }

        InteractiveNavigator navigator = new InteractiveNavigator(scanner.RootNode, scanner);
        navigator.Run();

        await scanCts.CancelAsync();
        try
        {
            await scanTask;
        }
        catch
        {
            // Ignore exceptions from task cancellation on cleanup.
        }

        return 0;
    }

    internal static string FormatHelp(string programName) =>
        $"""
        Usage: {programName} [OPTION]... [PATH]
          or:  {programName} du [OPTION]... [FILE]...
        Launch the interactive disk usage explorer on PATH (default: current directory).

        Options:
          -j, --threads=N         Number of concurrent scanner threads (default: auto)
              --apparent-size     Count apparent byte sizes rather than allocated disk blocks
          -b, --bytes             Equivalent to '--apparent-size' with exact byte display
          -h, --human-readable    Format sizes using powers of 1024 (default in explorer)
              --si                Format sizes using powers of 1000
          -t, --threshold=SIZE    Hide items smaller than SIZE if positive, or greater if negative
          -l, --count-links       Count hard-linked files multiple times
          -x, --one-file-system   Skip directories and targets on other filesystems
          -L, --dereference       Follow symbolic links
              --exclude=PATTERN   Skip files and directories matching glob PATTERN (repeatable)
          -X, --exclude-from=FILE Read exclude glob patterns from FILE (one per line)
              --skip-hidden       Skip hidden and system files/folders
              --no-ui             Scan headlessly and print the summary total without TUI
          -V, --version           Output version information and exit
              --help              Display this help and exit

        Legacy flag migration:
          -t <N> -> -j <N>   (-t now sets --threshold; thread count moved to -j/--threads)
          -v     -> -V       (or --version)
          -e     -> --exclude=PATTERN / -X FILE
          -h     -> --help   (-h now selects binary human-readable units)

        """;

    /// <summary>
    /// Formats "1.2.0+&lt;full sha&gt;" (SourceLink appends the commit) as "1.2.0 (abc1234)".
    /// </summary>
    private static string GetDisplayVersion()
    {
        string informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0-local";

        int plus = informational.IndexOf('+');
        if (plus < 0) return informational;

        string version = informational[..plus];
        string metadata = informational[(plus + 1)..];
        return metadata.Length >= 7 && metadata.All(Uri.IsHexDigit)
            ? $"{version} ({metadata[..7]})"
            : version;
    }
}