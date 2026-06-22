using hyperdu.Core.Models;
using hyperdu.Core.Scanning;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace hyperdu.Cli.UI;

public class InteractiveNavigator
{
    private const int PageSize = 15; // Limit the number of visible items to avoid scroll overflow in small terminals
    private readonly Stack<DirectoryNode> _history = new();
    private DirectoryNode _root;
    private readonly ParallelScanner? _scanner;
    private DirectoryNode _current;

    private string? _cachedFileDirPath;
    private List<(string Name, long Size)>? _cachedFileList;
    private DirectoryNode? _lastPriorityCurrentNode;
    private DirectoryNode? _lastPrioritySelectedNode;
    private int _scrollOffset;
    private int _selectedIndex;
    private bool _needsRefresh;
    private CancellationTokenSource? _rescanCts;
    private ParallelScanner? _activeRescanScanner;

    public InteractiveNavigator(DirectoryNode root, ParallelScanner? scanner = null)
    {
        _root = root;
        _current = root;
        _scanner = scanner;
    }

    public void Run()
    {
        AnsiConsole.Cursor.Show(false);
        try
        {
            List<NavigatorItem> items = BuildItemList();
            ExplorerRenderer renderer = new ExplorerRenderer();

            IRenderable initialLayout = CreateCombinedLayout(renderer, items);

            AnsiConsole.Live(initialLayout)
                .StartAsync(async ctx =>
                {
                    while (true)
                    {
                        if ((_scanner != null && _scanner.IsScanning) || (_activeRescanScanner != null && _activeRescanScanner.IsScanning))
                        {
                            UpdateScannerPriority(items);
                        }

                        bool dirChanged = ProcessConsoleInput(items, out bool exit);
                        if (exit) return;

                        bool needsRefresh = _needsRefresh;
                        if (needsRefresh)
                        {
                            _needsRefresh = false;
                        }

                        if (dirChanged || (_scanner != null && _scanner.IsScanning) || (_activeRescanScanner != null && _activeRescanScanner.IsScanning) || needsRefresh)
                        {
                            AdjustSelectionAfterDirChange(ref items);
                        }

                        IRenderable layout = CreateCombinedLayout(renderer, items);
                        ctx.UpdateTarget(layout);

                        await Task.Delay(100);
                    }
                }).GetAwaiter().GetResult();
        }
        finally
        {
            _rescanCts?.Cancel();
            _rescanCts?.Dispose();
            AnsiConsole.Cursor.Show(true);
            AnsiConsole.Clear();
            AnsiConsole.Write(new Markup("[bold green]Thank you for using hyperdu![/]\n"));
        }
    }

    private bool ProcessConsoleInput(List<NavigatorItem> items, out bool exit)
    {
        bool dirChanged = false;
        exit = false;
        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Q || keyInfo.KeyChar == 'q' || keyInfo.KeyChar == 'Q' || keyInfo.Key == ConsoleKey.Escape)
            {
                exit = true;
                return false;
            }

            if (HandleKey(keyInfo, items))
            {
                dirChanged = true;
            }
        }
        return dirChanged;
    }

    private void AdjustSelectionAfterDirChange(ref List<NavigatorItem> items)
    {
        string? selectedName = items != null && _selectedIndex >= 0 && _selectedIndex < items.Count
            ? items[_selectedIndex].Name
            : null;
        bool isParentLink = items != null && _selectedIndex >= 0 && _selectedIndex < items.Count &&
                           items[_selectedIndex].IsParentLink;

        items = BuildItemList();

        if (selectedName != null)
        {
            int newIndex = items.FindIndex(i => i.Name == selectedName && i.IsParentLink == isParentLink);
            if (newIndex >= 0)
            {
                _selectedIndex = newIndex;
                KeepSelectionInView();
            }
            else
            {
                _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, items.Count - 1));
            }
        }
    }

    private void KeepSelectionInView()
    {
        if (_selectedIndex < _scrollOffset)
        {
            _scrollOffset = _selectedIndex;
        }
        else if (_selectedIndex >= _scrollOffset + PageSize)
        {
            _scrollOffset = _selectedIndex - PageSize + 1;
        }
    }

    private IRenderable CreateCombinedLayout(ExplorerRenderer renderer, List<NavigatorItem> items)
    {
        ParallelScanner? activeScanner = _activeRescanScanner ?? _scanner;
        Table explorerTable = renderer.Render(_current, items, _selectedIndex, _scrollOffset, PageSize, activeScanner);
        if (activeScanner == null || !activeScanner.IsScanning) return explorerTable;

        Panel progressPanel = CreateProgressPanel(activeScanner);
        return new Rows(progressPanel, explorerTable);
    }

    private static Panel CreateProgressPanel(ParallelScanner scanner)
    {
        Table table = new Table().NoBorder().HideHeaders();
        table.AddColumn("Col1");
        table.AddColumn("Col2");
        table.AddColumn("Col3");

        string statusText;
        if (scanner.IsPaused)
        {
            statusText = "[bold yellow]Paused[/]";
        }
        else if (scanner.IsScanning)
        {
            statusText = "[bold yellow]Scanning...[/]";
        }
        else
        {
            statusText = "[bold green]Completed[/]";
        }
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
        if (currentPathDisp.Length > 60) currentPathDisp = "..." + currentPathDisp[^57..];

        Rows panelContent = new Rows(
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
        List<NavigatorItem> list = new List<NavigatorItem>();

        if (_current != _root && _history.Count > 0)
            list.Add(new NavigatorItem
            {
                Name = "..",
                IsParentLink = true,
                Size = _history.Peek().TotalSize
            });

        List<DirectoryNode> sortedDirs;
        lock (_current.Subdirectories)
        {
            sortedDirs = _current.Subdirectories
                .OrderByDescending(d => d.TotalSize)
                .ToList();
        }

        foreach (DirectoryNode dir in sortedDirs)
            list.Add(new NavigatorItem
            {
                Name = dir.Name,
                DirNode = dir,
                Size = dir.TotalSize
            });

        if (_current.IsScanned)
        {
            List<(string Name, long Size)> files = GetCachedFiles(_current.Path);
            foreach ((string name, long size) in files)
                list.Add(new NavigatorItem
                {
                    Name = name,
                    IsFile = true,
                    Size = size
                });
        }

        return list;
    }

    private List<(string Name, long Size)> GetCachedFiles(string dirPath)
    {
        if (_cachedFileDirPath == dirPath && _cachedFileList != null)
            return _cachedFileList;

        List<(string Name, long Size)> files = new List<(string Name, long Size)>();
        try
        {
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            foreach (FileInfo file in dirInfo.EnumerateFiles())
            {
                long size = 0;
                try
                {
                    size = file.Length;
                }
                catch
                {
                    // Ignore exception when querying size of specific files (e.g. system files).
                }

                files.Add((file.Name, size));
            }

            files.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
        catch
        {
            // Ignore directory listing exceptions during file caching.
        }

        _cachedFileDirPath = dirPath;
        _cachedFileList = files;
        return files;
    }

    private bool HandleKey(ConsoleKeyInfo keyInfo, List<NavigatorItem> items)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                return MoveSelectionUp();

            case ConsoleKey.DownArrow:
                return MoveSelectionDown(items.Count);

            case ConsoleKey.Enter:
                return ExecuteSelection(items[_selectedIndex]);

            case ConsoleKey.Backspace:
            case ConsoleKey.LeftArrow:
                return AscendIfPossible();

            case ConsoleKey.Spacebar:
                TogglePause();
                return true;

            default:
                if (keyInfo.Key == ConsoleKey.R || keyInfo.KeyChar == 'r' || keyInfo.KeyChar == 'R')
                {
                    if (_scanner != null && (_activeRescanScanner == null || !_activeRescanScanner.IsScanning))
                    {
                        StartRescan(items);
                        return true;
                    }
                }
                return false;
        }
    }

    private void TogglePause()
    {
        ParallelScanner? activeScanner = _activeRescanScanner ?? _scanner;
        if (activeScanner == null || !activeScanner.IsScanning) return;

        if (activeScanner.IsPaused)
        {
            activeScanner.Resume();
        }
        else
        {
            activeScanner.Pause();
        }
        _needsRefresh = true;
    }

    private bool MoveSelectionUp()
    {
        if (_selectedIndex > 0)
        {
            _selectedIndex--;
            if (_selectedIndex < _scrollOffset)
            {
                _scrollOffset = _selectedIndex;
            }
        }
        return false;
    }

    private bool MoveSelectionDown(int itemsCount)
    {
        if (_selectedIndex < itemsCount - 1)
        {
            _selectedIndex++;
            if (_selectedIndex >= _scrollOffset + PageSize)
            {
                _scrollOffset = _selectedIndex - PageSize + 1;
            }
        }
        return false;
    }

    private bool ExecuteSelection(NavigatorItem selected)
    {
        if (selected.IsParentLink)
        {
            AscendDirectory();
            return true;
        }
        if (selected.DirNode != null)
        {
            DescendDirectory(selected.DirNode);
            return true;
        }
        return false;
    }

    private bool AscendIfPossible()
    {
        if (_current != _root && _history.Count > 0)
        {
            AscendDirectory();
            return true;
        }
        return false;
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
            DirectoryNode previous = _current;
            _current = _history.Pop();

            // Re-build items to find the folder we just exited, so we can focus it for a better UX!
            List<NavigatorItem> items = BuildItemList();
            _selectedIndex = items.FindIndex(i => i.DirNode == previous);
            if (_selectedIndex < 0) _selectedIndex = 0;

            if (_selectedIndex >= PageSize)
            {
                _scrollOffset = _selectedIndex - PageSize / 2;
                if (_scrollOffset + PageSize > items.Count) _scrollOffset = Math.Max(0, items.Count - PageSize);
            }
            else
            {
                _scrollOffset = 0;
            }
        }
    }

    public static bool IsScanningRecursive(DirectoryNode node)
    {
        if (!node.IsScanned) return true;

        DirectoryNode[] subs;
        lock (node.Subdirectories)
        {
            if (node.Subdirectories.Count == 0) return false;
            subs = node.Subdirectories.ToArray();
        }

        for (int i = 0; i < subs.Length; i++)
            if (IsScanningRecursive(subs[i]))
                return true;

        return false;
    }

    /// <summary>
    /// Tree-based ancestor check. Walks up the parent chain instead of comparing path strings.
    /// Faster than string comparison and eliminates string allocations.
    /// </summary>
    private static bool IsDescendantOrSelf(DirectoryNode node, DirectoryNode ancestor)
    {
        DirectoryNode? current = node;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current.Parent;
        }

        return false;
    }

    private void UpdateScannerPriority(List<NavigatorItem> items)
    {
        ParallelScanner? scanner = _activeRescanScanner ?? _scanner;
        if (scanner == null || !scanner.IsScanning)
            return;

        DirectoryNode? selectedNode = null;

        if (_selectedIndex >= 0 && _selectedIndex < items.Count)
        {
            NavigatorItem selectedItem = items[_selectedIndex];
            selectedNode = selectedItem.DirNode;
        }

        if (ReferenceEquals(_current, _lastPriorityCurrentNode) &&
            ReferenceEquals(selectedNode, _lastPrioritySelectedNode))
            return;

        _lastPriorityCurrentNode = _current;
        _lastPrioritySelectedNode = selectedNode;

        DirectoryNode capturedCurrent = _current;
        DirectoryNode? capturedSelected = selectedNode;

        scanner.Queue.SetPriorityEvaluator(node =>
        {
            if (capturedSelected != null && IsDescendantOrSelf(node, capturedSelected)) return 2;
            if (IsDescendantOrSelf(node, capturedCurrent)) return 1;
            return 0;
        });
    }

    private DirectoryNode? GetDirectoryToRescan(List<NavigatorItem> items)
    {
        if (_selectedIndex < 0 || _selectedIndex >= items.Count)
        {
            return _current;
        }

        NavigatorItem selected = items[_selectedIndex];
        if (selected.DirNode != null)
        {
            return selected.DirNode;
        }

        return _current;
    }

    private void ResetNodeStatistics(DirectoryNode node)
    {
        long oldSize = node.TotalSize;
        int oldFiles = node.FileCount;
        int oldSubdirs = node.SubdirectoryCount;

        node.TotalSize = 0;
        node.FileCount = 0;
        node.SubdirectoryCount = 0;
        node.SelfSize = 0;
        node.SelfFileCount = 0;
        node.IsScanned = false;
        node.ErrorMessage = null;

        lock (node.Subdirectories)
        {
            node.Subdirectories.Clear();
        }

        DirectoryNode? currentAncestor = node.Parent;
        while (currentAncestor != null)
        {
            currentAncestor.AddDelta(-oldSize, -oldSubdirs, -oldFiles);
            currentAncestor = currentAncestor.Parent;
        }
    }

    private void StartRescan(List<NavigatorItem> items)
    {
        DirectoryNode? targetNode = GetDirectoryToRescan(items);
        if (targetNode == null) return;

        _cachedFileDirPath = null;
        _cachedFileList = null;

        ResetNodeStatistics(targetNode);
        _needsRefresh = true;

        _rescanCts?.Cancel();
        _rescanCts?.Dispose();
        _rescanCts = new CancellationTokenSource();
        CancellationToken token = _rescanCts.Token;

        var rescanScanner = new ParallelScanner(_scanner?.Options);
        _activeRescanScanner = rescanScanner;

        Task.Run(async () =>
        {
            try
            {
                DirectoryNode newSubNode = await rescanScanner.ScanAsync(targetNode.Path, null, token);
                ReplaceNodeInTree(targetNode, newSubNode);
            }
            catch (Exception)
            {
                // Ignore cancellation and other scan errors.
            }
            finally
            {
                _needsRefresh = true;
                if (ReferenceEquals(_activeRescanScanner, rescanScanner))
                {
                    _activeRescanScanner = null;
                }
            }
        });
    }

    private void ReplaceNodeInTree(DirectoryNode oldNode, DirectoryNode newNode)
    {
        DirectoryNode? parent = oldNode.Parent;
        newNode.Parent = parent;

        long sizeDelta = newNode.TotalSize - oldNode.TotalSize;
        int filesDelta = newNode.FileCount - oldNode.FileCount;
        int subdirsDelta = newNode.SubdirectoryCount - oldNode.SubdirectoryCount;

        if (parent != null)
        {
            lock (parent.Subdirectories)
            {
                int index = parent.Subdirectories.IndexOf(oldNode);
                if (index >= 0)
                {
                    parent.Subdirectories[index] = newNode;
                }
            }

            DirectoryNode? currentAncestor = parent;
            while (currentAncestor != null)
            {
                currentAncestor.AddDelta(sizeDelta, subdirsDelta, filesDelta);
                currentAncestor = currentAncestor.Parent;
            }
        }
        else
        {
            _root = newNode;
        }

        if (ReferenceEquals(_current, oldNode))
        {
            _current = newNode;
        }
    }
}

public class NavigatorItem
{
    public required string Name { get; init; }
    public DirectoryNode? DirNode { get; init; }
    public bool IsFile { get; init; }
    public bool IsParentLink { get; init; }
    public long Size { get; init; }
}

public class ExplorerRenderer
{
    private static readonly string[] SpinnerFrames = { "⠋⠙", "⠙⠹", "⠹⠸", "⠸⠼", "⠼⠴", "⠴⠦", "⠦⠧", "⠧⠇", "⠇⠏", "⠏⠋" };
    private int _decayFiles;
    private int _decaySize;

    private int _decaySubdirs;
    private int _heldAddFiles;
    private long _heldAddSize;

    private int _heldAddSubdirs;

    private string? _lastDirPath;
    private int _lastFiles;
    private long _lastSize;
    private int _lastSubdirs;

    private record struct TableRenderContext(
        int ScrollOffset,
        int SelectedIndex,
        int PageSize,
        ParallelScanner? Scanner,
        string Spinner
    );

    public Table Render(DirectoryNode current, List<NavigatorItem> items, int selectedIndex, int scrollOffset,
        int pageSize, ParallelScanner? scanner)
    {
        int spinnerIndex = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 100 % SpinnerFrames.Length);
        string spinner = SpinnerFrames[spinnerIndex];

        string titleSpinner = "";
        if (scanner != null && scanner.IsScanning && InteractiveNavigator.IsScanningRecursive(current))
        {
            if (scanner.IsPaused)
            {
                titleSpinner = "[bold yellow]⏸ Paused[/]";
            }
            else
            {
                titleSpinner = $"[bold green]⚡[/][yellow]{spinner}[/]";
            }
        }

        string spinnerDisp = string.IsNullOrEmpty(titleSpinner) ? "" : $" {titleSpinner}";
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.DeepSkyBlue4)
            .Title($"[bold yellow]hyperdu Explorer[/] - [cyan]{Markup.Escape(current.Path)}[/]{spinnerDisp}");

        UpdateDecayAnimations(current);

        string subdirsStr = _heldAddSubdirs > 0
            ? $"{_lastSubdirs}+{_heldAddSubdirs}"
            : $"{_lastSubdirs}";

        string filesStr = _heldAddFiles > 0
            ? $"{_lastFiles}+{_heldAddFiles}"
            : $"{_lastFiles}";

        string sizeStr = _heldAddSize > 0
            ? $"{FormatSize(_lastSize)} +{FormatSize(_heldAddSize)}"
            : $"{FormatSize(_lastSize)}";

        string statsColor = scanner != null && scanner.IsScanning && InteractiveNavigator.IsScanningRecursive(current)
            ? "grey"
            : "#ffffff";
        string caption = $"[{statsColor}]Total Size: {sizeStr} | Subdirectories: {subdirsStr} | Files: {filesStr}[/]";
        if (statsColor == "#ffffff") caption += $"\n[bold white]Scan Time: {FormatTime(current.TotalScanTime)}[/]";
        caption +=
            "\n[bold green]↑/↓[/] Navigate | [bold green]Enter[/] Open | [bold green]Backspace/←[/] Up | [bold green]Space[/] Pause/Resume | [bold green]R[/] Rescan | [bold green]Q/Esc[/] Exit";

        table.AddColumn("[bold]Sel[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Size[/]");
        table.AddColumn("[bold]Percentage[/]");
        table.AddColumn("[bold]Visual Usage[/]");

        long parentTotalSize = current.TotalSize > 0 ? current.TotalSize : 1;
        TableRenderContext context = new TableRenderContext(scrollOffset, selectedIndex, pageSize, scanner, spinner);
        AddTableRows(table, items, parentTotalSize, context);

        if (items.Count > pageSize)
        {
            int visibleCount = Math.Min(pageSize, items.Count - scrollOffset);
            caption =
                $"[grey]Showing {scrollOffset + 1}-{scrollOffset + visibleCount} of {items.Count} items. (Scroll active)[/]\n" +
                caption;
        }

        table.Caption = new TableTitle(caption, Style.Plain);

        return table;
    }

    private void UpdateDecayAnimations(DirectoryNode current)
    {
        int currentSubdirs = current.SubdirectoryCount;
        int currentFiles = current.FileCount;
        long currentSize = current.TotalSize;

        if (current.Path != _lastDirPath)
        {
            ResetDecayState(current.Path, currentSubdirs, currentFiles, currentSize);
        }
        else
        {
            UpdateSubdirsDecay(currentSubdirs);
            UpdateFilesDecay(currentFiles);
            UpdateSizeDecay(currentSize);
            ApplySafetyFallbacks(currentSubdirs, currentFiles, currentSize);
        }
    }

    private void ResetDecayState(string path, int subdirs, int files, long size)
    {
        _lastDirPath = path;
        _lastSubdirs = subdirs;
        _lastFiles = files;
        _lastSize = size;

        _heldAddSubdirs = 0;
        _heldAddFiles = 0;
        _heldAddSize = 0;

        _decaySubdirs = 0;
        _decayFiles = 0;
        _decaySize = 0;
    }

    private void UpdateSubdirsDecay(int currentSubdirs)
    {
        int diff = currentSubdirs - (_lastSubdirs + _heldAddSubdirs);
        if (diff > 0)
        {
            _lastSubdirs += _heldAddSubdirs;
            _heldAddSubdirs = diff;
            _decaySubdirs = 15;
        }
        else if (_decaySubdirs > 0)
        {
            _decaySubdirs--;
            if (_decaySubdirs == 0)
            {
                _lastSubdirs += _heldAddSubdirs;
                _heldAddSubdirs = 0;
            }
        }
    }

    private void UpdateFilesDecay(int currentFiles)
    {
        int diff = currentFiles - (_lastFiles + _heldAddFiles);
        if (diff > 0)
        {
            _lastFiles += _heldAddFiles;
            _heldAddFiles = diff;
            _decayFiles = 15;
        }
        else if (_decayFiles > 0)
        {
            _decayFiles--;
            if (_decayFiles == 0)
            {
                _lastFiles += _heldAddFiles;
                _heldAddFiles = 0;
            }
        }
    }

    private void UpdateSizeDecay(long currentSize)
    {
        long diff = currentSize - (_lastSize + _heldAddSize);
        if (diff > 0)
        {
            _lastSize += _heldAddSize;
            _heldAddSize = diff;
            _decaySize = 15;
        }
        else if (_decaySize > 0)
        {
            _decaySize--;
            if (_decaySize == 0)
            {
                _lastSize += _heldAddSize;
                _heldAddSize = 0;
            }
        }
    }

    private void ApplySafetyFallbacks(int currentSubdirs, int currentFiles, long currentSize)
    {
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

    private static void AddTableRows(Table table, List<NavigatorItem> items, long parentTotalSize, TableRenderContext ctx)
    {
        int visibleCount = Math.Min(ctx.PageSize, items.Count - ctx.ScrollOffset);
        bool parentSelected = ctx.SelectedIndex >= 0 && ctx.SelectedIndex < items.Count && items[ctx.SelectedIndex].IsParentLink;

        for (int i = 0; i < visibleCount; i++)
        {
            int index = ctx.ScrollOffset + i;
            NavigatorItem item = items[index];
            bool isSelected = index == ctx.SelectedIndex;

            string selectMarker = isSelected ? "[bold green]>[/]" : " ";
            string displayName = GetItemDisplayName(item, isSelected, parentSelected, ctx.Scanner, ctx.Spinner);
            string percentStr = item.IsParentLink ? "-" : $"{((double)item.Size / parentTotalSize * 100):F1}%";
            string bar = BuildProgressBar(item, parentTotalSize);

            table.AddRow(
                new Markup(selectMarker),
                new Markup(displayName),
                new Markup(FormatSize(item.Size)),
                new Markup(percentStr),
                new Markup(bar)
            );
        }
    }

    private static string GetItemDisplayName(NavigatorItem item, bool isSelected, bool parentSelected, ParallelScanner? scanner, string spinner)
    {
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
                if (scanner.IsPaused)
                {
                    scanStatus = " [bold yellow]⏸[/]";
                }
                else if (parentSelected || isSelected)
                {
                    scanStatus = $" [bold green]⚡[/] [yellow]{spinner}[/]";
                }
                else
                {
                    scanStatus = $" [yellow]{spinner}[/]";
                }
            }

            rawDisplayName = $"[bold blue]📁 {Markup.Escape(item.DirNode.Name)}[/]{scanStatus}{err}";
        }
        else if (item.IsFile)
        {
            rawDisplayName = $"[grey]📄 {Markup.Escape(item.Name)}[/]";
        }
        else
        {
            rawDisplayName = Markup.Escape(item.Name);
        }

        return isSelected ? $"[bold reverse]{rawDisplayName}[/]" : rawDisplayName;
    }

    private static string BuildProgressBar(NavigatorItem item, long parentTotalSize)
    {
        if (item.IsParentLink) return string.Empty;

        double percent = (double)item.Size / parentTotalSize * 100;
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

        return $"[{barColor}]{filled}[/][grey]{empty}[/]";
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
        if (time.TotalMilliseconds < 1) return "< 1 ms";
        if (time.TotalSeconds < 1) return $"{time.TotalMilliseconds:F1} ms";
        if (time.TotalMinutes < 1) return $"{time.TotalSeconds:F2} s";
        return $"{time.Minutes}m {time.Seconds}s {time.Milliseconds}ms";
    }
}