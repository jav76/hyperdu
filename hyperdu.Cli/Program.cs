using System;
using System.IO;
using System.Threading.Tasks;
using Spectre.Console;
using hyperdu.Core.Scanning;
using hyperdu.Core.Models;
using hyperdu.Cli.UI;

namespace hyperdu.Cli;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AnsiConsole.Write(new FigletText("hyperdu").Color(Color.DeepSkyBlue4));
        AnsiConsole.Write(new Markup("[bold teal]High-Performance Directory Space Analyzer[/]\n\n"));

        string targetPath = "/";
        int? customThreads = null;
        bool skipHidden = false;
        System.Collections.Generic.List<string>? customExcludes = null;

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-h" || args[i] == "--help")
            {
                PrintHelp();
                return;
            }
            else if ((args[i] == "-t" || args[i] == "--threads") && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int threads))
                {
                    customThreads = threads;
                }
                i++;
            }
            else if ((args[i] == "-e" || args[i] == "--exclude") && i + 1 < args.Length)
            {
                if (customExcludes == null)
                {
                    customExcludes = new System.Collections.Generic.List<string>();
                }
                customExcludes.Add(args[i + 1]);
                i++;
            }
            else if (args[i] == "--skip-hidden")
            {
                skipHidden = true;
            }
            else
            {
                targetPath = args[i];
            }
        }

        // Validate path
        if (!Directory.Exists(targetPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Target directory does not exist:[/] [white]{Markup.Escape(targetPath)}[/]");
            return;
        }

        targetPath = Path.GetFullPath(targetPath);
        AnsiConsole.MarkupLine($"Target Directory: [cyan]{Markup.Escape(targetPath)}[/]");

        bool isNetwork = IsNetworkMount(targetPath);
        int finalWorkers = customThreads ?? (isNetwork ? Math.Max(32, Environment.ProcessorCount * 2) : Environment.ProcessorCount);

        var options = new ScanOptions
        {
            WorkerCount = finalWorkers,
            SkipHidden = skipHidden,
            FollowSymlinks = false
        };
        if (customExcludes != null)
        {
            options.ExcludedPaths = customExcludes;
        }

        if (isNetwork && customThreads == null)
        {
            AnsiConsole.MarkupLine($"[yellow]Network mount detected. Automatically optimized scanner workers to {finalWorkers} to hide latency.[/]");
        }

        AnsiConsole.MarkupLine($"Scanner Workers:  [green]{options.WorkerCount}[/]");
        AnsiConsole.MarkupLine($"Skip Hidden:      [green]{options.SkipHidden}[/]");
        AnsiConsole.MarkupLine($"Excluded Paths:   [green]{(options.ExcludedPaths.Count > 0 ? string.Join(", ", options.ExcludedPaths) : "None")}[/]");
        AnsiConsole.MarkupLine("Press [yellow]Ctrl+C[/] to cancel scan at any time.\n");

        // Since scanner workers perform synchronous blocking I/O (stat/network latency),
        // adjust ThreadPool minimum threads to avoid slow ramp-up (ThreadPool starvation).
        System.Threading.ThreadPool.GetMinThreads(out int _, out int minCompletionPortThreads);
        System.Threading.ThreadPool.SetMinThreads(Math.Max(options.WorkerCount, Environment.ProcessorCount), minCompletionPortThreads);

        var scanner = new ParallelScanner(options);

        using var scanCts = new CancellationTokenSource();
        var scanTask = Task.Run(async () =>
        {
            try
            {
                await scanner.ScanAsync(targetPath, null, scanCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Clean cancel
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]Fatal error during scan:[/] {Markup.Escape(ex.Message)}");
            }
        });

        // Wait for scanner to initialize root node
        while (scanner.RootNode == null)
        {
            await Task.Delay(10);
        }

        // Run interactive CLI UI immediately
        var navigator = new InteractiveNavigator(scanner.RootNode, scanner);
        navigator.Run();

        // When navigator exits, clean up background scanning task
        scanCts.Cancel();
        try
        {
            await scanTask;
        }
        catch
        {
            // Ignore
        }
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/] hyperdu [[path]] [[options]]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Options:[/]");
        AnsiConsole.MarkupLine("  [cyan]-h, --help[/]         Show this help text");
        AnsiConsole.MarkupLine("  [cyan]-t, --threads <N>[/]  Set the number of concurrent worker threads (default: CPU logical cores)");
        AnsiConsole.MarkupLine("  [cyan]-e, --exclude <path>[/] Add a path to the exclusion list (can be specified multiple times. default: /mnt/)");
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

                if (fullPath.StartsWith(mountPoint, StringComparison.Ordinal))
                {
                    if (mountPoint.Length > bestMatchMount.Length)
                    {
                        bestMatchMount = mountPoint;
                        bestMatchFsType = fsType;
                    }
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
            // Ignore and fallback to false
        }
        return false;
    }
}
