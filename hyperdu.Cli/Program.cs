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
        List<string> CustomExcludes,
        bool ShowHelp
    );

    private static CommandLineOptions ParseArguments(string[] args)
    {
        string targetPath = "/";
        int? customThreads = null;
        bool skipHidden = false;
        List<string> customExcludes = new();
        bool showHelp = false;

        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            if (arg == "-h" || arg == "--help")
            {
                showHelp = true;
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
            else
            {
                targetPath = arg;
                i++;
            }
        }

        return new CommandLineOptions(targetPath, customThreads, skipHidden, customExcludes, showHelp);
    }

    private static int DetermineWorkerCount(CommandLineOptions parsed, bool isNetwork, bool isRotational)
    {
        if (parsed.CustomThreads.HasValue)
        {
            return parsed.CustomThreads.Value;
        }
        if (isNetwork)
        {
            return Math.Max(32, Environment.ProcessorCount * 2);
        }
        if (isRotational)
        {
            return 1;
        }
        return Environment.ProcessorCount;
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

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AnsiConsole.Write(new FigletText("hyperdu").Color(Color.DeepSkyBlue4));
        AnsiConsole.Write(new Markup("[bold teal]High-Performance Directory Space Analyzer[/]\n\n"));

        CommandLineOptions parsed = ParseArguments(args);

        if (parsed.ShowHelp)
        {
            PrintHelp();
            return;
        }

        string targetPath = parsed.TargetPath;

        if (!Directory.Exists(targetPath))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error: Target directory does not exist:[/] [white]{Markup.Escape(targetPath)}[/]");
            return;
        }

        targetPath = Path.GetFullPath(targetPath);
        AnsiConsole.MarkupLine($"Target Directory: [cyan]{Markup.Escape(targetPath)}[/]");

        bool isNetwork = IsNetworkMount(targetPath);
        bool isRotational = !isNetwork && DiskDeviceHelper.IsRotationalDrive(targetPath);

        int finalWorkers = DetermineWorkerCount(parsed, isNetwork, isRotational);

        ScanOptions options = new ScanOptions
        {
            WorkerCount = finalWorkers,
            SkipHidden = parsed.SkipHidden,
            FollowSymlinks = false
        };
        if (parsed.CustomExcludes.Count > 0)
        {
            options.ExcludedPaths = parsed.CustomExcludes;
        }

        PrintScannerConfig(options, isNetwork, isRotational, parsed.CustomThreads.HasValue);

        // Since scanner workers perform synchronous blocking I/O (stat/network latency),
        // adjust ThreadPool minimum threads to avoid slow ramp-up (ThreadPool starvation).
        ThreadPool.GetMinThreads(out int _, out int minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(options.WorkerCount, Environment.ProcessorCount), minCompletionPortThreads);

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
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/] hyperdu [[path]] [[options]]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Options:[/]");
        AnsiConsole.MarkupLine("  [cyan]-h, --help[/]         Show this help text");
        AnsiConsole.MarkupLine(
            "  [cyan]-t, --threads <N>[/]  Set the number of concurrent worker threads (default: CPU logical cores)");
        AnsiConsole.MarkupLine(
            "  [cyan]-e, --exclude <path>[/] Add a path to the exclusion list (can be specified multiple times. default: /mnt/)");
        AnsiConsole.MarkupLine("  [cyan]--skip-hidden[/]       Skip hidden and system files/folders");
    }

    private static bool IsNetworkMount(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists("/proc/mounts")) return false;

            string[] lines = File.ReadAllLines("/proc/mounts");
            string bestMatchMount = "";
            string bestMatchFsType = "";

            foreach (string line in lines)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                string mountPoint = parts[1];
                string fsType = parts[2];

                if (fullPath.StartsWith(mountPoint, StringComparison.Ordinal) && mountPoint.Length > bestMatchMount.Length)
                {
                    bestMatchMount = mountPoint;
                    bestMatchFsType = fsType;
                }
            }

            if (!string.IsNullOrEmpty(bestMatchFsType))
            {
                string fsLower = bestMatchFsType.ToLowerInvariant();
                return fsLower.Contains("nfs") ||
                       fsLower.Contains("cifs") ||
                       fsLower.Contains("smb") ||
                       fsLower.Contains("sshfs") ||
                       fsLower.Contains("davfs") ||
                       fsLower.Contains("glusterfs") ||
                       fsLower.Contains("ceph");
            }
        }
        catch
        {
            // Fallback to false if /proc/mounts cannot be read.
        }

        return false;
    }
}