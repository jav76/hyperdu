using System.Buffers;
using System.Diagnostics;
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
    private const int FanOutThreshold = 2;
    private static readonly int MaxPreopenedFds = OperatingSystem.IsMacOS() ? 48 : 128;

    private readonly ScanOptions _options;
    private long _activeWorkers;
    private DirectoryNode? _currentDirectoryNode;

    private long _directoriesScanned;
    private long _filesScanned;
    private bool _hasExcludes;
    private HashSet<string>? _normalizedExcludedPaths;
    private List<string>? _normalizedExcludedPrefixes;
    private long _totalBytesFound;
    private long _pendingCount;
    private bool _useNativeIo;
    private bool _trackSeen;
    private bool _workersSpawned;
    private bool _queueTouched;
    private int _preopenedFdCount;
    private ulong _rootDevice;

    private bool _inlineHasRootIdentity;
    private FileIdentity _inlineRootIdentity;
    private HashSet<FileIdentity>? _inlineSeenSet;
    private HashSet<FileIdentity>? _inlineActiveAncestors;

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
    public string CurrentDirectory => Volatile.Read(ref _currentDirectoryNode)?.Path ?? string.Empty;
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
        RootNode = rootNode;
        IsScanning = true;
        IsPaused = false;
        _pauseEvent.Set();

        try
        {
            if (_queueTouched)
            {
                Queue.Clear();
                _queueTouched = false;
            }

            _useNativeIo = _options.UseNativeIo && NativeIo.IsSupported;
            _trackSeen = _useNativeIo && !_options.CountLinks;
            _workersSpawned = false;
            _preopenedFdCount = 0;
            _rootDevice = 0;
            _inlineHasRootIdentity = false;
            _inlineRootIdentity = default;
            _inlineSeenSet = null;
            _inlineActiveAncestors = null;
            _seenLocks = null;
            _seenSets = null;

            _directoriesScanned = 0;
            _filesScanned = 0;
            _totalBytesFound = 0;
            _currentDirectoryNode = rootNode;

            _pendingCount = 0;
            _activeWorkers = 0;

            PrepareLegacyExcludes(
                _options.ExcludedPaths,
                out _normalizedExcludedPaths,
                out _normalizedExcludedPrefixes);

            _hasExcludes = (_normalizedExcludedPaths != null && _normalizedExcludedPaths.Count > 0) ||
                           !_options.ExcludeMatcher.IsEmpty;
            LastScanExcludedRoot = false;
            if (_hasExcludes && IsRootExcluded(rootNode))
            {
                LastScanExcludedRoot = true;
                rootNode.SelfSize = 0;
                rootNode.TotalSize = 0;
                rootNode.IsScanned = true;
                return rootNode;
            }

            cancellationToken.ThrowIfCancellationRequested();

            EnumerationOptions enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = _options.SkipHidden ? FileAttributes.Hidden | FileAttributes.System : 0
            };

            using Timer? progressTimer = progress != null
                ? new Timer(_ =>
                {
                    progress.Report(new ScanProgress(
                        Volatile.Read(ref _directoriesScanned),
                        Volatile.Read(ref _filesScanned),
                        Volatile.Read(ref _totalBytesFound),
                        CurrentDirectory,
                        Volatile.Read(ref _activeWorkers)
                    ));
                }, null, 0, 100)
                : null;

            await RunInlineOrFanOutAsync(rootNode, enumerationOptions, cancellationToken);

            if (progressTimer != null)
            {
                progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
                progress!.Report(new ScanProgress(
                    Volatile.Read(ref _directoriesScanned),
                    Volatile.Read(ref _filesScanned),
                    Volatile.Read(ref _totalBytesFound),
                    CurrentDirectory,
                    0
                ));
            }

            if (_workersSpawned)
                ComputeTotalSizesUnlocked(rootNode);

            if (!_options.Headless)
                rootNode.Compact();

            return rootNode;
        }
        finally
        {
            if (_queueTouched)
            {
                Queue.Clear();
                _queueTouched = false;
            }

            IsScanning = false;
            IsPaused = false;
            _seenLocks = null;
            _seenSets = null;
            _inlineSeenSet = null;
            _inlineActiveAncestors = null;
            _preopenedFdCount = 0;
            _workersSpawned = false;
            _pauseEvent.Set();
        }
    }

    private struct InlineFrame
    {
        public DirectoryNode Node;
        public List<DirectoryNode>? ExtraChildren;
        public int NextExtraIndex;
        public long PendingSize;
        public int PendingSubdirs;
        public int PendingFiles;
    }

    private async Task RunInlineOrFanOutAsync(
        DirectoryNode rootNode,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken)
    {
        List<InlineFrame>? activeFrames = null;
        int pendingUnstartedCount = 0;
        bool canCheckFanOut = _options.AutoDetectWorkerCount || _options.WorkerCount > 1;
        int inlineSinceFlush = 0;
        long lastFlushTick = _options.Headless ? 0 : Stopwatch.GetTimestamp();

        DirectoryNode? current = rootNode;
        if (!_options.Headless)
            Volatile.Write(ref _activeWorkers, 1);

        try
        {
            while (current != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pauseEvent.Wait(cancellationToken);

                Volatile.Write(ref _currentDirectoryNode, current);

                DirectoryNode? firstSubdir = null;
                List<DirectoryNode>? extraSubdirs = null;
                long selfSize = 0;
                int selfFiles = 0;
                long subdirSelfSize = 0;
                int subdirCount = 0;

                if (current.TryClaimForScan())
                {
                    string? error = ScanSingleDirectory(
                        current,
                        enumerationOptions,
                        cancellationToken,
                        out firstSubdir,
                        out extraSubdirs,
                        out selfSize,
                        out selfFiles,
                        out subdirSelfSize,
                        out subdirCount);

                    if (error != null)
                    {
                        MarkNodeFailed(current, error);
                    }
                    else
                    {
                        RecordDirectoryCompleted(current, selfSize, selfFiles);
                    }
                }
                else
                {
                    current.DisposePreopenedDirectory();
                    selfSize = 0;
                    selfFiles = 0;
                }

                long framePendingSize = selfSize + subdirSelfSize;
                int framePendingSubdirs = subdirCount;
                int framePendingFiles = selfFiles;

                int newChildrenCount = (firstSubdir != null ? 1 : 0) + (extraSubdirs?.Count ?? 0);
                int totalPending = pendingUnstartedCount + newChildrenCount;

                if (canCheckFanOut && totalPending >= FanOutThreshold)
                {
                    int workerCount = ResolveEffectiveWorkerCount(rootNode.Path);
                    if (workerCount > 1)
                    {
                        activeFrames ??= new List<InlineFrame>(1);
                        activeFrames.Add(new InlineFrame
                        {
                            Node = current,
                            ExtraChildren = extraSubdirs,
                            NextExtraIndex = 0,
                            PendingSize = framePendingSize,
                            PendingSubdirs = framePendingSubdirs,
                            PendingFiles = framePendingFiles
                        });
                        current = null;

                        FlushInlineFrames(activeFrames);
                        await TransitionToWorkersAndRunAsync(
                            activeFrames,
                            firstSubdir,
                            workerCount,
                            enumerationOptions,
                            cancellationToken);
                        return;
                    }

                    canCheckFanOut = false;
                }

                if (firstSubdir != null)
                {
                    activeFrames ??= new List<InlineFrame>(16);
                    activeFrames.Add(new InlineFrame
                    {
                        Node = current,
                        ExtraChildren = extraSubdirs,
                        NextExtraIndex = 0,
                        PendingSize = framePendingSize,
                        PendingSubdirs = framePendingSubdirs,
                        PendingFiles = framePendingFiles
                    });
                    if (extraSubdirs != null)
                        pendingUnstartedCount += extraSubdirs.Count;

                    if (!_options.Headless && (++inlineSinceFlush & 255) == 0)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (Stopwatch.GetElapsedTime(lastFlushTick, now).TotalMilliseconds >= 50)
                        {
                            FlushInlineFrames(activeFrames);
                            lastFlushTick = now;
                        }
                    }

                    current = firstSubdir;
                    if (_inlineActiveAncestors != null && current.Identity.Inode != 0 && current.Identity.Inode != ulong.MaxValue)
                        _inlineActiveAncestors.Add(current.Identity);
                    continue;
                }

                current.ApplyInlineDelta(framePendingSize, framePendingSubdirs, framePendingFiles);
                if (activeFrames == null || activeFrames.Count == 0)
                {
                    current = null;
                    break;
                }

                int topIdx = activeFrames.Count - 1;
                InlineFrame parentFrame = activeFrames[topIdx];
                parentFrame.PendingSize += framePendingSize;
                parentFrame.PendingSubdirs += framePendingSubdirs;
                parentFrame.PendingFiles += framePendingFiles;
                activeFrames[topIdx] = parentFrame;

                if (_inlineActiveAncestors != null && current.Identity.Inode != 0 && current.Identity.Inode != ulong.MaxValue)
                    _inlineActiveAncestors.Remove(current.Identity);

                current = null;
                while (activeFrames.Count > 0)
                {
                    topIdx = activeFrames.Count - 1;
                    InlineFrame top = activeFrames[topIdx];
                    if (top.ExtraChildren != null && top.NextExtraIndex < top.ExtraChildren.Count)
                    {
                        DirectoryNode nextSibling = top.ExtraChildren[top.NextExtraIndex++];
                        activeFrames[topIdx] = top;
                        pendingUnstartedCount--;
                        current = nextSibling;
                        if (_inlineActiveAncestors != null && current.Identity.Inode != 0 && current.Identity.Inode != ulong.MaxValue)
                            _inlineActiveAncestors.Add(current.Identity);
                        break;
                    }

                    activeFrames.RemoveAt(topIdx);
                    top.Node.ApplyInlineDelta(top.PendingSize, top.PendingSubdirs, top.PendingFiles);
                    if (activeFrames.Count > 0)
                    {
                        int pIdx = activeFrames.Count - 1;
                        InlineFrame grandParent = activeFrames[pIdx];
                        grandParent.PendingSize += top.PendingSize;
                        grandParent.PendingSubdirs += top.PendingSubdirs;
                        grandParent.PendingFiles += top.PendingFiles;
                        activeFrames[pIdx] = grandParent;
                    }

                    if (_inlineActiveAncestors != null && top.Node.Identity.Inode != 0 && top.Node.Identity.Inode != ulong.MaxValue)
                        _inlineActiveAncestors.Remove(top.Node.Identity);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when scan is canceled; cleanup runs in finally.
        }
        finally
        {
            if (!_workersSpawned)
            {
                current?.DisposePreopenedDirectory();

                if (activeFrames != null)
                {
                    FlushInlineFrames(activeFrames);
                    for (int i = 0; i < activeFrames.Count; i++)
                    {
                        InlineFrame frame = activeFrames[i];
                        if (frame.ExtraChildren != null)
                        {
                            for (int j = frame.NextExtraIndex; j < frame.ExtraChildren.Count; j++)
                                frame.ExtraChildren[j].DisposePreopenedDirectory();
                        }
                    }
                }

                Volatile.Write(ref _activeWorkers, 0);
            }
        }
    }

    private int ResolveEffectiveWorkerCount(string rootPath)
    {
        if (!_options.AutoDetectWorkerCount)
            return Math.Max(1, _options.WorkerCount);

        return MountHeuristics.ResolveWorkerBudget(null, rootPath, out _, out _);
    }

    private static void EnsureThreadPoolMinThreads(int workerCount)
    {
        ThreadPool.GetMinThreads(out int minWorkerThreads, out int minCompletionPortThreads);
        int target = Math.Max(workerCount, Environment.ProcessorCount);
        if (minWorkerThreads < target)
            ThreadPool.SetMinThreads(target, minCompletionPortThreads);
    }

    private static void FlushInlineFrames(List<InlineFrame> activeFrames)
    {
        for (int i = activeFrames.Count - 1; i >= 0; i--)
        {
            InlineFrame frame = activeFrames[i];
            if (frame.PendingSize == 0 && frame.PendingSubdirs == 0 && frame.PendingFiles == 0)
                continue;

            frame.Node.ApplyInlineDelta(frame.PendingSize, frame.PendingSubdirs, frame.PendingFiles);
            if (i > 0)
            {
                InlineFrame parent = activeFrames[i - 1];
                parent.PendingSize += frame.PendingSize;
                parent.PendingSubdirs += frame.PendingSubdirs;
                parent.PendingFiles += frame.PendingFiles;
                activeFrames[i - 1] = parent;
            }

            frame.PendingSize = 0;
            frame.PendingSubdirs = 0;
            frame.PendingFiles = 0;
            activeFrames[i] = frame;
        }
    }

    private async Task TransitionToWorkersAndRunAsync(
        List<InlineFrame> activeFrames,
        DirectoryNode? firstSubdir,
        int workerCount,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken)
    {
        Stack<DirectoryNode> callingLocalQueue = new Stack<DirectoryNode>();
        int queuedCount = 0;
        _queueTouched = true;

        try
        {
            for (int i = 0; i < activeFrames.Count; i++)
            {
                InlineFrame frame = activeFrames[i];
                if (frame.ExtraChildren == null)
                    continue;

                for (int j = frame.NextExtraIndex; j < frame.ExtraChildren.Count; j++)
                {
                    Queue.Enqueue(frame.ExtraChildren[j]);
                    queuedCount++;
                }

                frame.NextExtraIndex = frame.ExtraChildren.Count;
                activeFrames[i] = frame;
            }

            if (firstSubdir != null)
            {
                callingLocalQueue.Push(firstSubdir);
                queuedCount++;
            }

            if (queuedCount == 0)
            {
                Queue.Complete();
                Volatile.Write(ref _activeWorkers, 0);
                return;
            }

            _pendingCount = queuedCount;
            Volatile.Write(ref _activeWorkers, 0);
            MigrateSeenToStriped();
            _inlineActiveAncestors = null;
            _workersSpawned = true;
        }
        catch
        {
            firstSubdir?.DisposePreopenedDirectory();
            throw;
        }

        EnsureThreadPoolMinThreads(workerCount);

        Task[] workerTasks = new Task[workerCount];
        workerTasks[0] = Task.Run(
            () => ScanWorkerLoopAsync(callingLocalQueue, enumerationOptions, cancellationToken),
            CancellationToken.None);
        for (int i = 1; i < workerTasks.Length; i++)
        {
            workerTasks[i] = Task.Run(
                () => ScanWorkerLoopAsync(new Stack<DirectoryNode>(), enumerationOptions, cancellationToken),
                CancellationToken.None);
        }

        await Task.WhenAll(workerTasks);
    }

    private void MigrateSeenToStriped()
    {
        if (!_trackSeen)
            return;

        _seenLocks = new Lock[SeenStripeCount];
        _seenSets = new HashSet<FileIdentity>[SeenStripeCount];
        for (int i = 0; i < SeenStripeCount; i++)
        {
            _seenLocks[i] = new Lock();
            _seenSets[i] = new HashSet<FileIdentity>();
        }

        if (_inlineSeenSet != null)
        {
            foreach (FileIdentity id in _inlineSeenSet)
            {
                int stripe = (int)((uint)id.GetHashCode() & (SeenStripeCount - 1));
                _seenSets[stripe].Add(id);
            }

            _inlineSeenSet = null;
        }
        else if (_inlineHasRootIdentity)
        {
            int stripe = (int)((uint)_inlineRootIdentity.GetHashCode() & (SeenStripeCount - 1));
            _seenSets[stripe].Add(_inlineRootIdentity);
        }
    }

    private async Task ScanWorkerLoopAsync(
        Stack<DirectoryNode> localQueue,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken)
    {
        Dictionary<DirectoryNode, NodeDelta> localUpdates = new Dictionary<DirectoryNode, NodeDelta>();
        List<DirectoryNode> flushScratch = new List<DirectoryNode>();

        async Task<(bool Success, DirectoryNode? Node)> DequeueWorkAsync()
        {
            if (localQueue.Count > 0) return (true, localQueue.Pop());

            FlushLocalUpdates(localUpdates, flushScratch);

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
                Volatile.Write(ref _currentDirectoryNode, node);

                try
                {
                    ProcessDirectoryNodeWorker(node, localQueue, localUpdates, flushScratch, enumerationOptions, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
            while (localQueue.Count > 0)
                localQueue.Pop().DisposePreopenedDirectory();

            FlushLocalUpdates(localUpdates, flushScratch);
        }
    }

    private string? ScanSingleDirectory(
        DirectoryNode node,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken,
        out DirectoryNode? firstSubdir,
        out List<DirectoryNode>? extraSubdirs,
        out long selfSize,
        out int selfFiles,
        out long subdirSelfSize,
        out int subdirCount)
    {
        if (!_options.Headless)
            node.StartTicks = DateTime.UtcNow.Ticks;

        firstSubdir = null;
        extraSubdirs = null;
        selfSize = 0;
        selfFiles = 0;
        subdirSelfSize = 0;
        subdirCount = 0;
        string? error = null;

        try
        {
            if (_useNativeIo)
            {
                error = ReadDirectoryNative(
                    node,
                    ref selfSize,
                    ref selfFiles,
                    ref firstSubdir,
                    ref extraSubdirs,
                    ref subdirSelfSize,
                    ref subdirCount,
                    cancellationToken);
            }
            else
            {
                ReadDirectoryManaged(
                    node,
                    ref selfSize,
                    ref selfFiles,
                    ref firstSubdir,
                    ref extraSubdirs,
                    ref subdirSelfSize,
                    ref subdirCount,
                    enumerationOptions,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            DisposeSubdirs(firstSubdir, extraSubdirs);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access Denied";
        }
        catch (DirectoryNotFoundException)
        {
            error = "Directory Not Found";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error != null)
        {
            DisposeSubdirs(firstSubdir, extraSubdirs);
            firstSubdir = null;
            extraSubdirs = null;
            selfSize = 0;
            selfFiles = 0;
        }

        return error;
    }

    private void RecordDirectoryCompleted(DirectoryNode node, long selfSize, int selfFiles)
    {
        node.SelfSize += selfSize;
        node.SelfFileCount = selfFiles;
        if (!_options.Headless)
            node.EndTicks = DateTime.UtcNow.Ticks;
        node.IsScanned = true;

        if (!_workersSpawned)
        {
            if (_options.Headless)
            {
                _directoriesScanned++;
                if (selfFiles > 0) _filesScanned += selfFiles;
                if (selfSize > 0) _totalBytesFound += selfSize;
            }
            else
            {
                Volatile.Write(ref _directoriesScanned, _directoriesScanned + 1);
                if (selfFiles > 0) Volatile.Write(ref _filesScanned, _filesScanned + selfFiles);
                if (selfSize > 0) Volatile.Write(ref _totalBytesFound, _totalBytesFound + selfSize);
            }
        }
        else
        {
            Interlocked.Increment(ref _directoriesScanned);
            if (selfFiles > 0) Interlocked.Add(ref _filesScanned, selfFiles);
            if (selfSize > 0) Interlocked.Add(ref _totalBytesFound, selfSize);
        }
    }

    private void ProcessDirectoryNodeWorker(
        DirectoryNode node,
        Stack<DirectoryNode> localQueue,
        Dictionary<DirectoryNode, NodeDelta> localUpdates,
        List<DirectoryNode> flushScratch,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken)
    {
        if (!node.TryClaimForScan())
        {
            node.DisposePreopenedDirectory();
            return;
        }

        string? error = ScanSingleDirectory(
            node,
            enumerationOptions,
            cancellationToken,
            out DirectoryNode? firstSubdir,
            out List<DirectoryNode>? extraSubdirs,
            out long localSelfSize,
            out int localFilesCount,
            out long subdirSelfSize,
            out int subdirCount);

        if (error != null)
        {
            if (subdirCount > 0 || subdirSelfSize > 0)
                QueueLocalUpdate(localUpdates, node, subdirSelfSize, subdirCount, 0);
            MarkNodeFailed(node, error);
            return;
        }

        RecordDirectoryCompleted(node, localSelfSize, localFilesCount);
        QueueLocalUpdate(localUpdates, node, localSelfSize + subdirSelfSize, subdirCount, localFilesCount);

        EnqueueSubdirectories(firstSubdir, extraSubdirs, localQueue);

        if (localUpdates.Count >= 50) FlushLocalUpdates(localUpdates, flushScratch);
    }

    private void EnqueueSubdirectories(DirectoryNode? firstSubdir, List<DirectoryNode>? extraSubdirs, Stack<DirectoryNode> localQueue)
    {
        if (firstSubdir == null) return;

        if (extraSubdirs == null)
        {
            Interlocked.Increment(ref _pendingCount);
            localQueue.Push(firstSubdir);
        }
        else
        {
            Interlocked.Add(ref _pendingCount, 1 + extraSubdirs.Count);
            foreach (DirectoryNode sub in extraSubdirs)
                localQueue.Push(sub);
            localQueue.Push(firstSubdir);
        }

        if (localQueue.Count > 4)
        {
            DirectoryNode keepTop = localQueue.Pop();
            while (localQueue.Count > 1)
                Queue.Enqueue(localQueue.Pop());
            localQueue.Push(keepTop);
        }
    }

    private static void DisposeSubdirs(DirectoryNode? firstSubdir, List<DirectoryNode>? extraSubdirs)
    {
        firstSubdir?.DisposePreopenedDirectory();
        if (extraSubdirs == null)
            return;

        for (int i = 0; i < extraSubdirs.Count; i++)
            extraSubdirs[i].DisposePreopenedDirectory();
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

    private void ReadDirectoryManaged(
        DirectoryNode node,
        ref long localSelfSize,
        ref int localFilesCount,
        ref DirectoryNode? firstSubdir,
        ref List<DirectoryNode>? extraSubdirs,
        ref long subdirSelfSize,
        ref int subdirCount,
        EnumerationOptions enumerationOptions,
        CancellationToken cancellationToken)
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
                ProcessDirectoryEntry(entry, node, isSymlink, ref firstSubdir, ref extraSubdirs, ref subdirSelfSize, ref subdirCount);
            }
            else
            {
                ProcessFileEntry(entry, isSymlink, ref localSelfSize, ref localFilesCount);
            }
        }
    }

    /// <summary>
    /// Reads one directory with libc/NtQuery: names stay raw bytes, every stat is relative to the open directory,
    /// and subdirectories are pre-opened via openat within a bounded FD budget to avoid O(D^2) path re-walking.
    /// </summary>
    private string? ReadDirectoryNative(
        DirectoryNode node,
        ref long localSelfSize,
        ref int localFilesCount,
        ref DirectoryNode? firstSubdir,
        ref List<DirectoryNode>? extraSubdirs,
        ref long subdirSelfSize,
        ref int subdirCount,
        CancellationToken cancellationToken)
    {
        NativeDirectory? directory = node.TakePreopenedDirectory();
        if (directory != null)
        {
            Interlocked.Decrement(ref _preopenedFdCount);
        }
        else
        {
            if (node.PreopenError != 0)
                return NativeIo.DescribeError(node.PreopenError);

            directory = NativeDirectory.OpenPathSafe(
                node.GetNativePath(),
                out int openError,
                followSymlinks: node.Parent == null || _options.FollowSymlinks);
            if (directory == null) return NativeIo.DescribeError(openError);
        }

        Span<byte> statBuffer = stackalloc byte[NativeIo.StatBufferSize];
        bool checkDotHidden = _options.SkipHidden && !NativeIo.IsWindows;
        bool checkHiddenFlag = _options.SkipHidden && (NativeIo.IsMacOS || NativeIo.IsWindows);

        byte[]? parentNativePath = _hasExcludes && !_options.ExcludeMatcher.IsEmpty
            ? node.GetNativePath(nullTerminated: false)
            : null;
        byte[]? excludeScratch = parentNativePath != null
            ? ArrayPool<byte>.Shared.Rent(Math.Max(1024, parentNativePath.Length + 256))
            : null;
        bool hitFdLimit = false;

        try
        {
            using (directory)
            {
                if (node.Parent == null && directory.TryStatSelf(statBuffer, out NativeStat rootStat))
                {
                    _rootDevice = rootStat.Device;
                    node.Identity = new FileIdentity(rootStat.Device, rootStat.Inode);
                    _ = TryMarkSeen(in rootStat);
                    if (!_workersSpawned && _options.FollowSymlinks && _options.CountLinks && rootStat.HasValidFileId)
                        (_inlineActiveAncestors ??= new HashSet<FileIdentity>()).Add(node.Identity);
                    localSelfSize += NativeIo.ComputeEntryByteSize(in rootStat, _options.ApparentSize);
                }

                while (directory.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    ReadOnlySpan<byte> name = directory.CurrentName;
                    if (checkDotHidden && name[0] == (byte)'.') continue;
                    if (_hasExcludes && IsExcludedNativeEntry(parentNativePath, node, name, ref excludeScratch))
                        continue;
                    if (!directory.TryStatCurrent(false, statBuffer, out NativeStat linkStat)) continue;
                    if (checkHiddenFlag && linkStat.HasHiddenFlag) continue;

                    switch (linkStat.Kind)
                    {
                        case NativeEntryKind.Directory:
                            TryAddNativeDirectory(
                                directory,
                                isSymlink: false,
                                node,
                                name,
                                in linkStat,
                                ref hitFdLimit,
                                ref firstSubdir,
                                ref extraSubdirs,
                                ref subdirSelfSize,
                                ref subdirCount);
                            break;

                        case NativeEntryKind.Symlink:
                            if (_options.FollowSymlinks)
                            {
                                if (directory.TryStatCurrent(true, statBuffer, out NativeStat targetStat))
                                {
                                    if (targetStat.Kind == NativeEntryKind.Directory)
                                    {
                                        TryAddNativeDirectory(
                                            directory,
                                            isSymlink: true,
                                            node,
                                            name,
                                            in targetStat,
                                            ref hitFdLimit,
                                            ref firstSubdir,
                                            ref extraSubdirs,
                                            ref subdirSelfSize,
                                            ref subdirCount);
                                    }
                                    else
                                    {
                                        TryRecordNativeFile(in targetStat, ref localSelfSize, ref localFilesCount);
                                    }
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
                                ProcessUnfollowedDirectorySymlink(name, node, linkOwnBytes, ref subdirSelfSize, ref subdirCount);
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

    private void TryAddNativeDirectory(
        NativeDirectory parentDir,
        bool isSymlink,
        DirectoryNode parent,
        ReadOnlySpan<byte> name,
        in NativeStat dirStat,
        ref bool hitFdLimit,
        ref DirectoryNode? firstSubdir,
        ref List<DirectoryNode>? extraSubdirs,
        ref long subdirSelfSize,
        ref int subdirCount)
    {
        if (_options.OneFileSystem && _rootDevice != 0 && dirStat.Device != _rootDevice)
            return;

        if (IsNativeDirectoryCycleOrSeen(parent, in dirStat, out FileIdentity dirId))
            return;

        byte[]? exactName = Utf8.IsValid(name) ? null : name.ToArray();
        DirectoryNode? childNode = CreateChildNode(parent, Encoding.UTF8.GetString(name), exactName);
        if (childNode == null) return;

        long dirOwnBytes = NativeIo.ComputeEntryByteSize(in dirStat, _options.ApparentSize);
        childNode.Identity = dirId;
        childNode.SelfSize = dirOwnBytes;
        childNode.TotalSize = dirOwnBytes;

        // Pre-open only the first subdirectory (which this worker/inline loop descends into next via openat),
        // keeping open FDs strictly bounded and avoiding sequential openat bursts on wide fan-out directories.
        if (firstSubdir == null)
            TryPreopenChildDirectory(parentDir, isSymlink, childNode, ref hitFdLimit);
        AddSubdirectory(parent, childNode, ref firstSubdir, ref extraSubdirs, ref subdirSelfSize, ref subdirCount);
    }

    private void TryPreopenChildDirectory(NativeDirectory parentDir, bool isSymlink, DirectoryNode childNode, ref bool hitFdLimit)
    {
        if (hitFdLimit || (isSymlink && NativeIo.IsWindows))
            return;

        if (Volatile.Read(ref _preopenedFdCount) >= MaxPreopenedFds)
            return;

        if (Interlocked.Increment(ref _preopenedFdCount) > MaxPreopenedFds)
        {
            Interlocked.Decrement(ref _preopenedFdCount);
            return;
        }

        NativeDirectory? childDir = parentDir.OpenCurrentDirectory(out int openError, followSymlinks: _options.FollowSymlinks);
        if (childDir != null)
        {
            childNode.SetPreopenedDirectory(childDir);
            return;
        }

        Interlocked.Decrement(ref _preopenedFdCount);
        if (NativeDirectory.IsTooManyOpenFilesError(openError))
        {
            hitFdLimit = true;
            return;
        }

        // For real POSIX directories when not following symlinks, cache non-EMFILE open errors so
        // processing does not re-walk long paths only to observe the same failure.
        if (!isSymlink && !_options.FollowSymlinks && !NativeIo.IsWindows)
            childNode.PreopenError = openError;
    }

    private bool IsNativeDirectoryCycleOrSeen(DirectoryNode parent, in NativeStat dirStat, out FileIdentity dirId)
    {
        dirId = new FileIdentity(dirStat.Device, dirStat.Inode);

        if (_trackSeen)
        {
            if (!dirStat.HasValidFileId)
                return false;

            return !TryMarkSeen(in dirStat);
        }

        if (!_options.FollowSymlinks || !dirStat.HasValidFileId)
            return false;

        if (!_workersSpawned)
            return _inlineActiveAncestors != null && _inlineActiveAncestors.Contains(dirId);

        return HasAncestorIdentity(parent, dirId);
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
        if (!_trackSeen || !stat.HasValidFileId)
            return true;

        bool shouldTrack = stat.Kind == NativeEntryKind.Directory ||
                           stat.HardLinks != 1 ||
                           _options.FollowSymlinks;
        if (!shouldTrack)
            return true;

        FileIdentity id = new(stat.Device, stat.Inode);
        if (!_workersSpawned)
        {
            if (!_inlineHasRootIdentity)
            {
                _inlineRootIdentity = id;
                _inlineHasRootIdentity = true;
                return true;
            }

            if (_inlineSeenSet == null)
            {
                if (id == _inlineRootIdentity)
                    return false;

                _inlineSeenSet = new HashSet<FileIdentity>(4) { _inlineRootIdentity, id };
                return true;
            }

            return _inlineSeenSet.Add(id);
        }

        int stripe = (int)((uint)id.GetHashCode() & (SeenStripeCount - 1));
        lock (_seenLocks![stripe])
        {
            return _seenSets![stripe].Add(id);
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

    private void ProcessUnfollowedDirectorySymlink(
        ReadOnlySpan<byte> nativeName,
        DirectoryNode node,
        long linkOwnBytes,
        ref long subdirSelfSize,
        ref int subdirCount)
    {
        byte[]? exactName = Utf8.IsValid(nativeName) ? null : nativeName.ToArray();
        DirectoryNode? childNode = CreateChildNode(node, Encoding.UTF8.GetString(nativeName), exactName);
        if (childNode == null) return;

        ProcessDirectorySymlink(node, childNode, linkOwnBytes, ref subdirSelfSize, ref subdirCount);
    }

    private static DirectoryNode? CreateChildNode(DirectoryNode parent, string name, byte[]? nativeName)
    {
        if (!OperatingSystem.IsWindows() &&
            parent.Depth == 0 &&
            ((parent.Path == "/" && name is "proc" or "sys" or "dev" or "run") ||
             IsVirtualOrSystemPath(parent.Path)))
        {
            return null;
        }

        return new DirectoryNode(parent, name, nativeName);
    }

    private void ProcessDirectoryEntry(
        LightweightEntry entry,
        DirectoryNode node,
        bool isSymlink,
        ref DirectoryNode? firstSubdir,
        ref List<DirectoryNode>? extraSubdirs,
        ref long subdirSelfSize,
        ref int subdirCount)
    {
        DirectoryNode? childNode = CreateChildNode(node, entry.Name, null);
        if (childNode == null) return;

        if (isSymlink)
        {
            if (!_options.FollowSymlinks)
            {
                string? linkTarget = ResolveLinkTargetSafely(childNode.Path);
                ProcessDirectorySymlink(node, childNode, linkTarget?.Length ?? 0, ref subdirSelfSize, ref subdirCount);
                return;
            }

            if (IsManagedSymlinkCycle(node, childNode.Path))
                return;
        }

        AddSubdirectory(node, childNode, ref firstSubdir, ref extraSubdirs, ref subdirSelfSize, ref subdirCount);
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

    private void AddSubdirectory(
        DirectoryNode node,
        DirectoryNode childNode,
        ref DirectoryNode? firstSubdir,
        ref List<DirectoryNode>? extraSubdirs,
        ref long subdirSelfSize,
        ref int subdirCount)
    {
        if (firstSubdir == null)
        {
            firstSubdir = childNode;
        }
        else
        {
            extraSubdirs ??= new List<DirectoryNode>(4);
            extraSubdirs.Add(childNode);
        }

        if (_options.Headless && !_workersSpawned)
        {
            node.Subdirectories.Add(childNode);
            if (childNode.SelfSize > 0)
                _totalBytesFound += childNode.SelfSize;
        }
        else
        {
            lock (node.Subdirectories)
            {
                node.Subdirectories.Add(childNode);
            }

            if (childNode.SelfSize > 0)
                Interlocked.Add(ref _totalBytesFound, childNode.SelfSize);
        }

        subdirSelfSize += childNode.SelfSize;
        subdirCount++;
    }

    private void ProcessFileEntry(LightweightEntry entry, bool isSymlink, ref long localSelfSize, ref int localFilesCount)
    {
        if (isSymlink && !_options.FollowSymlinks) return;

        localSelfSize += entry.Length;
        localFilesCount++;
    }

    private void ProcessDirectorySymlink(
        DirectoryNode node,
        DirectoryNode childNode,
        long targetLength,
        ref long subdirSelfSize,
        ref int subdirCount)
    {
        childNode.SelfSize = targetLength;
        childNode.TotalSize = targetLength;
        childNode.IsScanned = true;

        if (_options.Headless && !_workersSpawned)
        {
            node.Subdirectories.Add(childNode);
            _directoriesScanned++;
        }
        else
        {
            lock (node.Subdirectories)
            {
                node.Subdirectories.Add(childNode);
            }

            Interlocked.Increment(ref _directoriesScanned);
        }

        subdirSelfSize += targetLength;
        subdirCount++;
    }

    private void MarkNodeFailed(DirectoryNode node, string errorMessage)
    {
        node.ErrorMessage = errorMessage;
        if (!_options.Headless)
            node.EndTicks = DateTime.UtcNow.Ticks;
        node.IsScanned = true;

        if (_options.Headless && !_workersSpawned)
            _directoriesScanned++;
        else
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

    private static void FlushLocalUpdates(Dictionary<DirectoryNode, NodeDelta> localUpdates, List<DirectoryNode> nodes)
    {
        if (localUpdates.Count == 0) return;

        nodes.Clear();
        nodes.AddRange(localUpdates.Keys);
        for (int i = 0; i < nodes.Count; i++)
        {
            DirectoryNode? current = nodes[i].Parent;
            while (current != null && !localUpdates.ContainsKey(current))
            {
                localUpdates[current] = default;
                nodes.Add(current);
                current = current.Parent;
            }
        }

        nodes.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        for (int i = 0; i < nodes.Count; i++)
        {
            DirectoryNode node = nodes[i];
            NodeDelta delta = localUpdates[node];
            ApplyNodeDelta(node, delta);

            if (node.Parent != null)
            {
                PropagateDeltaToParent(node.Parent, delta, localUpdates);
            }
        }

        localUpdates.Clear();
        nodes.Clear();
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
        Stack<(DirectoryNode Node, bool ChildrenVisited)> stack = new Stack<(DirectoryNode, bool)>();
        stack.Push((node, false));

        while (stack.Count > 0)
        {
            (DirectoryNode current, bool visited) = stack.Pop();
            if (!visited)
            {
                DirectoryNode[] subs;
                lock (current.Subdirectories)
                {
                    if (current.Subdirectories.Count == 0)
                    {
                        current.TotalSize = current.SelfSize;
                        continue;
                    }

                    subs = current.Subdirectories.ToArray();
                }

                stack.Push((current, true));
                for (int i = subs.Length - 1; i >= 0; i--)
                    stack.Push((subs[i], false));
            }
            else
            {
                long total = current.SelfSize;
                lock (current.Subdirectories)
                {
                    for (int i = 0; i < current.Subdirectories.Count; i++)
                        total += current.Subdirectories[i].TotalSize;
                }

                current.TotalSize = total;
            }
        }

        return node.TotalSize;
    }

    private static void ComputeTotalSizesUnlocked(DirectoryNode node)
    {
        Stack<(DirectoryNode Node, bool ChildrenVisited)> stack = new Stack<(DirectoryNode, bool)>();
        stack.Push((node, false));

        while (stack.Count > 0)
        {
            (DirectoryNode current, bool visited) = stack.Pop();
            List<DirectoryNode> subs = current.Subdirectories;
            if (!visited)
            {
                if (subs.Count == 0)
                {
                    current.TotalSize = current.SelfSize;
                    continue;
                }

                stack.Push((current, true));
                for (int i = subs.Count - 1; i >= 0; i--)
                    stack.Push((subs[i], false));
            }
            else
            {
                long total = current.SelfSize;
                for (int i = 0; i < subs.Count; i++)
                    total += subs[i].TotalSize;

                current.TotalSize = total;
            }
        }
    }

    private bool IsRootExcluded(DirectoryNode rootNode)
    {
        if (_normalizedExcludedPaths != null && _normalizedExcludedPaths.Count > 0 && IsExcludedPath(rootNode.Path))
            return true;

        if (_options.ExcludeMatcher.IsEmpty)
            return false;

        if (_useNativeIo)
            return _options.ExcludeMatcher.IsExcluded(rootNode.GetNativePath(nullTerminated: false));

        return _options.ExcludeMatcher.IsExcludedManagedPath(rootNode.Path);
    }

    private bool IsExcludedNativeEntry(byte[]? parentNativePath, DirectoryNode parentNode, ReadOnlySpan<byte> name, ref byte[]? scratch)
    {
        if (!_options.ExcludeMatcher.IsEmpty && parentNativePath != null && scratch != null)
        {
            int len = DuAccounting.WriteChildPathIntoPooled(parentNativePath, name, ref scratch);
            if (_options.ExcludeMatcher.IsExcluded(scratch.AsSpan(0, len)))
                return true;
        }

        if (_normalizedExcludedPaths != null && _normalizedExcludedPaths.Count > 0)
        {
            string subPath = Path.Combine(parentNode.Path, Encoding.UTF8.GetString(name));
            if (IsExcludedPath(subPath))
                return true;
        }

        return false;
    }

    private bool IsExcludedManagedSpan(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName)
    {
        if (!_options.ExcludeMatcher.IsEmpty && _options.ExcludeMatcher.IsExcludedManagedParts(directory, fileName))
            return true;

        if (_normalizedExcludedPaths != null && _normalizedExcludedPaths.Count > 0)
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
