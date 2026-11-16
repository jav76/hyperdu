using System.Reflection;
using System.Text;
using hyperdu.Cli.UI;
using hyperdu.Core.Scanning;
using Spectre.Console;

namespace hyperdu.Cli;

public static class Program
{
    private sealed record CommandLineOptions(
        string TargetPath,
        int? CustomThreads,
        bool SkipHidden,
        bool ApparentSize,
        bool CountLinks,
        bool OneFileSystem,
        bool FollowSymlinks,
        List<string> CustomExcludes,
        bool ShowHelp,
        bool ShowVersion
    );

    private static CommandLineOptions ParseArguments(string[] args)
    {
        string targetPath = Directory.GetCurrentDirectory();
        int? customThreads = null;
        bool skipHidden = false;
        bool apparentSize = false;
        bool countLinks = false;
        bool oneFileSystem = false;
        bool followSymlinks = false;
        List<string> customExcludes = new();
        bool showHelp = false;
        bool showVersion = false;

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            if (arg == "-h" || arg == "--help")
            {
                showHelp = true;
                i++;
            }
            else if (arg == "-v" || arg == "--version")
            {
                showVersion = true;
                i++;
            }
            else if ((arg == "-t" || arg == "--threads") && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int threads))
                {
                    customThreads = threads;
                }
                i += 2;
            }
            else if ((arg == "-e" || arg == "--exclude") && i + 1 < args.Length)
            {
                customExcludes.Add(args[i + 1]);
                i += 2;
            }
            else if (arg == "--skip-hidden")
            {
                skipHidden = true;
                i++;
            }
            else if (arg == "--apparent-size")
            {
                apparentSize = true;
                i++;
            }
            else if (arg == "-l" || arg == "--count-links")
            {
                countLinks = true;
                i++;
            }
            else if (arg == "-x" || arg == "--one-file-system")
            {
                oneFileSystem = true;
                i++;
            }
            else if (arg == "-L" || arg == "--dereference")
            {
                followSymlinks = true;
                i++;
            }
            else
            {
                targetPath = arg;
                i++;
            }
        }

        return new CommandLineOptions(
            targetPath,
            customThreads,
            skipHidden,
            apparentSize,
            countLinks,
            oneFileSystem,
            followSymlinks,
            customExcludes,
            showHelp,
            showVersion);
    }

    private static void PrintScannerConfig(ScanOptions options, bool isNetwork, bool isRotational, bool isCustomThreads)
    {
        if (!isCustomThreads)
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

        AnsiConsole.MarkupLine($"Scanner Workers:  [green]{options.WorkerCount}[/]");
        AnsiConsole.MarkupLine($"Skip Hidden:      [green]{options.SkipHidden}[/]");
        AnsiConsole.MarkupLine(
            $"Excluded Paths:   [green]{(options.ExcludedPaths.Count > 0 ? string.Join(", ", options.ExcludedPaths) : "None")}[/]");
        AnsiConsole.MarkupLine("Press [yellow]Ctrl+C[/] to cancel scan at any time.\n");
    }

    private static (ScanOptions Options, bool IsNetwork, bool IsRotational) BuildScanContext(
        CommandLineOptions parsed,
        string targetPath)
    {
        int finalWorkers = MountHeuristics.ResolveWorkerBudget(
            parsed.CustomThreads,
            targetPath,
            out bool isNetwork,
            out bool isRotational);

        ScanOptions options = new ScanOptions
        {
            WorkerCount = finalWorkers,
            SkipHidden = parsed.SkipHidden,
            ApparentSize = parsed.ApparentSize,
            CountLinks = parsed.CountLinks,
            OneFileSystem = parsed.OneFileSystem,
            FollowSymlinks = parsed.FollowSymlinks,
            UseNativeIo = Environment.GetEnvironmentVariable("HYPERDU_NATIVE_IO") != "0"
        };
        if (parsed.CustomExcludes.Count > 0)
        {
            options.ExcludedPaths = parsed.CustomExcludes;
        }

        return (options, isNetwork, isRotational);
    }

    private static void ConfigureThreadPool(int workerCount)
    {
        // Since scanner workers perform synchronous blocking I/O (stat/network latency),
        // adjust ThreadPool minimum threads to avoid slow ramp-up (ThreadPool starvation).
        ThreadPool.GetMinThreads(out int _, out int minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerCount, Environment.ProcessorCount), minCompletionPortThreads);
    }

    private static async Task<int> RunHeadlessScanAsync(CommandLineOptions parsed)
    {
        string targetPath = parsed.TargetPath;
        if (!ParallelScanner.DirectoryExistsPathSafe(targetPath))
        {
            Console.Error.WriteLine($"Error: Target directory does not exist: {targetPath}");
            return 1;
        }

        targetPath = Path.GetFullPath(targetPath);
        (ScanOptions options, _, _) = BuildScanContext(parsed, targetPath);
        ConfigureThreadPool(options.WorkerCount);

        ParallelScanner scanner = new ParallelScanner(options);
        var rootNode = await scanner.ScanAsync(targetPath, null, CancellationToken.None);
        Console.WriteLine($"{rootNode.TotalSize}\t{targetPath}");
        return 0;
    }

    private static bool TryDispatchDuPersona(string[] args, out int exitCode)
    {
        string rawArgv0 = ReadRawArgv0();
        string baseName = Path.GetFileName(rawArgv0);
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^4];

        if (string.Equals(baseName, "du", StringComparison.Ordinal) ||
            string.Equals(baseName, "gdu", StringComparison.Ordinal))
        {
            string programName = string.IsNullOrEmpty(rawArgv0) ? baseName : rawArgv0;
            exitCode = DuCommand.Run(args, programName, GetDisplayVersion());
            return true;
        }

        if (args.Length > 0 && string.Equals(args[0], "du", StringComparison.Ordinal))
        {
            exitCode = DuCommand.Run(args[1..], "du", GetDisplayVersion());
            return true;
        }

        exitCode = 0;
        return false;
    }

    private static string ReadRawArgv0()
    {
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

    public static async Task<int> Main(string[] args)
    {
        if (TryDispatchDuPersona(args, out int duExitCode))
        {
            return duExitCode;
        }

        Console.OutputEncoding = Encoding.UTF8;
        CommandLineOptions parsed = ParseArguments(args);

        if (parsed.ShowVersion)
        {
            PrintVersion();
            return 0;
        }

        if (parsed.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (Environment.GetEnvironmentVariable("HYPERDU_NO_UI") == "1")
        {
            return await RunHeadlessScanAsync(parsed);
        }

        AnsiConsole.Write(new FigletText("hyperdu").Color(Color.DeepSkyBlue4));
        AnsiConsole.Write(new Markup($"[bold teal]High-Performance Directory Space Analyzer v{Markup.Escape(GetDisplayVersion())}[/]\n\n"));

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

        PrintScannerConfig(options, isNetwork, isRotational, parsed.CustomThreads.HasValue);

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

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/] hyperdu [[path]] [[options]]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Options:[/]");
        AnsiConsole.MarkupLine("  [cyan]-h, --help[/]         Show this help text");
        AnsiConsole.MarkupLine("  [cyan]-v, --version[/]      Show version information");
        AnsiConsole.MarkupLine(
            "  [cyan]-t, --threads <N>[/]  Set the number of concurrent worker threads (default: CPU logical cores)");
        AnsiConsole.MarkupLine(
            "  [cyan]-e, --exclude <path>[/] Add a path to the exclusion list (can be specified multiple times. default: None)");
        AnsiConsole.MarkupLine("  [cyan]--skip-hidden[/]       Skip hidden and system files/folders");
        AnsiConsole.MarkupLine("  [cyan]--apparent-size[/]     Count apparent byte sizes rather than allocated disk blocks");
        AnsiConsole.MarkupLine("  [cyan]-l, --count-links[/]   Count hard-linked files multiple times");
        AnsiConsole.MarkupLine("  [cyan]-x, --one-file-system[/] Skip directories on different filesystems");
        AnsiConsole.MarkupLine("  [cyan]-L, --dereference[/]   Follow symbolic links");
    }

    // Plain Console output so package-manager smoke tests and scripts get one unwrapped line.
    private static void PrintVersion()
    {
        Console.WriteLine($"hyperdu {GetDisplayVersion()}");
    }

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