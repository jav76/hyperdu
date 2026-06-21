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
    private long _pendingCount;

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

            _pendingCount = 1;
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

            EnumerationOptions enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = _options.SkipHidden ? FileAttributes.Hidden | FileAttributes.System : 0
            };

            for (int i = 0; i < workerCount; i++)
            {
                workerTasks.Add(Task.Run(async () => await ScanWorkerLoopAsync(enumerationOptions, cancellationToken), cancellationToken));
            }

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

    private async Task ScanWorkerLoopAsync(EnumerationOptions enumerationOptions, CancellationToken cancellationToken)
    {
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
                    await ProcessDirectoryNodeAsync(node, localQueue, localUpdates, enumerationOptions, cancellationToken);
                }
                catch (UnauthorizedAccessException)
                {
                    MarkNodeFailed(node, "Access Denied");
                }
                catch (DirectoryNotFoundException)
                {
                    MarkNodeFailed(node, "Directory Not Found");
                }
                catch (Exception ex)
                {
                    MarkNodeFailed(node, ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeWorkers);
                    if (Interlocked.Decrement(ref _pendingCount) == 0)
                    {
                        Queue.Complete();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when scan is canceled.
        }
        finally
        {
            FlushLocalUpdates(localUpdates);
        }
    }

    private async Task ProcessDirectoryNodeAsync(DirectoryNode node, Stack<DirectoryNode> localQueue,
        Dictionary<DirectoryNode, NodeDelta> localUpdates, EnumerationOptions enumerationOptions, CancellationToken cancellationToken)
    {
        if (!node.TryClaimForScan()) return;

        node.StartTicks = DateTime.UtcNow.Ticks;

        long localSelfSize = 0;
        int localFilesCount = 0;
        List<DirectoryNode>? subdirsToQueue = null;

        try
        {
            using var enumerator = new LightweightFileSystemEnumerator(node.Path, enumerationOptions);
            while (enumerator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                LightweightEntry entry = enumerator.Current;

                bool isSymlink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

                if (entry.IsDirectory)
                {
                    ProcessDirectoryEntry(entry, node, isSymlink, ref subdirsToQueue, localUpdates);
                }
                else
                {
                    ProcessFileEntry(entry, isSymlink, ref localSelfSize, ref localFilesCount);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            MarkNodeFailed(node, "Access Denied");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            MarkNodeFailed(node, "Directory Not Found");
            return;
        }
        catch (Exception ex)
        {
            MarkNodeFailed(node, ex.Message);
            return;
        }

        node.SelfSize = localSelfSize;
        node.SelfFileCount = localFilesCount;
        Interlocked.Increment(ref _directoriesScanned);
        node.EndTicks = DateTime.UtcNow.Ticks;
        node.IsScanned = true;

        QueueLocalUpdate(localUpdates, node, localSelfSize, 0, localFilesCount);

        if (localFilesCount > 0) Interlocked.Add(ref _filesScanned, localFilesCount);
        if (localSelfSize > 0) Interlocked.Add(ref _totalBytesFound, localSelfSize);

        EnqueueSubdirectories(subdirsToQueue, localQueue);

        if (localUpdates.Count >= 50) FlushLocalUpdates(localUpdates);
    }

    private void EnqueueSubdirectories(List<DirectoryNode>? subdirsToQueue, Stack<DirectoryNode> localQueue)
    {
        if (subdirsToQueue == null || subdirsToQueue.Count == 0) return;

        Interlocked.Add(ref _pendingCount, subdirsToQueue.Count);
        foreach (DirectoryNode sub in subdirsToQueue)
        {
            localQueue.Push(sub);
        }

        if (localQueue.Count > 4)
        {
            while (localQueue.Count > 2)
            {
                Queue.Enqueue(localQueue.Pop());
            }
        }
    }

    private static string? ResolveLinkTargetSafely(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, false);
            return target?.FullName;
        }
        catch
        {
            try
            {
                var target = File.ResolveLinkTarget(path, false);
                return target?.FullName;
            }
            catch
            {
                return null;
            }
        }
    }

    private void ProcessDirectoryEntry(LightweightEntry entry, DirectoryNode node, bool isSymlink, ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        string subPath = Path.Combine(node.Path, entry.Name);
        if (IsVirtualOrSystemPath(subPath)) return;
        if (IsExcludedPath(subPath)) return;
        DirectoryNode childNode = new DirectoryNode(subPath, node);

        if (isSymlink && !_options.FollowSymlinks)
        {
            string? linkTarget = ResolveLinkTargetSafely(subPath);
            ProcessDirectorySymlink(node, childNode, linkTarget, localUpdates);
            return;
        }

        subdirsToQueue ??= new List<DirectoryNode>(4);
        subdirsToQueue.Add(childNode);
        lock (node.Subdirectories)
        {
            node.Subdirectories.Add(childNode);
        }

        QueueLocalUpdate(localUpdates, node, 0, 1, 0);
    }

    private void ProcessFileEntry(LightweightEntry entry, bool isSymlink, ref long localSelfSize, ref int localFilesCount)
    {
        if (isSymlink && !_options.FollowSymlinks) return;

        localSelfSize += entry.Length;
        localFilesCount++;
    }

    private void ProcessDirectorySymlink(DirectoryNode node, DirectoryNode childNode, string? linkTarget, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
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
    }

    private void MarkNodeFailed(DirectoryNode node, string errorMessage)
    {
        node.ErrorMessage = errorMessage;
        node.EndTicks = DateTime.UtcNow.Ticks;
        node.IsScanned = true;

        Interlocked.Increment(ref _directoriesScanned);
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

        EnsureParentHierarchyExists(localUpdates, new List<DirectoryNode>(localUpdates.Keys));

        List<DirectoryNode> nodes = new List<DirectoryNode>(localUpdates.Keys);
        nodes.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

        foreach (DirectoryNode node in nodes)
        {
            ApplyNodeDelta(node, localUpdates[node]);

            if (node.Parent != null)
            {
                PropagateDeltaToParent(node.Parent, localUpdates[node], localUpdates);
            }
        }

        localUpdates.Clear();
    }

    private static void EnsureParentHierarchyExists(Dictionary<DirectoryNode, NodeDelta> localUpdates, IEnumerable<DirectoryNode> nodes)
    {
        foreach (DirectoryNode? parent in nodes.Select(node => node.Parent))
        {
            DirectoryNode? current = parent;
            while (current != null && !localUpdates.ContainsKey(current))
            {
                localUpdates[current] = default;
                current = current.Parent;
            }
        }
    }

    private static void ApplyNodeDelta(DirectoryNode node, NodeDelta delta)
    {
        if (delta.SizeDelta != 0) Interlocked.Add(ref node._totalSize, delta.SizeDelta);
        if (delta.SubdirsDelta != 0) Interlocked.Add(ref node._subdirectoryCount, delta.SubdirsDelta);
        if (delta.FilesDelta != 0) Interlocked.Add(ref node._fileCount, delta.FilesDelta);
    }

    private static void PropagateDeltaToParent(DirectoryNode parent, NodeDelta delta, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        ref NodeDelta parentDelta = ref CollectionsMarshal.GetValueRefOrAddDefault(localUpdates, parent, out _);
        parentDelta.SizeDelta += delta.SizeDelta;
        parentDelta.SubdirsDelta += delta.SubdirsDelta;
        parentDelta.FilesDelta += delta.FilesDelta;
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

        return _normalizedExcludedPrefixes.Any(prefix => path.StartsWith(prefix, comparison));
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