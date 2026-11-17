using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using hyperdu.Core.Du;
using hyperdu.Core.Models;
using hyperdu.Core.Native;

namespace hyperdu.Core.Scanning;

public class ParallelScanner
{
    private const int SeenStripeCount = 64;

    private readonly ScanOptions _options;
    private long _activeWorkers;
    private string _currentDirectory = string.Empty;

    private long _directoriesScanned;
    private long _filesScanned;
    private bool _hasExcludes;
    private HashSet<string> _normalizedExcludedPaths = new();
    private List<string> _normalizedExcludedPrefixes = new();
    private long _totalBytesFound;
    private long _pendingCount;
    private bool _useNativeIo;
    private ulong _rootDevice;
    private Lock[]? _seenLocks;
    private HashSet<FileIdentity>[]? _seenSets;

    /// <summary>True when the most recent scan's root directory matched an exclusion rule.</summary>
    internal bool LastScanExcludedRoot { get; private set; }

    public ParallelScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);

    public DirectoryNode? RootNode { get; private set; }
    public bool IsScanning { get; private set; }
    public bool IsPaused { get; private set; }
    public ScanOptions Options => _options;
    public PrioritizedQueue Queue { get; } = new();

    public void Pause()
    {
        if (IsScanning && !IsPaused)
        {
            IsPaused = true;
            _pauseEvent.Reset();
        }
    }

    public void Resume()
    {
        if (IsScanning && IsPaused)
        {
            IsPaused = false;
            _pauseEvent.Set();
        }
    }

    public long DirectoriesScanned => Volatile.Read(ref _directoriesScanned);
    public long FilesScanned => Volatile.Read(ref _filesScanned);
    public long TotalBytesFound => Volatile.Read(ref _totalBytesFound);
    public string CurrentDirectory => Volatile.Read(ref _currentDirectory);
    public long ActiveWorkers => Volatile.Read(ref _activeWorkers);

    /// <summary>Whether the current or last scan used the native libc reader.</summary>
    internal bool UsesNativeIo => _useNativeIo;

    public static bool DirectoryExistsPathSafe(string path) => NativeIo.DirectoryExistsPathSafe(path);

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
        return await ScanCoreAsync(new DirectoryNode(rootPath), progress, cancellationToken);
    }

    /// <summary>
    /// Scans the directory behind an existing node into a new, detached tree. Unlike the string overload,
    /// this keeps exact native paths for names that are not valid UTF-8.
    /// </summary>
    public async Task<DirectoryNode> ScanAsync(DirectoryNode existing, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DirectoryNode rootNode = existing.HasNativeAncestry
            ? new DirectoryNode(existing.Path, null, existing.NativeName, existing.GetNativePath(nullTerminated: false))
            : new DirectoryNode(existing.Path);
        return await ScanCoreAsync(rootNode, progress, cancellationToken);
    }

    private async Task<DirectoryNode> ScanCoreAsync(DirectoryNode rootNode, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        string rootPath = rootNode.Path;
        RootNode = rootNode;
        IsScanning = true;
        IsPaused = false;
        _pauseEvent.Set();

        try
        {
            Queue.Clear();

            _useNativeIo = _options.UseNativeIo && NativeIo.IsSupported;
            _rootDevice = 0;
            if (_useNativeIo && !_options.CountLinks)
            {
                _seenLocks = new Lock[SeenStripeCount];
                _seenSets = new HashSet<FileIdentity>[SeenStripeCount];
                for (int i = 0; i < SeenStripeCount; i++)
                {
                    _seenLocks[i] = new Lock();
                    _seenSets[i] = new HashSet<FileIdentity>();
                }
            }
            else
            {
                _seenLocks = null;
                _seenSets = null;
            }

            _directoriesScanned = 0;
            _filesScanned = 0;
            _totalBytesFound = 0;
            _currentDirectory = rootPath;

            _pendingCount = 1;
            _activeWorkers = 0;

            PrepareLegacyExcludes(
                _options.ExcludedPaths,
                out HashSet<string>? exactExcludes,
                out List<string>? prefixExcludes);
            _normalizedExcludedPaths = exactExcludes ?? [];
            _normalizedExcludedPrefixes = prefixExcludes ?? [];

            _hasExcludes = _normalizedExcludedPaths.Count > 0 || !_options.ExcludeMatcher.IsEmpty;
            LastScanExcludedRoot = false;
            if (_hasExcludes && IsRootExcluded(rootNode))
            {
                LastScanExcludedRoot = true;
                rootNode.SelfSize = 0;
                rootNode.TotalSize = 0;
                rootNode.IsScanned = true;
                return rootNode;
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
            IsPaused = false;
            _seenLocks = null;
            _seenSets = null;
            _pauseEvent.Set();
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
                _pauseEvent.Wait(cancellationToken);
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
            string? error = null;
            if (_useNativeIo)
                error = ReadDirectoryNative(node, ref localSelfSize, ref localFilesCount, ref subdirsToQueue, localUpdates, cancellationToken);
            else
                ReadDirectoryManaged(node, ref localSelfSize, ref localFilesCount, ref subdirsToQueue, localUpdates, enumerationOptions, cancellationToken);

            if (error != null)
            {
                MarkNodeFailed(node, error);
                return;
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

        node.SelfSize += localSelfSize;
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

    private void ReadDirectoryManaged(DirectoryNode node, ref long localSelfSize, ref int localFilesCount,
        ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates,
        EnumerationOptions enumerationOptions, CancellationToken cancellationToken)
    {
        LightweightFileSystemEnumerator.EntryExcludePredicate? excludePredicate =
            _hasExcludes ? IsExcludedManagedSpan : null;
        using var enumerator = new LightweightFileSystemEnumerator(node.Path, enumerationOptions, excludePredicate);
        while (enumerator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pauseEvent.Wait(cancellationToken);
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

    /// <summary>
    /// Reads one directory with libc: names stay raw bytes and every stat is relative to the open directory,
    /// so the kernel never re-walks the full path per entry. Returns an error message when the directory cannot be read.
    /// </summary>
    private string? ReadDirectoryNative(DirectoryNode node, ref long localSelfSize, ref int localFilesCount,
        ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates,
        CancellationToken cancellationToken)
    {
        Span<byte> statBuffer = stackalloc byte[NativeIo.StatBufferSize];
        bool checkDotHidden = _options.SkipHidden && !NativeIo.IsWindows;
        bool checkHiddenFlag = _options.SkipHidden && (NativeIo.IsMacOS || NativeIo.IsWindows);

        byte[]? parentNativePath = _hasExcludes ? node.GetNativePath(nullTerminated: false) : null;
        byte[]? excludeScratch = _hasExcludes && !_options.ExcludeMatcher.IsEmpty
            ? ArrayPool<byte>.Shared.Rent(Math.Max(1024, parentNativePath!.Length + 256))
            : null;

        try
        {
            NativeDirectory? directory = NativeDirectory.OpenPathSafe(
                node.GetNativePath(),
                out int openError,
                followSymlinks: node.Parent == null || _options.FollowSymlinks);
            if (directory == null) return NativeIo.DescribeError(openError);

            using (directory)
            {
                if (node.Parent == null && directory.TryStatSelf(statBuffer, out NativeStat rootStat))
                {
                    _rootDevice = rootStat.Device;
                    node.Identity = new FileIdentity(rootStat.Device, rootStat.Inode);
                    _ = TryMarkSeen(in rootStat);
                    localSelfSize += NativeIo.ComputeEntryByteSize(in rootStat, _options.ApparentSize);
                }

                while (directory.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    ReadOnlySpan<byte> name = directory.CurrentName;
                    if (checkDotHidden && name[0] == (byte)'.') continue;
                    if (_hasExcludes && IsExcludedNativeEntry(parentNativePath!, node.Path, name, ref excludeScratch))
                        continue;
                    if (!directory.TryStatCurrent(false, statBuffer, out NativeStat linkStat)) continue;
                    if (checkHiddenFlag && linkStat.HasHiddenFlag) continue;

                    switch (linkStat.Kind)
                    {
                        case NativeEntryKind.Directory:
                            TryAddNativeDirectory(node, name, in linkStat, ref subdirsToQueue, localUpdates);
                            break;

                        case NativeEntryKind.Symlink:
                            if (_options.FollowSymlinks)
                            {
                                if (directory.TryStatCurrent(true, statBuffer, out NativeStat targetStat))
                                {
                                    if (targetStat.Kind == NativeEntryKind.Directory)
                                        TryAddNativeDirectory(node, name, in targetStat, ref subdirsToQueue, localUpdates);
                                    else
                                        TryRecordNativeFile(in targetStat, ref localSelfSize, ref localFilesCount);
                                }
                                else
                                {
                                    // Dangling symlink when following: fall back to link's own size.
                                    TryRecordNativeFile(in linkStat, ref localSelfSize, ref localFilesCount);
                                }
                            }
                            else if (directory.TryStatCurrent(true, statBuffer, out NativeStat targetStat) &&
                                     targetStat.Kind == NativeEntryKind.Directory)
                            {
                                long linkOwnBytes = TryMarkSeen(in linkStat)
                                    ? NativeIo.ComputeEntryByteSize(in linkStat, _options.ApparentSize)
                                    : 0;
                                ProcessUnfollowedDirectorySymlink(name, node, linkOwnBytes, localUpdates);
                            }

                            break;

                        default:
                            TryRecordNativeFile(in linkStat, ref localSelfSize, ref localFilesCount);
                            break;
                    }
                }

                return directory.Error == 0 ? null : NativeIo.DescribeError(directory.Error);
            }
        }
        finally
        {
            if (excludeScratch != null)
                ArrayPool<byte>.Shared.Return(excludeScratch);
        }
    }

    private void TryAddNativeDirectory(DirectoryNode parent, ReadOnlySpan<byte> name, in NativeStat dirStat,
        ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        if (_options.OneFileSystem && _rootDevice != 0 && dirStat.Device != _rootDevice)
            return;

        FileIdentity dirId = new(dirStat.Device, dirStat.Inode);
        if ((_options.FollowSymlinks && HasAncestorIdentity(parent, dirId)) || !TryMarkSeen(in dirStat))
            return;

        long dirOwnBytes = NativeIo.ComputeEntryByteSize(in dirStat, _options.ApparentSize);
        ProcessNativeDirectoryEntry(name, parent, dirId, dirOwnBytes, ref subdirsToQueue, localUpdates);
    }

    private void TryRecordNativeFile(in NativeStat fileStat, ref long localSelfSize, ref int localFilesCount)
    {
        if (_options.OneFileSystem && _rootDevice != 0 && fileStat.Device != _rootDevice)
            return;

        if (!TryMarkSeen(in fileStat))
            return;

        localSelfSize += NativeIo.ComputeEntryByteSize(in fileStat, _options.ApparentSize);
        localFilesCount++;
    }

    private bool TryMarkSeen(in NativeStat stat)
    {
        if (_seenSets == null || _seenLocks == null || !stat.HasValidFileId)
            return true;

        bool shouldTrack = stat.Kind == NativeEntryKind.Directory ||
                           stat.HardLinks != 1 ||
                           _options.FollowSymlinks;
        if (!shouldTrack)
            return true;

        FileIdentity id = new(stat.Device, stat.Inode);
        int stripe = (int)((uint)id.GetHashCode() & (SeenStripeCount - 1));
        lock (_seenLocks[stripe])
        {
            return _seenSets[stripe].Add(id);
        }
    }

    private static bool HasAncestorIdentity(DirectoryNode? node, FileIdentity id)
    {
        if (id.Inode == 0 || id.Inode == ulong.MaxValue)
            return false;

        while (node != null)
        {
            if (node.Identity == id)
                return true;
            node = node.Parent;
        }

        return false;
    }

    private void ProcessNativeDirectoryEntry(ReadOnlySpan<byte> nativeName, DirectoryNode node, FileIdentity dirId,
        long dirOwnBytes, ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        byte[]? exactName = Utf8.IsValid(nativeName) ? null : nativeName.ToArray();
        DirectoryNode? childNode = CreateChildNode(node, Encoding.UTF8.GetString(nativeName), exactName);
        if (childNode == null) return;

        childNode.Identity = dirId;
        childNode.SelfSize = dirOwnBytes;
        childNode.TotalSize = dirOwnBytes;
        AddSubdirectory(node, childNode, ref subdirsToQueue, localUpdates);
    }

    private void ProcessUnfollowedDirectorySymlink(ReadOnlySpan<byte> nativeName, DirectoryNode node, long linkOwnBytes,
        Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        byte[]? exactName = Utf8.IsValid(nativeName) ? null : nativeName.ToArray();
        DirectoryNode? childNode = CreateChildNode(node, Encoding.UTF8.GetString(nativeName), exactName);
        if (childNode == null) return;

        ProcessDirectorySymlink(node, childNode, linkOwnBytes, localUpdates);
    }

    private DirectoryNode? CreateChildNode(DirectoryNode node, string name, byte[]? nativeName)
    {
        string subPath = Path.Combine(node.Path, name);
        if (IsVirtualOrSystemPath(subPath)) return null;
        return new DirectoryNode(subPath, node, nativeName, null);
    }

    private void ProcessDirectoryEntry(LightweightEntry entry, DirectoryNode node, bool isSymlink, ref List<DirectoryNode>? subdirsToQueue, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        DirectoryNode? childNode = CreateChildNode(node, entry.Name, null);
        if (childNode == null) return;

        if (isSymlink)
        {
            if (!_options.FollowSymlinks)
            {
                string? linkTarget = ResolveLinkTargetSafely(childNode.Path);
                ProcessDirectorySymlink(node, childNode, linkTarget?.Length ?? 0, localUpdates);
                return;
            }

            if (IsManagedSymlinkCycle(node, childNode.Path))
                return;
        }

        AddSubdirectory(node, childNode, ref subdirsToQueue, localUpdates);
    }

    private static bool IsManagedSymlinkCycle(DirectoryNode node, string symlinkPath)
    {
        try
        {
            FileSystemInfo? target = Directory.ResolveLinkTarget(symlinkPath, returnFinalTarget: true);
            if (target == null) return false;
            string targetFull = NormalizePath(target.FullName);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            DirectoryNode? current = node;
            while (current != null)
            {
                if (string.Equals(NormalizePath(current.Path), targetFull, comparison))
                    return true;

                FileSystemInfo? ancestorTarget = Directory.ResolveLinkTarget(current.Path, returnFinalTarget: true);
                if (ancestorTarget != null &&
                    string.Equals(NormalizePath(ancestorTarget.FullName), targetFull, comparison))
                {
                    return true;
                }

                current = current.Parent;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void AddSubdirectory(DirectoryNode node, DirectoryNode childNode, ref List<DirectoryNode>? subdirsToQueue,
        Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
        subdirsToQueue ??= new List<DirectoryNode>(4);
        subdirsToQueue.Add(childNode);
        lock (node.Subdirectories)
        {
            node.Subdirectories.Add(childNode);
        }

        QueueLocalUpdate(localUpdates, node, childNode.SelfSize, 1, 0);
        if (childNode.SelfSize > 0) Interlocked.Add(ref _totalBytesFound, childNode.SelfSize);
    }

    private void ProcessFileEntry(LightweightEntry entry, bool isSymlink, ref long localSelfSize, ref int localFilesCount)
    {
        if (isSymlink && !_options.FollowSymlinks) return;

        localSelfSize += entry.Length;
        localFilesCount++;
    }

    private void ProcessDirectorySymlink(DirectoryNode node, DirectoryNode childNode, long targetLength, Dictionary<DirectoryNode, NodeDelta> localUpdates)
    {
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

    private bool IsRootExcluded(DirectoryNode rootNode)
    {
        if (_normalizedExcludedPaths.Count > 0 && IsExcludedPath(rootNode.Path))
            return true;

        if (_options.ExcludeMatcher.IsEmpty)
            return false;

        if (_useNativeIo)
            return _options.ExcludeMatcher.IsExcluded(rootNode.GetNativePath(nullTerminated: false));

        return _options.ExcludeMatcher.IsExcludedManagedPath(rootNode.Path);
    }

    private bool IsExcludedNativeEntry(byte[] parentNativePath, string parentPath, ReadOnlySpan<byte> name, ref byte[]? scratch)
    {
        if (!_options.ExcludeMatcher.IsEmpty && scratch != null)
        {
            int len = DuAccounting.WriteChildPathIntoPooled(parentNativePath, name, ref scratch);
            if (_options.ExcludeMatcher.IsExcluded(scratch.AsSpan(0, len)))
                return true;
        }

        if (_normalizedExcludedPaths.Count > 0)
        {
            string subPath = Path.Combine(parentPath, Encoding.UTF8.GetString(name));
            if (IsExcludedPath(subPath))
                return true;
        }

        return false;
    }

    private bool IsExcludedManagedSpan(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName)
    {
        if (!_options.ExcludeMatcher.IsEmpty && _options.ExcludeMatcher.IsExcludedManagedParts(directory, fileName))
            return true;

        if (_normalizedExcludedPaths.Count > 0)
        {
            string fullPath = Path.Join(directory, fileName);
            if (IsExcludedPath(fullPath))
                return true;
        }

        return false;
    }

    internal static void PrepareLegacyExcludes(
        IReadOnlyList<string>? excludedPaths,
        out HashSet<string>? normalizedExact,
        out List<string>? normalizedPrefixes)
    {
        if (excludedPaths == null || excludedPaths.Count == 0)
        {
            normalizedExact = null;
            normalizedPrefixes = null;
            return;
        }

        StringComparer pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        normalizedExact = new HashSet<string>(pathComparer);
        normalizedPrefixes = new List<string>(excludedPaths.Count);

        foreach (string excluded in excludedPaths)
        {
            if (string.IsNullOrEmpty(excluded)) continue;
            string norm = NormalizePath(excluded);
            normalizedExact.Add(norm);

            string prefix = norm;
            if (!norm.EndsWith(Path.DirectorySeparatorChar) && !norm.EndsWith(Path.AltDirectorySeparatorChar))
                prefix = norm + Path.DirectorySeparatorChar;
            normalizedPrefixes.Add(prefix);
        }
    }

    internal static bool MatchesLegacyExcludedPaths(
        string path,
        HashSet<string>? normalizedExact,
        List<string>? normalizedPrefixes)
    {
        if (normalizedExact == null || normalizedExact.Count == 0)
            return false;

        if (normalizedExact.Contains(path))
            return true;

        if (normalizedPrefixes == null || normalizedPrefixes.Count == 0)
            return false;

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPrefixes.Any(prefix => path.StartsWith(prefix, comparison));
    }

    private bool IsExcludedPath(string path) =>
        MatchesLegacyExcludedPaths(path, _normalizedExcludedPaths, _normalizedExcludedPrefixes);

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