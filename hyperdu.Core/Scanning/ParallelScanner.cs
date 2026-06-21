using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using hyperdu.Core.Models;

namespace hyperdu.Core.Scanning;

public class ParallelScanner
{
    private readonly ScanOptions _options;
    private HashSet<string> _normalizedExcludedPaths = new();
    private List<string> _normalizedExcludedPrefixes = new();

    // Progress tracking counters
    private long _directoriesScanned;
    private long _filesScanned;
    private long _totalBytesFound;
    private string _currentDirectory = string.Empty;
    private long _activeWorkers;

    public DirectoryNode? RootNode { get; private set; }
    public bool IsScanning { get; private set; }
    public PrioritizedQueue Queue { get; } = new();

    public long DirectoriesScanned => Volatile.Read(ref _directoriesScanned);
    public long FilesScanned => Volatile.Read(ref _filesScanned);
    public long TotalBytesFound => Volatile.Read(ref _totalBytesFound);
    public string CurrentDirectory => Volatile.Read(ref _currentDirectory);
    public long ActiveWorkers => Volatile.Read(ref _activeWorkers);

    public ParallelScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        string fullPath = Path.GetFullPath(path);

        // Trim trailing slashes except for root paths (e.g. "/" or "C:\")
        if (fullPath.Length > 1 && (fullPath.EndsWith(Path.DirectorySeparatorChar) || fullPath.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            var root = Path.GetPathRoot(fullPath);
            if (fullPath != root)
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        return fullPath;
    }

    public static bool IsVirtualOrSystemPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

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

    public async Task<DirectoryNode> ScanAsync(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        rootPath = NormalizePath(rootPath);
        if (IsVirtualOrSystemPath(rootPath))
        {
            throw new ArgumentException($"Cannot scan virtual system path: {rootPath}");
        }
        var rootNode = new DirectoryNode(rootPath);
        RootNode = rootNode;
        IsScanning = true;

        try
        {
            // Use OS-appropriate string comparer for paths
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            var allNodes = new ConcurrentDictionary<string, DirectoryNode>(pathComparer);
            allNodes.TryAdd(rootPath, rootNode);

            // Thread-safe set to prevent scanning the same physical folder path twice
            var scannedPaths = new ConcurrentDictionary<string, byte>(pathComparer);

            Queue.Clear();

            // Initialize state
            _directoriesScanned = 0;
            _filesScanned = 0;
            _totalBytesFound = 0;
            _currentDirectory = rootPath;

            // Start with 1 pending item (the root)
            long pendingCount = 1;
            _activeWorkers = 0;

            _normalizedExcludedPaths = new HashSet<string>(pathComparer);
            _normalizedExcludedPrefixes = new List<string>();

            if (_options.ExcludedPaths != null)
            {
                foreach (var excluded in _options.ExcludedPaths)
                {
                    if (string.IsNullOrEmpty(excluded)) continue;
                    string norm = NormalizePath(excluded);
                    _normalizedExcludedPaths.Add(norm);

                    string prefix = norm;
                    if (!norm.EndsWith(Path.DirectorySeparatorChar) && !norm.EndsWith(Path.AltDirectorySeparatorChar))
                    {
                        prefix = norm + Path.DirectorySeparatorChar;
                    }
                    _normalizedExcludedPrefixes.Add(prefix);
                }
            }

            Queue.Enqueue(rootPath);

        // Set up the background progress reporter
        using var progressTimer = new Timer(_ =>
        {
            if (progress != null)
            {
                progress.Report(new ScanProgress(
                    Volatile.Read(ref _directoriesScanned),
                    Volatile.Read(ref _filesScanned),
                    Volatile.Read(ref _totalBytesFound),
                    Volatile.Read(ref _currentDirectory),
                    Volatile.Read(ref _activeWorkers)
                ));
            }
        }, null, 0, 100);

        var workerTasks = new List<Task>();
        int workerCount = Math.Max(1, _options.WorkerCount);

        for (int i = 0; i < workerCount; i++)
        {
            workerTasks.Add(Task.Run(async () =>
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = _options.SkipHidden ? FileAttributes.Hidden | FileAttributes.System : 0
                };

                var localQueue = new Stack<string>();
                var localUpdates = new Dictionary<DirectoryNode, NodeDelta>();

                async Task<(bool Success, string Path)> DequeueWorkAsync()
                {
                    if (localQueue.Count > 0)
                    {
                        return (true, localQueue.Pop());
                    }

                    // Flush all pending local updates to the main tree before waiting on the global queue
                    FlushLocalUpdates(localUpdates);

                    return await Queue.DequeueAsync(cancellationToken);
                }

                try
                {
                    while (true)
                    {
                        var (success, dirPath) = await DequeueWorkAsync();
                        if (!success)
                        {
                            break;
                        }

                        Interlocked.Increment(ref _activeWorkers);
                        Volatile.Write(ref _currentDirectory, dirPath);

                        try
                        {
                            // Guarantee we scan each unique path exactly once
                            if (!scannedPaths.TryAdd(dirPath, 0))
                            {
                                continue;
                            }

                            if (!allNodes.TryGetValue(dirPath, out var node))
                            {
                                node = new DirectoryNode(dirPath);
                                allNodes[dirPath] = node;
                            }

                            node.StartTicks = DateTime.UtcNow.Ticks;

                            var dirInfo = new DirectoryInfo(dirPath);
                            long localSelfSize = 0;
                            int localFilesCount = 0;
                            List<DirectoryNode>? subdirsToQueue = null;

                            foreach (var entry in dirInfo.EnumerateFileSystemInfos("*", enumerationOptions))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                bool isSymlink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                                string? linkTarget = null;
                                if (isSymlink)
                                {
                                    try
                                    {
                                        linkTarget = entry.LinkTarget;
                                    }
                                    catch
                                    {
                                        // Ignore and treat as non-symlink if metadata read fails
                                    }
                                }

                                if (entry is DirectoryInfo subDir)
                                {
                                    string subPath = subDir.FullName;
                                    if (IsVirtualOrSystemPath(subPath))
                                    {
                                        continue;
                                    }
                                    if (IsExcludedPath(subPath))
                                    {
                                        continue;
                                    }
                                    var childNode = new DirectoryNode(subPath, node);

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
                                        allNodes.TryAdd(subPath, childNode);
                                        Interlocked.Increment(ref _directoriesScanned);
                                        
                                        QueueLocalUpdate(localUpdates, node, targetLength, 1, 0);
                                        continue;
                                    }

                                    if (allNodes.TryAdd(subPath, childNode))
                                    {
                                        subdirsToQueue ??= new List<DirectoryNode>();
                                        subdirsToQueue.Add(childNode);
                                        lock (node.Subdirectories)
                                        {
                                            node.Subdirectories.Add(childNode);
                                        }
                                        QueueLocalUpdate(localUpdates, node, 0, 1, 0);
                                    }
                                }
                                else if (entry is FileInfo file)
                                {
                                    if (isSymlink && !_options.FollowSymlinks)
                                    {
                                        // Skip file symlinks entirely if not following them
                                        continue;
                                    }

                                    long fileSize = 0;
                                    try
                                    {
                                        fileSize = file.Length;
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        // Handle broken links or deleted files
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

                                    lock (node.Files)
                                    {
                                        node.Files.Add(new FileNode(file.Name, fileSize));
                                    }
                                    localSelfSize += fileSize;
                                    localFilesCount++;
                                }
                            }

                            node.SelfSize = localSelfSize;
                            Interlocked.Increment(ref _directoriesScanned);
                            node.EndTicks = DateTime.UtcNow.Ticks;
                            node.IsScanned = true;

                            QueueLocalUpdate(localUpdates, node, localSelfSize, 0, localFilesCount);

                            // Update global progress counters in batch
                            if (localFilesCount > 0)
                            {
                                Interlocked.Add(ref _filesScanned, localFilesCount);
                            }
                            if (localSelfSize > 0)
                            {
                                Interlocked.Add(ref _totalBytesFound, localSelfSize);
                            }

                             if (subdirsToQueue != null && subdirsToQueue.Count > 0)
                             {
                                 Interlocked.Add(ref pendingCount, subdirsToQueue.Count);
                                 foreach (var sub in subdirsToQueue)
                                 {
                                     localQueue.Push(sub.Path);
                                 }

                                 if (localQueue.Count > 4)
                                 {
                                     while (localQueue.Count > 2)
                                     {
                                         Queue.Enqueue(localQueue.Pop());
                                     }
                                 }
                             }

                            if (localUpdates.Count >= 50)
                            {
                                FlushLocalUpdates(localUpdates);
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            if (allNodes.TryGetValue(dirPath, out var node))
                            {
                                node.ErrorMessage = "Access Denied";
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;
                            }
                            Interlocked.Increment(ref _directoriesScanned);
                        }
                        catch (DirectoryNotFoundException)
                        {
                            if (allNodes.TryGetValue(dirPath, out var node))
                            {
                                node.ErrorMessage = "Directory Not Found";
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;
                            }
                            Interlocked.Increment(ref _directoriesScanned);
                        }
                        catch (Exception ex)
                        {
                            if (allNodes.TryGetValue(dirPath, out var node))
                            {
                                node.ErrorMessage = ex.Message;
                                node.EndTicks = DateTime.UtcNow.Ticks;
                                node.IsScanned = true;
                            }
                            Interlocked.Increment(ref _directoriesScanned);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activeWorkers);
                            if (Interlocked.Decrement(ref pendingCount) == 0)
                            {
                                Queue.Complete();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Clean exit on cancellation
                }
                finally
                {
                    FlushLocalUpdates(localUpdates);
                }
            }, cancellationToken));
        }

        // Wait for all workers to finish
        await Task.WhenAll(workerTasks);

        // Report final progress before disposing the timer
        progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
        progress?.Report(new ScanProgress(
            Volatile.Read(ref _directoriesScanned),
            Volatile.Read(ref _filesScanned),
            Volatile.Read(ref _totalBytesFound),
            Volatile.Read(ref _currentDirectory),
            0
        ));

        // Post-processing: recursively calculate total sizes
        ComputeTotalSizes(rootNode);

        return rootNode;
    }
    finally
    {
        IsScanning = false;
    }
}

private class NodeDelta
{
    public long SizeDelta;
    public int SubdirsDelta;
    public int FilesDelta;
}

private static void QueueLocalUpdate(Dictionary<DirectoryNode, NodeDelta> localUpdates, DirectoryNode node, long sizeDelta, int subdirsDelta, int filesDelta)
{
    if (!localUpdates.TryGetValue(node, out var delta))
    {
        delta = new NodeDelta();
        localUpdates[node] = delta;
    }
    delta.SizeDelta += sizeDelta;
    delta.SubdirsDelta += subdirsDelta;
    delta.FilesDelta += filesDelta;
}

private static void FlushLocalUpdates(Dictionary<DirectoryNode, NodeDelta> localUpdates)
{
    if (localUpdates.Count == 0) return;

    // Ensure all ancestors are in the dictionary
    var keys = new List<DirectoryNode>(localUpdates.Keys);
    foreach (var node in keys)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            if (localUpdates.ContainsKey(parent))
            {
                break;
            }
            localUpdates[parent] = new NodeDelta();
            parent = parent.Parent;
        }
    }

    // Sort keys by path depth descending
    var nodes = new List<DirectoryNode>(localUpdates.Keys);
    nodes.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));

    foreach (var node in nodes)
    {
        var delta = localUpdates[node];

        if (delta.SizeDelta != 0) Interlocked.Add(ref node._totalSize, delta.SizeDelta);
        if (delta.SubdirsDelta != 0) Interlocked.Add(ref node._subdirectoryCount, delta.SubdirsDelta);
        if (delta.FilesDelta != 0) Interlocked.Add(ref node._fileCount, delta.FilesDelta);

        if (node.Parent != null)
        {
            var parentDelta = localUpdates[node.Parent];
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

    foreach (var sub in subs)
    {
        total += ComputeTotalSizes(sub);
    }

    node.TotalSize = total;
    return total;
}

private bool IsExcludedPath(string path)
{
    if (_normalizedExcludedPaths.Count == 0)
    {
        return false;
    }

    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    if (_normalizedExcludedPaths.Contains(path))
    {
        return true;
    }

    foreach (var prefix in _normalizedExcludedPrefixes)
    {
        if (path.StartsWith(prefix, comparison))
        {
            return true;
        }
    }

    return false;
}
}

public record ScanProgress(
    long DirectoriesScanned,
    long FilesScanned,
    long TotalBytesFound,
    string CurrentDirectory,
    long ActiveWorkers
);
