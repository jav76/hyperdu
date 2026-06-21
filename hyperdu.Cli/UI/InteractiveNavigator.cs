using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Cli.UI;

public class InteractiveNavigator
{
    private readonly DirectoryNode _root;
    private DirectoryNode _current;
    private readonly Stack<DirectoryNode> _history = new();
    private int _selectedIndex = 0;
    private const int PageSize = 15; // Limit the number of visible items to avoid scroll overflow in small terminals
    private int _scrollOffset = 0;
    private readonly ParallelScanner? _scanner;

    private string? _lastPriorityCurrentPath;
    private string? _lastPrioritySelectedPath;

    public InteractiveNavigator(DirectoryNode root, ParallelScanner? scanner = null)
    {
        _root = root;
        _current = root;
        _scanner = scanner;
    }

    public void Run()
    {
        // Suppress cursor
        AnsiConsole.Cursor.Show(false);
        try
        {
            var items = BuildItemList();
            var renderer = new ExplorerRenderer();

            var initialLayout = CreateCombinedLayout(renderer, items);

            AnsiConsole.Live(initialLayout)
                .StartAsync(async ctx =>
                {
                    bool dirChanged = false;
                    while (true)
                    {
                        // Update scanner priority dynamically if scanning is active
                        if (_scanner != null && _scanner.IsScanning)
                        {
                            UpdateScannerPriority(items);
                        }

                        // Check key presses without blocking
                        while (Console.KeyAvailable)
                        {
                            var keyInfo = Console.ReadKey(intercept: true);
                            if (keyInfo.Key == ConsoleKey.Q || keyInfo.Key == ConsoleKey.Escape)
                            {
                                return; // Quit
                            }

                            if (HandleKey(keyInfo.Key, items))
                            {
                                dirChanged = true;
                            }
                        }

                        if (dirChanged || (_scanner != null && _scanner.IsScanning))
                        {
                            string? selectedName = (items != null && _selectedIndex >= 0 && _selectedIndex < items.Count) ? items[_selectedIndex].Name : null;
                            bool isParentLink = (items != null && _selectedIndex >= 0 && _selectedIndex < items.Count) && items[_selectedIndex].IsParentLink;

                            items = BuildItemList();

                            if (selectedName != null)
                            {
                                int newIndex = items.FindIndex(i => i.Name == selectedName && i.IsParentLink == isParentLink);
                                if (newIndex >= 0)
                                {
                                    _selectedIndex = newIndex;
                                    // Keep scroll offset bounds correct
                                    if (_selectedIndex < _scrollOffset)
                                    {
                                        _scrollOffset = _selectedIndex;
                                    }
                                    else if (_selectedIndex >= _scrollOffset + PageSize)
                                    {
                                        _scrollOffset = _selectedIndex - PageSize + 1;
                                    }
                                }
                                else
                                {
                                    // Clamp selection if item disappeared
                                    _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, items.Count - 1));
                                }
                            }
                            dirChanged = false;
                        }

                        var layout = CreateCombinedLayout(renderer, items);
                        ctx.UpdateTarget(layout);

                        // Sleep 100ms
                        await Task.Delay(100);
                    }
                }).GetAwaiter().GetResult();
        }
        finally
        {
            AnsiConsole.Cursor.Show(true);
            AnsiConsole.Clear();
            AnsiConsole.Write(new Markup("[bold green]Thank you for using hyperdu![/]\n"));
        }
    }

    private IRenderable CreateCombinedLayout(ExplorerRenderer renderer, List<NavigatorItem> items)
    {
        var explorerTable = renderer.Render(_current, items, _selectedIndex, _scrollOffset, PageSize, _scanner);
        if (_scanner == null || !_scanner.IsScanning)
        {
            return explorerTable;
        }

        var progressPanel = CreateProgressPanel(_scanner);
        return new Rows(progressPanel, explorerTable);
    }

    private Panel CreateProgressPanel(ParallelScanner scanner)
    {
        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("Col1");
        table.AddColumn("Col2");
        table.AddColumn("Col3");

        string statusText = scanner.IsScanning ? "[bold yellow]Scanning...[/]" : "[bold green]Completed[/]";
        table.AddRow(
            new Markup($"[bold grey]Dirs:[/] [bold cyan]{scanner.DirectoriesScanned:n0}[/]"),
            new Markup($"[bold grey]Files:[/] [bold cyan]{scanner.FilesScanned:n0}[/]"),
            new Markup($"[bold grey]Status:[/] {statusText}")
        );
        table.AddRow(
            new Markup($"[bold grey]Size:[/] [bold green]{ExplorerRenderer.FormatSize(scanner.TotalBytesFound)}[/]"),
            new Markup($"[bold grey]Threads:[/] [bold yellow]{scanner.ActiveWorkers}[/]"),
            new Markup("")
        );

        string currentPathDisp = scanner.CurrentDirectory;
        if (currentPathDisp.Length > 60)
        {
            currentPathDisp = "..." + currentPathDisp[^57..];
        }

        var panelContent = new Rows(
            table,
            new Markup($"[bold grey]Scanning Path:[/] [grey]{Markup.Escape(currentPathDisp)}[/]")
        );

        return new Panel(panelContent)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[yellow]Scan Progress[/]")
        };
    }

    private List<NavigatorItem> BuildItemList()
    {
        var list = new List<NavigatorItem>();

        // Add parent directory option if we aren't at root
        if (_current != _root && _history.Count > 0)
        {
            list.Add(new NavigatorItem
            {
                Name = "..",
                IsParentLink = true,
                Size = _history.Peek().TotalSize
            });
        }

        // Add subdirectories sorted by size
        List<DirectoryNode> sortedDirs;
        lock (_current.Subdirectories)
        {
            sortedDirs = _current.Subdirectories
                .OrderByDescending(d => d.TotalSize)
                .ToList();
        }

        foreach (var dir in sortedDirs)
        {
            list.Add(new NavigatorItem
            {
                Name = dir.Name,
                DirNode = dir,
                Size = dir.TotalSize
            });
        }

        // Add files sorted by size
        List<FileNode> sortedFiles;
        lock (_current.Files)
        {
            sortedFiles = _current.Files
                .OrderByDescending(f => f.Size)
                .ToList();
        }

        foreach (var file in sortedFiles)
        {
            list.Add(new NavigatorItem
            {
                Name = file.Name,
                FileNode = file,
                Size = file.Size
            });
        }

        return list;
    }

    private bool HandleKey(ConsoleKey key, List<NavigatorItem> items)
    {
        bool dirChanged = false;

        switch (key)
        {
            case ConsoleKey.UpArrow:
                if (_selectedIndex > 0)
                {
                    _selectedIndex--;
                    if (_selectedIndex < _scrollOffset)
                    {
                        _scrollOffset = _selectedIndex;
                    }
                }
                break;

            case ConsoleKey.DownArrow:
                if (_selectedIndex < items.Count - 1)
                {
                    _selectedIndex++;
                    if (_selectedIndex >= _scrollOffset + PageSize)
                    {
                        _scrollOffset = _selectedIndex - PageSize + 1;
                    }
                }
                break;

            case ConsoleKey.Enter:
                var selected = items[_selectedIndex];
                if (selected.IsParentLink)
                {
                    AscendDirectory();
                    dirChanged = true;
                }
                else if (selected.DirNode != null)
                {
                    DescendDirectory(selected.DirNode);
                    dirChanged = true;
                }
                break;

            case ConsoleKey.Backspace:
            case ConsoleKey.LeftArrow:
                if (_current != _root && _history.Count > 0)
                {
                    AscendDirectory();
                    dirChanged = true;
                }
                break;
        }

        return dirChanged;
    }

    private void DescendDirectory(DirectoryNode subDir)
    {
        _history.Push(_current);
        _current = subDir;
        _selectedIndex = 0;
        _scrollOffset = 0;
    }

    private void AscendDirectory()
    {
        if (_history.Count > 0)
        {
            var previous = _current;
            _current = _history.Pop();
            
            // Re-build items to find the folder we just exited, so we can focus it for a better UX!
            var items = BuildItemList();
            _selectedIndex = items.FindIndex(i => i.DirNode == previous);
            if (_selectedIndex < 0) _selectedIndex = 0;

            // Adjust scroll offset
            if (_selectedIndex >= PageSize)
            {
                _scrollOffset = _selectedIndex - PageSize / 2;
                if (_scrollOffset + PageSize > items.Count)
                {
                    _scrollOffset = Math.Max(0, items.Count - PageSize);
                }
            }
            else
            {
                _scrollOffset = 0;
            }
        }
    }

    public static bool IsScanningRecursive(DirectoryNode node)
    {
        if (!node.IsScanned)
        {
            return true;
        }

        DirectoryNode[] subs;
        lock (node.Subdirectories)
        {
            if (node.Subdirectories.Count == 0)
            {
                return false;
            }
            subs = node.Subdirectories.ToArray();
        }

        for (int i = 0; i < subs.Length; i++)
        {
            if (IsScanningRecursive(subs[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSubPathOf(string path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix))
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(path, prefix, comparison))
            return true;

        string prefixWithSep = prefix;
        if (!prefix.EndsWith(Path.DirectorySeparatorChar) && !prefix.EndsWith(Path.AltDirectorySeparatorChar))
        {
            prefixWithSep = prefix + Path.DirectorySeparatorChar;
        }

        return path.StartsWith(prefixWithSep, comparison);
    }

    private void UpdateScannerPriority(List<NavigatorItem> items)
    {
        if (_scanner == null || !_scanner.IsScanning)
            return;

        string currentPath = _current.Path;
        string? selectedPath = null;

        if (_selectedIndex >= 0 && _selectedIndex < items.Count)
        {
            var selectedItem = items[_selectedIndex];
            if (selectedItem.DirNode != null)
            {
                selectedPath = selectedItem.DirNode.Path;
            }
        }

        if (currentPath == _lastPriorityCurrentPath && selectedPath == _lastPrioritySelectedPath)
        {
            return;
        }

        _lastPriorityCurrentPath = currentPath;
        _lastPrioritySelectedPath = selectedPath;

        _scanner.Queue.SetPriorityEvaluator(path =>
        {
            if (selectedPath != null && IsSubPathOf(path, selectedPath))
            {
                return 2;
            }
            if (IsSubPathOf(path, currentPath))
            {
                return 1;
            }
            return 0;
        });
    }
}

public class NavigatorItem
{
    public required string Name { get; init; }
    public DirectoryNode? DirNode { get; init; }
    public FileNode? FileNode { get; init; }
    public bool IsParentLink { get; init; }
    public long Size { get; init; }
}

public class ExplorerRenderer
{
    private static readonly string[] SpinnerFrames = { "⠋⠙", "⠙⠹", "⠹⠸", "⠸⠼", "⠼⠴", "⠴⠦", "⠦⠧", "⠧⠇", "⠇⠏", "⠏⠋" };

    private string? _lastDirPath;
    private int _lastSubdirs;
    private int _lastFiles;
    private long _lastSize;

    private int _heldAddSubdirs;
    private int _heldAddFiles;
    private long _heldAddSize;

    private int _decaySubdirs;
    private int _decayFiles;
    private int _decaySize;

    public Table Render(DirectoryNode current, List<NavigatorItem> items, int selectedIndex, int scrollOffset, int pageSize, ParallelScanner? scanner)
    {
        int spinnerIndex = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 100) % SpinnerFrames.Length);
        string spinner = SpinnerFrames[spinnerIndex];

        string titleSpinner = "";
        if (scanner != null && scanner.IsScanning && InteractiveNavigator.IsScanningRecursive(current))
        {
            titleSpinner = $"[bold orange3]»[/] [yellow]{spinner}[/]";
        }

        string spinnerDisp = string.IsNullOrEmpty(titleSpinner) ? "" : $" {titleSpinner}";
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.DeepSkyBlue4)
            .Title($"[bold yellow]hyperdu Explorer[/] - [cyan]{Markup.Escape(current.Path)}[/]{spinnerDisp}");

        int currentSubdirs = current.SubdirectoryCount;
        int currentFiles = current.FileCount;
        long currentSize = current.TotalSize;

        if (current.Path != _lastDirPath)
        {
            _lastDirPath = current.Path;
            _lastSubdirs = currentSubdirs;
            _lastFiles = currentFiles;
            _lastSize = currentSize;

            _heldAddSubdirs = 0;
            _heldAddFiles = 0;
            _heldAddSize = 0;

            _decaySubdirs = 0;
            _decayFiles = 0;
            _decaySize = 0;
        }
        else
        {
            // Subdirectories
            int diffSubdirs = currentSubdirs - (_lastSubdirs + _heldAddSubdirs);
            if (diffSubdirs > 0)
            {
                _lastSubdirs += _heldAddSubdirs;
                _heldAddSubdirs = diffSubdirs;
                _decaySubdirs = 15;
            }
            else
            {
                if (_decaySubdirs > 0)
                {
                    _decaySubdirs--;
                    if (_decaySubdirs == 0)
                    {
                        _lastSubdirs += _heldAddSubdirs;
                        _heldAddSubdirs = 0;
                    }
                }
            }

            // Files
            int diffFiles = currentFiles - (_lastFiles + _heldAddFiles);
            if (diffFiles > 0)
            {
                _lastFiles += _heldAddFiles;
                _heldAddFiles = diffFiles;
                _decayFiles = 15;
            }
            else
            {
                if (_decayFiles > 0)
                {
                    _decayFiles--;
                    if (_decayFiles == 0)
                    {
                        _lastFiles += _heldAddFiles;
                        _heldAddFiles = 0;
                    }
                }
            }

            // Size
            long diffSize = currentSize - (_lastSize + _heldAddSize);
            if (diffSize > 0)
            {
                _lastSize += _heldAddSize;
                _heldAddSize = diffSize;
                _decaySize = 15;
            }
            else
            {
                if (_decaySize > 0)
                {
                    _decaySize--;
                    if (_decaySize == 0)
                    {
                        _lastSize += _heldAddSize;
                        _heldAddSize = 0;
                    }
                }
            }

            // Fallback safety if values decrease (e.g. deletion or recount)
            if (currentSubdirs < _lastSubdirs)
            {
                _lastSubdirs = currentSubdirs;
                _heldAddSubdirs = 0;
                _decaySubdirs = 0;
            }
            if (currentFiles < _lastFiles)
            {
                _lastFiles = currentFiles;
                _heldAddFiles = 0;
                _decayFiles = 0;
            }
            if (currentSize < _lastSize)
            {
                _lastSize = currentSize;
                _heldAddSize = 0;
                _decaySize = 0;
            }
        }

        string subdirsStr = _heldAddSubdirs > 0 
            ? $"{_lastSubdirs}+{_heldAddSubdirs}" 
            : $"{_lastSubdirs}";

        string filesStr = _heldAddFiles > 0 
            ? $"{_lastFiles}+{_heldAddFiles}" 
            : $"{_lastFiles}";

        string sizeStr = _heldAddSize > 0 
            ? $"{FormatSize(_lastSize)} +{FormatSize(_heldAddSize)}" 
            : $"{FormatSize(_lastSize)}";

        string statsColor = (scanner != null && scanner.IsScanning && InteractiveNavigator.IsScanningRecursive(current)) ? "grey" : "#ffffff";
        string caption = $"[{statsColor}]Total Size: {sizeStr} | Subdirectories: {subdirsStr} | Files: {filesStr}[/]";
        if (statsColor == "#ffffff")
        {
            caption += $"\n[bold white]Scan Time: {FormatTime(current.TotalScanTime)}[/]";
        }
        caption += $"\n[bold green]↑/↓[/] Navigate | [bold green]Enter[/] Open | [bold green]Backspace/←[/] Up | [bold green]Q/Esc[/] Exit";

        table.AddColumn("[bold]Sel[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Size[/]");
        table.AddColumn("[bold]Percentage[/]");
        table.AddColumn("[bold]Visual Usage[/]");

        long parentTotalSize = current.TotalSize > 0 ? current.TotalSize : 1;
        int visibleCount = Math.Min(pageSize, items.Count - scrollOffset);

        for (int i = 0; i < visibleCount; i++)
        {
            int index = scrollOffset + i;
            var item = items[index];
            bool isSelected = index == selectedIndex;

            string selectMarker = isSelected ? "[bold green]>[/]" : " ";

            string rawDisplayName;
            if (item.IsParentLink)
            {
                rawDisplayName = "[bold yellow].. (Parent Directory)[/]";
            }
            else if (item.DirNode != null)
            {
                string err = item.DirNode.ErrorMessage != null ? $" [red]({item.DirNode.ErrorMessage})[/]" : "";
                string scanStatus = "";
                if (scanner != null && scanner.IsScanning && InteractiveNavigator.IsScanningRecursive(item.DirNode))
                {
                    bool parentSelected = selectedIndex >= 0 && selectedIndex < items.Count && items[selectedIndex].IsParentLink;
                    if (parentSelected || isSelected)
                    {
                        scanStatus = $" [bold orange3]»[/] [yellow]{spinner}[/]";
                    }
                    else
                    {
                        scanStatus = $" [yellow]{spinner}[/]";
                    }
                }
                rawDisplayName = $"[bold blue]📁 {Markup.Escape(item.DirNode.Name)}[/]{scanStatus}{err}";
            }
            else if (item.FileNode != null)
            {
                rawDisplayName = $"[grey]📄 {Markup.Escape(item.FileNode.Name)}[/]";
            }
            else
            {
                rawDisplayName = Markup.Escape(item.Name);
            }

            string displayName = isSelected ? $"[bold reverse]{rawDisplayName}[/]" : rawDisplayName;

            // Calculate percentage of current directory's total size
            double percent = (double)item.Size / parentTotalSize * 100;
            string percentStr = item.IsParentLink ? "-" : $"{percent:F1}%";

            // Visual bar graph
            string bar = string.Empty;
            if (!item.IsParentLink)
            {
                int barWidth = 10;
                int filledWidth = (int)Math.Round(percent / 100 * barWidth);
                filledWidth = Math.Clamp(filledWidth, 0, barWidth);
                string filled = new string('█', filledWidth);
                string empty = new string('░', barWidth - filledWidth);
                
                string barColor = percent switch
                {
                    > 70 => "red",
                    > 40 => "yellow",
                    _ => "green"
                };

                bar = $"[{barColor}]{filled}[/][grey]{empty}[/]";
            }

            table.AddRow(
                new Markup(selectMarker),
                new Markup(displayName),
                new Markup(FormatSize(item.Size)),
                new Markup(percentStr),
                new Markup(bar)
            );
        }

        // Show a scroll indicator if there are more items
        if (items.Count > pageSize)
        {
            caption = $"[grey]Showing {scrollOffset + 1}-{scrollOffset + visibleCount} of {items.Count} items. (Scroll active)[/]\n" + caption;
        }

        table.Caption = new TableTitle(caption, Style.Plain);

        return table;
    }

    public static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n2} {suffixes[counter]}";
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time.TotalMilliseconds < 1)
        {
            return "< 1 ms";
        }
        if (time.TotalSeconds < 1)
        {
            return $"{time.TotalMilliseconds:F1} ms";
        }
        if (time.TotalMinutes < 1)
        {
            return $"{time.TotalSeconds:F2} s";
        }
        return $"{time.Minutes}m {time.Seconds}s {time.Milliseconds}ms";
    }
}
