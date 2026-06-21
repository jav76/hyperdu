using System.Runtime.InteropServices;
using hyperdu.Core.Models;

namespace hyperdu.Core.Scanning;

public class ParallelScanner
{
    private readonly ScanOptions _options;
    private long _activeWorkers;
    private string _currentDirectory = string.Empty;

    private long _directoriesScanned;
    private long _filesScanned;
    private HashSet<string> _normalizedExcludedPaths = new();
    private List<string> _normalizedExcludedPrefixes = new();
    private long _totalBytesFound;

    public ParallelScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    public DirectoryNode? RootNode { get; private set; }
    public bool IsScanning { get; private set; }
    public PrioritizedQueue Queue { get; } = new();

    public long DirectoriesScanned => Volatile.Read(ref _directoriesScanned);
    public long FilesScanned => Volatile.Read(ref _filesScanned);
    public long TotalBytesFound => Volatile.Read(ref _totalBytesFound);
    public string CurrentDirectory => Volatile.Read(ref _currentDirectory);
    public long ActiveWorkers => Volatile.Read(ref _activeWorkers);

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        string fullPath = Path.GetFullPath(path);

        // Trim trailing slashes except for root paths (e.g. "/" or "C:\")
        if (fullPath.Length > 1 && (fullPath.EndsWith(Path.DirectorySeparatorChar) ||
                                    fullPath.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            string? root = Path.GetPathRoot(fullPath);
            if (fullPath != root)
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    public static bool IsVirtualOrSystemPath(string path)
    {
        if (OperatingSystem.IsWindows()) return false;

        // Standard Linux virtual/pseudo filesystems that do not consume actual disk space
        if (path.StartsWith("/proc", StringComparison.Ordinal) && (path.Length == 5 || path[5] == '/'))
            return true;
        if (path.StartsWith("/sys", StringComparison.Ordinal) && (path.Length == 4 || path[4] == '/'))
            return true;
        if (path.StartsWith("/dev", StringComparison.Ordinal) && (path.Length == 4 || path[4] == '/'))
            return true;
        if (path.StartsWith("/run", StringComparison.Ordinal) && (path.Length == 4 || path[4] == '/'))
            return true;

        return false;
    }

    public async Task<DirectoryNode> ScanAsync(string rootPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        rootPath = NormalizePath(rootPath);
        if (IsVirtualOrSystemPath(rootPath))
            throw new ArgumentException($"Cannot scan virtual system path: {rootPath}");
        DirectoryNode rootNode = new DirectoryNode(rootPath);
        RootNode = rootNode;
        IsScanning = true;

        try
        {
            Queue.Clear();

            _directoriesScanned = 0;
            _filesScanned = 0;
            _totalBytesFound = 0;
            _currentDirectory = rootPath;

            long pendingCount = 1;
            _activeWorkers = 0;

            StringComparer pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _normalizedExcludedPaths = new HashSet<string>(pathComparer);
            _normalizedExcludedPrefixes = new List<string>();

            foreach (string excluded in _options.ExcludedPaths)
            {
                if (string.IsNullOrEmpty(excluded)) continue;
                string norm = NormalizePath(excluded);
                _normalizedExcludedPaths.Add(norm);

                string prefix = norm;
                if (!norm.EndsWith(Path.DirectorySeparatorChar) && !norm.EndsWith(Path.AltDirectorySeparatorChar))
                    prefix = norm + Path.DirectorySeparatorChar;
                _normalizedExcludedPrefixes.Add(prefix);
            }

            Queue.Enqueue(rootNode);

            using Timer progressTimer = new Timer(_ =>
            {
                if (progress != null)
                    progress.Report(new ScanProgress(
                        Volatile.Read(ref _directoriesScanned),
                        Volatile.Read(ref _filesScanned),
                        Volatile.Read(ref _totalBytesFound),
                        Volatile.Read(ref _currentDirectory),
                        Volatile.Read(ref _activeWorkers)
                    ));
            }, null, 0, 100);

            List<Task> workerTasks = new List<Task>();
            int workerCount = Math.Max(1, _options.WorkerCount);

            for (int i = 0; i < workerCount; i++)
                workerTasks.Add(Task.Run(async () =>
                {
                    EnumerationOptions enumerationOptions = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = _options.SkipHidden ? FileAttributes.Hidden | FileAttributes.System : 0
                    };

                    Stack<DirectoryNode> localQueue = new Stack<DirectoryNode>();
                    Dictionary<DirectoryNode, NodeDelta> localUpdates = new Dictionary<DirectoryNode, NodeDelta>();

                    async Task<(bool Success, DirectoryNode? Node)> DequeueWorkAsync()
                    {
                        if (localQueue.Count > 0) return (true, localQueue.Pop());

                        FlushLocalUpdates(localUpdates);

                        return await Queue.DequeueAsync(cancellationToken);
                    }

                    try
                    {
                        while (true)
                        {
                            (bool success, DirectoryNode? node) = await DequeueWorkAsync();
                            if (!success || node == null) break;

                            Interlocked.Increment(ref _activeWorkers);
                            Volatile.Write(ref _currentDirectory, node.Path);

                            try
                            {
                                if (!node.TryClaimForScan()) continue;

                                node.StartTicks = DateTime.UtcNow.Ticks;

                                DirectoryInfo dirInfo = new DirectoryInfo(node.Path);
                                long localSelfSize = 0;
                                int localFilesCount = 0;
                                List<DirectoryNode>? subdirsToQueue = null;

                                foreach (FileSystemInfo entry in dirInfo.EnumerateFileSystemInfos("*", enumerationOptions))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    bool isSymlink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                                    string? linkTarget = null;
                                    if (isSymlink)
                                        try
                                        {
                                            linkTarget = entry.LinkTarget;
                                        }
                                        catch
                                        {
                                        }

                                    if (entry is DirectoryInfo subDir)
                                    {
                                        string subPath = subDir.FullName;
                                        if (IsVirtualOrSystemPath(subPath)) continue;
                                        if (IsExcludedPath(subPath)) continue;
                                        DirectoryNode childNode = new DirectoryNode(subPath, node);

                                        if (isSymlink && !_options.FollowSymlinks)
                                        {
                                            // It's a directory symlink and we are not following it.
                                            // Count it as a sub-folder but do not traverse it.
                                            int targetLength = linkTarget?.Length ?? 0;
                                            childNode.SelfSize = targetLength;
                                            childNode.TotalSize = targetLength;
                                            childNode.IsScanned = true;
                                            lock (node.Subdirectories)
                                            {
                                                node.Subdirectories.Add(childNode);
                                            }

                                            Interlocked.Increment(ref _directoriesScanned);

                                            QueueLocalUpdate(localUpdates, node, targetLength, 1, 0);
                                            continue;
                                        }

                                        subdirsToQueue ??= new List<DirectoryNode>(4);
                                        subdirsToQueue.Add(childNode);
                                        lock (node.Subdirectories)
                                        {
                                            node.Subdirectories.Add(childNode);
                                        }

                                        QueueLocalUpdate(localUpdates, node, 0, 1, 0);
                                    }
                                    else if (entry is FileInfo file)
                                    {
                                        if (isSymlink && !_options.FollowSymlinks)
                                            continue;

                                        long fileSize = 0;
                                        try
                                        {
                                            fileSize = file.Length;
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            fileSize = 0;
                                        }
                                        catch (UnauthorizedAccessException)
                                        {
                                            fileSize = 0;
                                        }
                                        catch (IOException)
                                        {
                                            fileSize = 0;
                                        }

                                        localSelfSize += fileSize;
                                        localFilesCount++;
                                    }
                                }

                                node.SelfSize = localSelfSize;
                                node.SelfFileCount = localFilesCount;
                                Interlocked.Increment(ref _directoriesScanned);
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;

                                QueueLocalUpdate(localUpdates, node, localSelfSize, 0, localFilesCount);

                                if (localFilesCount > 0) Interlocked.Add(ref _filesScanned, localFilesCount);
                                if (localSelfSize > 0) Interlocked.Add(ref _totalBytesFound, localSelfSize);

                                if (subdirsToQueue != null && subdirsToQueue.Count > 0)
                                {
                                    Interlocked.Add(ref pendingCount, subdirsToQueue.Count);
                                    foreach (DirectoryNode sub in subdirsToQueue) localQueue.Push(sub);

                                    if (localQueue.Count > 4)
                                        while (localQueue.Count > 2)
                                            Queue.Enqueue(localQueue.Pop());
                                }

                                if (localUpdates.Count >= 50) FlushLocalUpdates(localUpdates);
                            }
                            catch (UnauthorizedAccessException)
                            {
                                node.ErrorMessage = "Access Denied";
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;

                                Interlocked.Increment(ref _directoriesScanned);
                            }
                            catch (DirectoryNotFoundException)
                            {
                                node.ErrorMessage = "Directory Not Found";
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;

                                Interlocked.Increment(ref _directoriesScanned);
                            }
                            catch (Exception ex)
                            {
                                node.ErrorMessage = ex.Message;
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;

                                Interlocked.Increment(ref _directoriesScanned);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _activeWorkers);
                                if (Interlocked.Decrement(ref pendingCount) == 0) Queue.Complete();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        FlushLocalUpdates(localUpdates);
                    }
                }, cancellationToken));

            await Task.WhenAll(workerTasks);

            progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
            progress?.Report(new ScanProgress(
                Volatile.Read(ref _directoriesScanned),
                Volatile.Read(ref _filesScanned),
                Volatile.Read(ref _totalBytesFound),
                Volatile.Read(ref _currentDirectory),
                0
            ));

            ComputeTotalSizes(rootNode);

            rootNode.Compact();

            return rootNode;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static void QueueLocalUpdate(Dictionary<DirectoryNode, NodeDelta> localUpdates, DirectoryNode node,
        long sizeDelta, int subdirsDelta, int filesDelta)
    {
        ref NodeDelta delta = ref CollectionsMarshal.GetValueRefOrAddDefault(localUpdates, node, out _);

        delta.SizeDelta += sizeDelta;
        delta.SubdirsDelta += subdirsDelta;
        delta.FilesDelta += filesDelta;
    }

    private static void FlushLocalUpdates(Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        if (localUpdates.Count == 0) return;

        List<DirectoryNode> keys = new List<DirectoryNode>(localUpdates.Keys);
        foreach (DirectoryNode node in keys)
        {
            DirectoryNode? parent = node.Parent;
            while (parent != null)
            {
                if (localUpdates.ContainsKey(parent)) break;
                localUpdates[parent] = default;
                parent = parent.Parent;
            }
        }

        List<DirectoryNode> nodes = new List<DirectoryNode>(localUpdates.Keys);
        nodes.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

        foreach (DirectoryNode node in nodes)
        {
            NodeDelta delta = localUpdates[node];

            if (delta.SizeDelta != 0) Interlocked.Add(ref node._totalSize, delta.SizeDelta);
            if (delta.SubdirsDelta != 0) Interlocked.Add(ref node._subdirectoryCount, delta.SubdirsDelta);
            if (delta.FilesDelta != 0) Interlocked.Add(ref node._fileCount, delta.FilesDelta);

            if (node.Parent != null)
            {
                ref NodeDelta parentDelta = ref CollectionsMarshal.GetValueRefOrAddDefault(localUpdates, node.Parent, out _);
                parentDelta.SizeDelta += delta.SizeDelta;
                parentDelta.SubdirsDelta += delta.SubdirsDelta;
                parentDelta.FilesDelta += delta.FilesDelta;
            }
        }

        localUpdates.Clear();
    }

    public static long ComputeTotalSizes(DirectoryNode node)
    {
        long total = node.SelfSize;
        DirectoryNode[] subs;
        lock (node.Subdirectories)
        {
            subs = node.Subdirectories.ToArray();
        }

        foreach (DirectoryNode sub in subs) total += ComputeTotalSizes(sub);

        node.TotalSize = total;
        return total;
    }

    private bool IsExcludedPath(string path)
    {
        if (_normalizedExcludedPaths.Count == 0) return false;

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (_normalizedExcludedPaths.Contains(path)) return true;

        foreach (string prefix in _normalizedExcludedPrefixes)
            if (path.StartsWith(prefix, comparison))
                return true;

        return false;
    }

    private struct NodeDelta
    {
        public int FilesDelta;
        public long SizeDelta;
        public int SubdirsDelta;
    }
}

public record ScanProgress(
    long DirectoriesScanned,
    long FilesScanned,
    long TotalBytesFound,
    string CurrentDirectory,
    long ActiveWorkers
);