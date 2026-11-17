using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;
using hyperdu.Core.Scanning;

namespace hyperdu.Core.Du;

/// <summary>
/// Phase 4 parallel GNU-compatible du engine.
/// Starts inline with zero extra threads or mount lookups on small/deep single-chain trees,
/// adaptively fans out as pending work grows (respecting HDD=1, network>=32, and HYPERDU_THREADS),
/// schedules directories across 3 frontier priority tiers with bounded-memory backpressure,
/// and replays exact GNU post-order output on a single emitter thread.
/// </summary>
public sealed unsafe class ParallelDuWalker
{
    private const int StatePending = 0;
    private const int StateClaimed = 1;
    private const int StateCompleted = 2;
    private const int StateCancelled = 3;

    private const int FanOutSiblingThreshold = 2;
    private const int LargeDirChunkThreshold = 512;
    private const int BackpressureHighWatermark = 24576;
    private const int BackpressureLowWatermark = 8192;

    private enum ScannedEntryKind : byte
    {
        NonDir = 0,
        Subdir = 1,
        StatError = 2,
        ReadError = 3,
        BsdSymlinkLoop = 4,
        BsdSymlinkCycle = 5
    }

    private readonly record struct RawDirEntry(ulong Inode, int Order, int NameOffset, int NameLength);

    private sealed class RawDirEntryInodeComparer : IComparer<RawDirEntry>
    {
        public static readonly RawDirEntryInodeComparer Instance = new();

        public int Compare(RawDirEntry x, RawDirEntry y)
        {
            int cmp = x.Inode.CompareTo(y.Inode);
            return cmp != 0 ? cmp : x.Order.CompareTo(y.Order);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScannedEntry
    {
        public ulong Value;
        public ulong HardLinks;
        public FileIdentity Identity;
        public FileTimestamp Time;
        public int SubdirOrErrno;
        public int NameOffset;
        public int NameLength;
        public ScannedEntryKind Kind;
        public bool TrackIdentity;
    }

    private sealed class DirNode
    {
        public readonly DirNode? Parent;
        private readonly byte[] _pathOrSegmentNullTerm;
        public readonly FileIdentity Identity;
        public readonly ulong Depth;
        public readonly ulong OwnBytes;
        public readonly FileTimestamp OwnTime;
        public readonly int FullDisplayLength;
        public readonly uint HardLinks;
        public readonly bool FollowChildSymlinks;
        public readonly bool SkipDescent;
        public readonly bool HasValidFileId;
        public readonly bool NeedsChildSeparator;

        public int PreOpenedFd = -1;
        public int OpenErrno;
        public ulong InlineNonDirBytes;
        public FileTimestamp InlineMaxTime;
        public bool HasInlineTime;
        public ScannedEntry[]? Entries;
        public int EntryCount;
        public byte[]? NameArena;
        public DirNode? SingleSubdir;
        public DirNode?[]? Subdirs;
        public int SubdirCount;
        public int UnprintedWeight;

        public int State;
        public int Promoted;

        public DirNode(
            DirNode? parent,
            byte[] pathOrSegmentNullTerm,
            in NativeStat dirStat,
            FileIdentity identity,
            ulong depth,
            DuOptions options,
            bool followChildSymlinks,
            bool skipDescent = false)
        {
            Parent = parent;
            _pathOrSegmentNullTerm = pathOrSegmentNullTerm;
            Identity = identity;
            Depth = depth;
            OwnBytes = DuAccounting.ComputeEntryValue(in dirStat, options);
            OwnTime = DuAccounting.ExtractTimestamp(in dirStat, options);
            HardLinks = dirStat.HardLinks;
            HasValidFileId = dirStat.HasValidFileId;
            FollowChildSymlinks = followChildSymlinks;
            SkipDescent = skipDescent;

            int segDisplayLen = pathOrSegmentNullTerm.Length > 0 ? pathOrSegmentNullTerm.Length - 1 : 0;
            if (parent == null)
            {
                FullDisplayLength = segDisplayLen;
                NeedsChildSeparator = segDisplayLen > 0 && !DuAccounting.IsSeparator(pathOrSegmentNullTerm[segDisplayLen - 1]);
            }
            else
            {
                FullDisplayLength = parent.FullDisplayLength + (parent.NeedsChildSeparator ? 1 : 0) + segDisplayLen;
                NeedsChildSeparator = FullDisplayLength > 0 &&
                                      (segDisplayLen > 0
                                          ? !DuAccounting.IsSeparator(pathOrSegmentNullTerm[segDisplayLen - 1])
                                          : parent.NeedsChildSeparator);
            }

            if (skipDescent)
                State = StateCompleted;
        }

        public ReadOnlySpan<byte> SegmentOrRootDisplaySpan =>
            _pathOrSegmentNullTerm.Length > 0 ? _pathOrSegmentNullTerm.AsSpan(0, _pathOrSegmentNullTerm.Length - 1) : [];

        public byte[] RawPathOrSegmentNullTerm => _pathOrSegmentNullTerm;
    }

    private sealed class LargeDirBatch
    {
        public readonly DirNode Node;
        public readonly int DirFd;
        public readonly NativeDirectory? FallbackDir;
        public readonly RawDirEntry[] RawEntries;
        public readonly int RawCount;
        public readonly byte[] RawNameArena;
        public readonly ChunkSpec[] Chunks;
        public readonly int FinalReadErrno;
        public int RemainingChunks;
        public int ResourcesReleased;

        public LargeDirBatch(
            DirNode node,
            int dirFd,
            NativeDirectory? fallbackDir,
            RawDirEntry[] rawEntries,
            int rawCount,
            byte[] rawNameArena,
            ChunkSpec[] chunks,
            int finalReadErrno)
        {
            Node = node;
            DirFd = dirFd;
            FallbackDir = fallbackDir;
            RawEntries = rawEntries;
            RawCount = rawCount;
            RawNameArena = rawNameArena;
            Chunks = chunks;
            FinalReadErrno = finalReadErrno;
            RemainingChunks = chunks.Length;
        }
    }

    private sealed class ChunkSpec
    {
        public readonly int Start;
        public readonly int End;
        public ulong InlineNonDirBytes;
        public FileTimestamp InlineMaxTime;
        public bool HasInlineTime;
        public List<ScannedEntry>? KeptEntries;
        public List<DirNode>? Subdirs;
        public byte[]? Arena;
        public int ArenaLen;

        public ChunkSpec(int start, int end)
        {
            Start = start;
            End = end;
        }
    }

    private readonly struct WorkItem
    {
        public readonly DirNode? Node;
        public readonly LargeDirBatch? Batch;
        public readonly int ChunkIndex;

        public bool IsChunk => Batch != null;

        private WorkItem(DirNode? node, LargeDirBatch? batch, int chunkIndex)
        {
            Node = node;
            Batch = batch;
            ChunkIndex = chunkIndex;
        }

        public static WorkItem ForDir(DirNode node) => new(node, null, 0);
        public static WorkItem ForChunk(LargeDirBatch batch, int chunkIndex) => new(null, batch, chunkIndex);
    }

    private sealed class WorkerScratch
    {
        public byte[] DirentBuffer = new byte[4096];
        public readonly byte[] StatBuffer = new byte[NativeIo.StatBufferSize];
        public byte[] PathScratch = new byte[1024];
        public byte[]? ParentPathBuffer;
        public byte[] TempNameArena = new byte[4096];
        public int TempNameArenaLen;
        public RawDirEntry[] TempEntries = new RawDirEntry[64];
        public ScannedEntry[] TempScanned = new ScannedEntry[64];
        public readonly List<DirNode> TempSubdirs = new(16);
        public byte[] CompactArena = [];
        public int CompactArenaLen;
        public NativeStat[] TempInlineStats = [];

        public DirNode? AncestorTip;
        public List<DirNode>? AncestorPath;
        public HashSet<FileIdentity>? AncestorSet;
        public List<DirNode>? TempAlignNodes;

        public void ResetAncestors()
        {
            AncestorTip = null;
            AncestorPath?.Clear();
            AncestorSet?.Clear();
            TempAlignNodes?.Clear();
        }
    }

    private struct EmitFrame
    {
        public DirNode Node;
        public int EntryIndex;
        public int SavedPathLen;
        public ulong DirectNonDirBytes;
        public ulong DirectSubdirReportedBytes;
        public ulong SubdirFullBytes;
        public FileTimestamp DirectMaxTime;
        public bool HasSubdirReportedTime;
        public FileTimestamp SubdirReportedMaxTime;
        public bool HasSubdirFullTime;
        public FileTimestamp SubdirFullMaxTime;
    }

    private readonly DuOptions _options;
    private readonly string _programName;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly int? _requestedThreads;
    private readonly DuIdentityTracker _seen;
    private readonly HashSet<FileIdentity> _activeAncestors = [];
    private readonly ulong _maxDepth;
    private readonly bool _hasExcludes;
    private readonly bool _showTime;
    private bool _trackSingleLinkFiles;
    private ulong _currentRootDevice;
    private WorkerScratch? _inlineScratch;

    private readonly object _emitterSync = new();
    private readonly object _workerSpawnLock = new();
    private readonly List<Thread> _workerThreads = [];
    private readonly List<LargeDirBatch> _activeLargeBatches = [];

    private DuOutputWriter? _writer;
    private FrontierWorkQueue<WorkItem>? _queue;
    private DirNode? _frontierTarget;
    private DirNode? _waitingOnNode;
    private CancellationToken _cancellationToken;

    private EmitFrame[]? _emitStack;
    private int _emitStackDepth;
    private byte[]? _emitPathBuf;
    private int _emitPathLen;

    private ulong _grandTotal;
    private FileTimestamp _grandMaxTime = new(0, 0);

    private long _unprintedEntries;
    private long _peakUnprintedEntries;
    private int _backpressurePauseCount;
    private int _chunkStatBatches;
    private int _pendingInlineSiblings;
    private bool _heuristicsResolved;
    private bool _canEverFanOut = true;
    private volatile bool _workersStarted;
    private int _maxWorkerBudget = 1;
    private int _spawnedWorkers;
    private volatile bool _stoppedEarly;

    internal int WorkersStartedCount => Volatile.Read(ref _spawnedWorkers);
    internal long PeakUnprintedEntries => Volatile.Read(ref _peakUnprintedEntries);
    internal int BackpressurePauseCount => Volatile.Read(ref _backpressurePauseCount);
    internal int ChunkStatBatchesTriggered => Volatile.Read(ref _chunkStatBatches);
    internal int EffectiveMaxWorkers => _maxWorkerBudget;

    public ParallelDuWalker(DuOptions options, string programName, Stream stdout, Stream stderr)
        : this(options, programName, Stream.Null, stdout, stderr, null)
    {
    }

    public ParallelDuWalker(DuOptions options, string programName, Stream stdin, Stream stdout, Stream stderr)
        : this(options, programName, stdin, stdout, stderr, null)
    {
    }

    internal ParallelDuWalker(
        DuOptions options,
        string programName,
        Stream stdin,
        Stream stdout,
        Stream stderr,
        int? workerBudgetOverride)
    {
        _options = options;
        _programName = programName;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _seen = new DuIdentityTracker(options.Dialect);
        _requestedThreads = workerBudgetOverride ?? MountHeuristics.ParseWorkerCount(Environment.GetEnvironmentVariable("HYPERDU_THREADS"));
        if (_requestedThreads == 1)
        {
            _heuristicsResolved = true;
            _canEverFanOut = false;
            _maxWorkerBudget = 1;
        }

        _maxDepth = options.Summarize ? 0UL : (options.MaxDepth ?? ulong.MaxValue);
        _hasExcludes = !options.Excludes.IsEmpty;
        _showTime = options.ShowTime;
        _trackSingleLinkFiles = options.Dialect == DuDialect.Gnu &&
                                (options.Operands.Count > 1 || options.Files0From != null);
    }

    public static void RestoreDefaultSigpipe() => NativeIo.RestoreDefaultSigpipe();

    public int Run() => RunCore(() =>
    {
        if (_options.Files0From != null)
        {
            if (!Files0Reader.ReadAll(_options.Files0From, _stdin, _programName, _stderr, ProcessOperand))
                _writer!.HasError = true;
            return;
        }

        Span<byte> operandStackBuf = stackalloc byte[512];
        foreach (string operand in _options.Operands)
        {
            if (_cancellationToken.IsCancellationRequested || _stoppedEarly)
                break;

            int maxBytes = Encoding.UTF8.GetMaxByteCount(operand.Length);
            if (maxBytes <= operandStackBuf.Length)
            {
                int len = Encoding.UTF8.GetBytes(operand, operandStackBuf);
                ProcessOperand(operandStackBuf[..len]);
            }
            else
            {
                ProcessOperand(Encoding.UTF8.GetBytes(operand));
            }
        }
    }, CancellationToken.None);

    internal int Run(IReadOnlyList<byte[]> rawOperands, CancellationToken cancellationToken = default) => RunCore(() =>
    {
        if (rawOperands.Count > 1)
            _trackSingleLinkFiles = true;
        foreach (byte[] rawBytes in rawOperands)
        {
            if (cancellationToken.IsCancellationRequested || _stoppedEarly)
                break;

            ProcessOperand(rawBytes);
        }
    }, cancellationToken);

    private int RunCore(Action scanAction, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        using DuOutputWriter writer = new(_options, _programName, _stdout, _stderr);
        _writer = writer;
        try
        {
            scanAction();

            if (!_cancellationToken.IsCancellationRequested && !_stoppedEarly && _options.Total)
                writer.EmitLine(_grandTotal, _grandMaxTime, "total"u8);

            writer.Flush();
            return writer.HasError ? 1 : 0;
        }
        finally
        {
            ShutdownWorkers();
            _writer = null;
        }
    }

    private void ShutdownWorkers()
    {
        _stoppedEarly = true;
        FrontierWorkQueue<WorkItem>? queue = _queue;
        if (queue != null)
            queue.Complete();

        Thread[]? toWait;
        lock (_workerSpawnLock)
        {
            toWait = _workerThreads.Count > 0 ? [.. _workerThreads] : null;
            _workerThreads.Clear();
        }

        if (toWait != null)
        {
            foreach (Thread t in toWait)
                t.Join();
        }

        LargeDirBatch[]? leftoverBatches;
        lock (_workerSpawnLock)
        {
            leftoverBatches = _activeLargeBatches.Count > 0 ? [.. _activeLargeBatches] : null;
            _activeLargeBatches.Clear();
        }

        if (leftoverBatches != null)
        {
            foreach (LargeDirBatch batch in leftoverBatches)
                ReleaseBatchResources(batch);
        }
    }

    private bool IsBackpressureActive()
    {
        if (Volatile.Read(ref _unprintedEntries) < BackpressureHighWatermark)
            return false;

        Interlocked.Increment(ref _backpressurePauseCount);
        return true;
    }

    private void AddUnprintedEntries(int delta)
    {
        if (delta <= 0)
            return;

        long updated = Interlocked.Add(ref _unprintedEntries, delta);
        while (true)
        {
            long observedPeak = Volatile.Read(ref _peakUnprintedEntries);
            if (updated <= observedPeak)
                break;

            if (Interlocked.CompareExchange(ref _peakUnprintedEntries, updated, observedPeak) == observedPeak)
                break;
        }
    }

    private void ReleaseUnprintedEntries(int delta)
    {
        if (delta <= 0)
            return;

        long updated = Interlocked.Add(ref _unprintedEntries, -delta);
        if (updated < BackpressureLowWatermark && _queue != null)
            _queue.NotifyBackpressureDrained();
    }

    private void ProcessOperand(ReadOnlySpan<byte> rawOperand)
    {
        if (_stoppedEarly)
            return;

        byte[] nullTermPath = DuAccounting.NormalizeNullTerminatedDisplayPath(rawOperand);
        ReadOnlySpan<byte> displayPath = nullTermPath.AsSpan(0, nullTermPath.Length - 1);
        if (_hasExcludes && _options.Excludes.IsExcluded(displayPath))
            return;

        bool followOperand = _options.SymlinkPolicy is SymlinkPolicy.DereferenceArgs or SymlinkPolicy.DereferenceAll;
        NativeStat stat;
        int errno;
        bool statOk;
        bool isBsdFatalSymlinkLoop;
        byte* pStat = stackalloc byte[NativeIo.StatBufferSize];
        statOk = DuAccounting.TryStatPathWithPolicy(nullTermPath, followOperand, _options, pStat, out stat, out errno, out isBsdFatalSymlinkLoop);

        if (!statOk)
        {
            if (isBsdFatalSymlinkLoop)
            {
                _writer!.WriteBsdTooManySymlinksError(displayPath);
                ShutdownWorkers();
                return;
            }

            _writer!.WritePosixError("cannot access", displayPath, errno);
            return;
        }

        if (DuAccounting.ShouldIgnoreBsdEntry(in stat, _options, nullTermPath, DuAccounting.ExtractBasename(displayPath)))
            return;

        FileIdentity operandId = new(stat.Device, stat.Inode);
        if (!_options.CountLinks &&
            DuAccounting.ShouldTrackOperandIdentity(in stat, _options) &&
            !_seen.TryClaim(operandId, stat.HardLinks))
        {
            return;
        }

        if (stat.Kind != NativeEntryKind.Directory)
        {
            ulong count = DuAccounting.ComputeEntryValue(in stat, _options);
            FileTimestamp entryTime = DuAccounting.ExtractTimestamp(in stat, _options);
            _grandTotal += count;
            if (_showTime)
                _grandMaxTime = FileTimestamp.Max(_grandMaxTime, entryTime);
            if (DuAccounting.ShouldEmitForThreshold(count, _options, isDirectory: false))
                _writer!.EmitLine(count, entryTime, displayPath);
            return;
        }

        _currentRootDevice = stat.Device;
        bool followChild = _options.SymlinkPolicy == SymlinkPolicy.DereferenceAll;
        DirNode rootNode = new(
            parent: null,
            pathOrSegmentNullTerm: nullTermPath,
            dirStat: in stat,
            identity: operandId,
            depth: 0,
            options: _options,
            followChildSymlinks: followChild);

        if (_workersStarted)
            Volatile.Write(ref _frontierTarget, rootNode);
        else
            _frontierTarget = rootNode;
        _pendingInlineSiblings = 0;
        DirWalkResult walked = EmitTreeIterative(rootNode);
        _grandTotal += walked.FullBytes;
        if (_showTime)
            _grandMaxTime = FileTimestamp.Max(_grandMaxTime, walked.FullTime);
    }

    private WorkerScratch EnsureInlineScratch() => _inlineScratch ??= new WorkerScratch();

    private DirWalkResult EmitTreeIterative(DirNode rootNode)
    {
        WorkerScratch inlineScratch = EnsureInlineScratch();
        inlineScratch.ResetAncestors();
        _activeAncestors.Clear();
        _emitStackDepth = 0;
        _emitPathLen = 0;
        if (!PushEmitFrame(rootNode))
            return default;

        DirWalkResult rootResult = default;
        try
        {
            while (_emitStackDepth > 0)
            {
                if (_cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(_cancellationToken);

                ref EmitFrame frame = ref _emitStack![_emitStackDepth - 1];
                DirNode node = frame.Node;
                ScannedEntry[]? entries = node.Entries;
                int entryCount = node.EntryCount;

                if (frame.EntryIndex == 0 && node.SingleSubdir != null && entries == null)
                {
                    frame.EntryIndex = 1;
                    if (!TryDescendSubdir(node, node.SingleSubdir, isSiblingAfterFirst: false, inlineScratch))
                    {
                        if (_stoppedEarly)
                            break;
                    }

                    continue;
                }

                if (frame.EntryIndex < entryCount && entries != null)
                {
                    int idx = frame.EntryIndex;
                    frame.EntryIndex = idx + 1;
                    ref readonly ScannedEntry entry = ref entries[idx];

                    switch (entry.Kind)
                    {
                        case ScannedEntryKind.StatError:
                        {
                            ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                            int len = WriteEmitChildPath(node, name, ref inlineScratch.PathScratch);
                            _writer!.WritePosixError("cannot access", inlineScratch.PathScratch.AsSpan(0, len), entry.SubdirOrErrno);
                            break;
                        }
                        case ScannedEntryKind.ReadError:
                        {
                            ReadOnlySpan<byte> nodePath = GetEmitNodeDisplayPath(node, ref inlineScratch.PathScratch);
                            _writer!.WritePosixError("cannot read directory", nodePath, entry.SubdirOrErrno);
                            break;
                        }
                        case ScannedEntryKind.BsdSymlinkLoop:
                        {
                            ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                            int len = WriteEmitChildPath(node, name, ref inlineScratch.PathScratch);
                            _writer!.WriteBsdTooManySymlinksError(inlineScratch.PathScratch.AsSpan(0, len));
                            ShutdownWorkers();
                            break;
                        }
                        case ScannedEntryKind.BsdSymlinkCycle:
                        {
                            DirNode? cycleTarget = FindAncestorNode(node, entry.Identity);
                            if (cycleTarget != null)
                            {
                                ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                                int len = WriteEmitChildPath(node, name, ref inlineScratch.PathScratch);
                                EmitBsdSymlinkCycleError(inlineScratch.PathScratch.AsSpan(0, len), cycleTarget);
                                ShutdownWorkers();
                            }

                            break;
                        }
                        case ScannedEntryKind.NonDir:
                        {
                            if (!_options.CountLinks && entry.TrackIdentity && !_seen.TryClaim(entry.Identity, entry.HardLinks))
                                break;

                            frame.DirectNonDirBytes += entry.Value;
                            if (_showTime)
                                frame.DirectMaxTime = FileTimestamp.Max(frame.DirectMaxTime, entry.Time);

                            if (_options.All && node.Depth < _maxDepth && DuAccounting.ShouldEmitForThreshold(entry.Value, _options, isDirectory: false))
                            {
                                ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                                int len = WriteEmitChildPath(node, name, ref inlineScratch.PathScratch);
                                _writer!.EmitLine(entry.Value, entry.Time, inlineScratch.PathScratch.AsSpan(0, len));
                            }

                            break;
                        }
                        case ScannedEntryKind.Subdir:
                        {
                            DirNode? child = node.SubdirCount == 1 ? node.SingleSubdir : node.Subdirs![entry.SubdirOrErrno];
                            if (child == null)
                                break;

                            bool isSiblingAfterFirst = node.SubdirCount > 1 && entry.SubdirOrErrno > 0;
                            _ = TryDescendSubdir(node, child, isSiblingAfterFirst, inlineScratch);
                            break;
                        }
                    }

                    if (_stoppedEarly)
                        break;

                    continue;
                }

                // Directory children finished: compute totals, emit line if within max-depth/threshold, and pop frame.
                if (node.HasValidFileId)
                    _activeAncestors.Remove(node.Identity);

                ulong ownBytes = node.OwnBytes;
                ulong ownAndFiles = ownBytes + frame.DirectNonDirBytes;
                ulong reportedTotal = _options.SeparateDirs ? ownAndFiles : ownAndFiles + frame.DirectSubdirReportedBytes;
                ulong fullTotal = ownAndFiles + frame.SubdirFullBytes;

                FileTimestamp reportedTime = (!_options.SeparateDirs && frame.HasSubdirReportedTime)
                    ? FileTimestamp.Max(frame.DirectMaxTime, frame.SubdirReportedMaxTime)
                    : frame.DirectMaxTime;
                FileTimestamp fullTime = frame.HasSubdirFullTime
                    ? FileTimestamp.Max(frame.DirectMaxTime, frame.SubdirFullMaxTime)
                    : frame.DirectMaxTime;

                if (node.Depth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(reportedTotal, _options))
                {
                    ReadOnlySpan<byte> emitPath = GetEmitNodeDisplayPath(node, ref inlineScratch.PathScratch);
                    _writer!.EmitLine(reportedTotal, reportedTime, emitPath);
                }

                DirWalkResult dirResult = new(
                    _options.SeparateDirs ? 0UL : reportedTotal,
                    fullTotal,
                    reportedTime,
                    fullTime);

                _emitPathLen = frame.SavedPathLen;
                ReleaseNodePayload(node);
                _emitStackDepth--;

                if (_emitStackDepth == 0)
                {
                    rootResult = dirResult;
                    break;
                }

                ref EmitFrame parentFrame = ref _emitStack![_emitStackDepth - 1];
                parentFrame.DirectSubdirReportedBytes += dirResult.ReportedToParent;
                parentFrame.SubdirFullBytes += dirResult.FullBytes;
                DuAccounting.AccumulateSubdirTime(
                    in dirResult,
                    _options.SeparateDirs,
                    _showTime,
                    ref parentFrame.HasSubdirReportedTime,
                    ref parentFrame.SubdirReportedMaxTime,
                    ref parentFrame.HasSubdirFullTime,
                    ref parentFrame.SubdirFullMaxTime);
            }
        }
        finally
        {
            if (_emitStackDepth > 0)
            {
                ShutdownWorkers();
                while (_emitStackDepth > 0)
                {
                    ref EmitFrame remaining = ref _emitStack![--_emitStackDepth];
                    if (remaining.Node.HasValidFileId)
                        _activeAncestors.Remove(remaining.Node.Identity);
                    CancelAndReleaseSubtree(remaining.Node);
                }
            }

            _emitPathLen = 0;
        }

        return rootResult;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDescendSubdir(DirNode parent, DirNode child, bool isSiblingAfterFirst, WorkerScratch inlineScratch)
    {
        if (isSiblingAfterFirst && !_workersStarted)
            _pendingInlineSiblings = Math.Max(0, _pendingInlineSiblings - 1);

        if (_workersStarted && child.HasValidFileId && _activeAncestors.Contains(child.Identity))
        {
            if (_options.Dialect == DuDialect.Bsd)
            {
                DirNode? cycleTarget = FindAncestorNode(parent, child.Identity);
                if (cycleTarget != null)
                {
                    ReadOnlySpan<byte> childPath = GetNodeDisplayPath(child, ref inlineScratch.PathScratch);
                    EmitBsdSymlinkCycleError(childPath, cycleTarget);
                    ShutdownWorkers();
                    return false;
                }
            }

            CancelAndReleaseSubtree(child);
            return true;
        }

        if (!_options.CountLinks &&
            _options.Dialect == DuDialect.Gnu &&
            child.HasValidFileId &&
            !_seen.TryClaim(child.Identity, child.HardLinks))
        {
            CancelAndReleaseSubtree(child);
            return true;
        }

        if (_workersStarted)
            Volatile.Write(ref _frontierTarget, child);
        else
            _frontierTarget = child;
        _ = PushEmitFrame(child);
        return !_stoppedEarly;
    }

    private bool PushEmitFrame(DirNode node)
    {
        EnsureNodeCompleted(node);

        if (node.OpenErrno != 0)
        {
            ulong ownBytes = node.OwnBytes;
            FileTimestamp ownTime = node.OwnTime;
            ReadOnlySpan<byte> errPath = GetNodeDisplayPath(node, ref EnsureInlineScratch().PathScratch);
            _writer!.WritePosixError("cannot read directory", errPath, node.OpenErrno);
            if (_options.Dialect == DuDialect.Bsd)
            {
                ReleaseNodePayload(node);
                return _emitStackDepth != 0;
            }

            if (node.Depth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(ownBytes, _options))
                _writer.EmitLine(ownBytes, ownTime, errPath);

            DirWalkResult errResult = new(ownBytes, ownBytes, ownTime, ownTime);
            ReleaseNodePayload(node);

            if (_emitStackDepth == 0)
                return false;

            ref EmitFrame parentFrame = ref _emitStack![_emitStackDepth - 1];
            parentFrame.DirectSubdirReportedBytes += errResult.ReportedToParent;
            parentFrame.SubdirFullBytes += errResult.FullBytes;
            DuAccounting.AccumulateSubdirTime(
                in errResult,
                _options.SeparateDirs,
                _showTime,
                ref parentFrame.HasSubdirReportedTime,
                ref parentFrame.SubdirReportedMaxTime,
                ref parentFrame.HasSubdirFullTime,
                ref parentFrame.SubdirFullMaxTime);
            return true;
        }

        if (node.HasValidFileId)
            _activeAncestors.Add(node.Identity);

        int savedPathLen = _emitPathLen;
        if (_maxDepth > 0 && node.Depth <= _maxDepth)
        {
            AppendNodeToEmitPath(node);
        }

        FileTimestamp dirOwnTime = node.OwnTime;
        FileTimestamp initialMaxTime = node.HasInlineTime
            ? FileTimestamp.Max(dirOwnTime, node.InlineMaxTime)
            : dirOwnTime;

        _emitStack ??= new EmitFrame[32];
        if (_emitStackDepth == _emitStack.Length)
            Array.Resize(ref _emitStack, _emitStack.Length * 2);

        _emitStack[_emitStackDepth++] = new EmitFrame
        {
            Node = node,
            EntryIndex = 0,
            SavedPathLen = savedPathLen,
            DirectNonDirBytes = node.InlineNonDirBytes,
            DirectMaxTime = initialMaxTime
        };

        return true;
    }

    private void EnsureNodeCompleted(DirNode node)
    {
        if (!_workersStarted)
        {
            if (node.State == StatePending)
            {
                node.State = StateClaimed;
                ScanDirectoryNode(node, EnsureInlineScratch(), isWorkerThread: false);
            }

            if (node.State == StateCompleted)
                return;
        }
        else if (Volatile.Read(ref node.State) == StateCompleted)
        {
            return;
        }

        // Workers exist (either previously spawned or started mid-scan via chunk batching).
        WaitForNodeCompletion(node);
    }

    private void WaitForNodeCompletion(DirNode node)
    {
        if (Volatile.Read(ref node.State) == StatePending && _queue != null)
        {
            if (Interlocked.Exchange(ref node.Promoted, 1) == 0)
                _queue.PromoteToHigh(WorkItem.ForDir(node));
        }

        lock (_emitterSync)
        {
            _waitingOnNode = node;
            try
            {
                while (Volatile.Read(ref node.State) != StateCompleted)
                {
                    if (_cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(_cancellationToken);

                    if (Volatile.Read(ref node.State) == StatePending && _queue != null)
                    {
                        if (Interlocked.Exchange(ref node.Promoted, 1) == 0)
                            _queue.PromoteToHigh(WorkItem.ForDir(node));
                    }

                    Monitor.Wait(_emitterSync, 2);
                }
            }
            finally
            {
                _waitingOnNode = null;
            }
        }
    }

    private void WorkerLoop()
    {
        WorkerScratch scratch = new();
        FrontierWorkQueue<WorkItem> queue = _queue!;

        while (!_stoppedEarly && !_cancellationToken.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out WorkItem item))
                break;

            if (item.IsChunk)
            {
                ProcessChunkWork(item.Batch!, item.ChunkIndex, scratch);
                continue;
            }

            DirNode? current = item.Node;
            while (current != null && !_stoppedEarly && !_cancellationToken.IsCancellationRequested)
            {
                int state = Volatile.Read(ref current.State);
                if (state != StatePending)
                {
                    CloseAndClearPreOpenedFd(current);
                    break;
                }

                if (Interlocked.CompareExchange(ref current.State, StateClaimed, StatePending) != StatePending)
                {
                    CloseAndClearPreOpenedFd(current);
                    break;
                }

                current = ScanDirectoryNode(current, scratch, isWorkerThread: true);
            }
        }
    }

    private DirNode? ScanDirectoryNode(DirNode node, WorkerScratch scratch, bool isWorkerThread)
    {
        bool isConcurrent = isWorkerThread || _workersStarted;
        int dirFd;
        NativeDirectory? fallbackDir;
        int openErrno;

        int preOpenedFd;
        if (isConcurrent)
        {
            preOpenedFd = Interlocked.Exchange(ref node.PreOpenedFd, -1);
        }
        else
        {
            preOpenedFd = node.PreOpenedFd;
            if (preOpenedFd >= 0)
                node.PreOpenedFd = -1;
        }

        if (preOpenedFd >= 0)
        {
            dirFd = preOpenedFd;
            fallbackDir = null;
            openErrno = 0;
        }
        else
        {
            bool followOpen = node.FollowChildSymlinks ||
                              (node.Parent == null && _options.SymlinkPolicy is SymlinkPolicy.DereferenceArgs or SymlinkPolicy.DereferenceAll);
            ReadOnlySpan<byte> nullTermPath = GetNodeNullTermPath(node, ref scratch.PathScratch);
            DuAccounting.OpenDirectoryPathSafe(
                nullTermPath,
                out dirFd,
                out fallbackDir,
                out openErrno,
                followOpen);
        }

        if (openErrno != 0)
        {
            node.OpenErrno = openErrno;
            MarkNodeCompleted(node, isConcurrent);
            return null;
        }

        int readErrno;
        int rawCount;
        bool spawnedChunks = false;
        bool hasInlineStats = fallbackDir?.HasInlineStats == true;

        try
        {
            scratch.TempNameArenaLen = 0;
            rawCount = fallbackDir == null
                ? ReadEntriesGetdents64(dirFd, scratch, out readErrno)
                : ReadEntriesFallback(fallbackDir, scratch, out readErrno);

            if (rawCount > DuAccounting.InodeSortThreshold &&
                NativeIo.ShouldSortDirectoryByInode(dirFd, allowOnMacOs: _options.Dialect == DuDialect.Gnu))
                Array.Sort(scratch.TempEntries, 0, rawCount, RawDirEntryInodeComparer.Instance);

            if (rawCount >= LargeDirChunkThreshold &&
                !hasInlineStats &&
                TryEnableWorkersForLargeDir(node, rawCount, scratch))
            {
                if (node.FollowChildSymlinks)
                {
                    if (fallbackDir != null)
                        fallbackDir.Dispose();
                    else if (dirFd >= 0)
                        _ = Libc.close(dirFd);
                    DispatchLargeDirChunks(node, dirFd: -1, fallbackDir: null, rawCount, scratch, readErrno);
                }
                else
                {
                    DispatchLargeDirChunks(node, dirFd, fallbackDir, rawCount, scratch, readErrno);
                }

                spawnedChunks = true;
                return null;
            }

            return FinishSmallDirectoryScan(
                node,
                dirFd,
                fallbackDir,
                hasInlineStats,
                rawCount,
                readErrno,
                scratch,
                isWorkerThread,
                isConcurrent);
        }
        finally
        {
            if (!spawnedChunks)
            {
                if (fallbackDir != null)
                    fallbackDir.Dispose();
                else if (dirFd >= 0)
                    _ = Libc.close(dirFd);
            }
        }
    }

    private DirNode? FinishSmallDirectoryScan(
        DirNode node,
        int dirFd,
        NativeDirectory? fallbackDir,
        bool hasInlineStats,
        int rawCount,
        int readErrno,
        WorkerScratch scratch,
        bool isWorkerThread,
        bool isConcurrent)
    {
        scratch.TempSubdirs.Clear();
        scratch.CompactArenaLen = 0;
        if (scratch.TempScanned.Length < rawCount + 1)
            scratch.TempScanned = new ScannedEntry[Math.Max(scratch.TempScanned.Length * 2, rawCount + 1)];

        int keptCount = 0;
        ulong inlineBytes = 0;
        FileTimestamp inlineTime = default;
        bool hasInlineTime = false;
        bool followChild = node.FollowChildSymlinks;

        bool needParentPath = followChild || (_hasExcludes && !_options.Excludes.MatchBasenameOnly);
        ReadOnlySpan<byte> parentDisplayPath = needParentPath && rawCount > 0
            ? GetNodeParentDisplayPath(node, ref scratch.ParentPathBuffer)
            : [];

        for (int i = 0; i < rawCount; i++)
        {
            if ((i & 255) == 0 && _cancellationToken.IsCancellationRequested)
                break;

            ref readonly RawDirEntry raw = ref scratch.TempEntries[i];
            ReadOnlySpan<byte> nameSpan = scratch.TempNameArena.AsSpan(raw.NameOffset, raw.NameLength);

            if (_hasExcludes && DuAccounting.ShouldExcludeChild(_options, parentDisplayPath, nameSpan, ref scratch.PathScratch))
                continue;

            NativeStat childStat;
            int statErrno;
            bool statOk;
            bool isBsdFatalSymlinkLoop;
            if (hasInlineStats &&
                (!followChild || scratch.TempInlineStats[raw.Order].Kind != NativeEntryKind.Symlink))
            {
                childStat = scratch.TempInlineStats[raw.Order];
                statOk = true;
                statErrno = 0;
                isBsdFatalSymlinkLoop = false;
            }
            else
            {
                fixed (byte* pName = &scratch.TempNameArena[raw.NameOffset])
                fixed (byte* pStat = scratch.StatBuffer)
                {
                    statOk = DuAccounting.TryStatChildEntry(
                        dirFd,
                        parentDisplayPath,
                        nameSpan,
                        pName,
                        followChild,
                        _options,
                        pStat,
                        ref scratch.PathScratch,
                        out childStat,
                        out statErrno,
                        out isBsdFatalSymlinkLoop);
                }
            }

            if (!statOk)
            {
                int arenaOff = AppendToCompactArena(scratch, nameSpan);
                scratch.TempScanned[keptCount++] = new ScannedEntry
                {
                    Kind = isBsdFatalSymlinkLoop ? ScannedEntryKind.BsdSymlinkLoop : ScannedEntryKind.StatError,
                    SubdirOrErrno = statErrno,
                    NameOffset = arenaOff,
                    NameLength = nameSpan.Length
                };
                continue;
            }

            if (!TryClassifyChild(node, nameSpan, in childStat, scratch, isConcurrent, out DirNode? childNode, out FileIdentity childId, out bool isBsdCycle))
            {
                if (isBsdCycle)
                {
                    int arenaOff = AppendToCompactArena(scratch, nameSpan);
                    scratch.TempScanned[keptCount++] = new ScannedEntry
                    {
                        Kind = ScannedEntryKind.BsdSymlinkCycle,
                        Identity = childId,
                        NameOffset = arenaOff,
                        NameLength = nameSpan.Length
                    };
                }

                continue;
            }

            if (childNode != null)
            {
                int subdirIndex = scratch.TempSubdirs.Count;
                scratch.TempSubdirs.Add(childNode);

                scratch.TempScanned[keptCount++] = new ScannedEntry
                {
                    Kind = ScannedEntryKind.Subdir,
                    SubdirOrErrno = subdirIndex,
                    Identity = childId,
                    TrackIdentity = true
                };
                continue;
            }

            ulong entryValue = DuAccounting.ComputeEntryValue(in childStat, _options);
            FileTimestamp entryTime = DuAccounting.ExtractTimestamp(in childStat, _options);
            bool trackIdentity = DuAccounting.ShouldTrackChildIdentity(in childStat, _options, followChild, _trackSingleLinkFiles);

            if (!_options.All && (_options.CountLinks || !trackIdentity))
            {
                inlineBytes += entryValue;
                if (_showTime)
                {
                    inlineTime = hasInlineTime ? FileTimestamp.Max(inlineTime, entryTime) : entryTime;
                    hasInlineTime = true;
                }

                continue;
            }

            int nameOff = _options.All ? AppendToCompactArena(scratch, nameSpan) : 0;
            scratch.TempScanned[keptCount++] = new ScannedEntry
            {
                Kind = ScannedEntryKind.NonDir,
                Value = entryValue,
                HardLinks = trackIdentity ? childStat.HardLinks : 0,
                Identity = childId,
                Time = entryTime,
                NameOffset = nameOff,
                NameLength = _options.All ? nameSpan.Length : 0,
                TrackIdentity = trackIdentity
            };
        }

        if (readErrno != 0)
        {
            scratch.TempScanned[keptCount++] = new ScannedEntry
            {
                Kind = ScannedEntryKind.ReadError,
                SubdirOrErrno = readErrno
            };
        }

        PopulateNodeResults(
            node,
            inlineBytes,
            inlineTime,
            hasInlineTime,
            scratch.TempScanned.AsSpan(0, keptCount),
            scratch.CompactArena.AsSpan(0, scratch.CompactArenaLen),
            scratch.TempSubdirs,
            isConcurrent);

        // Pre-open Subdirs[0] via openat while parent dirFd is still open for fast FD-relative spine traversal.
        DirNode? inlineContinuation = null;
        if (node.SubdirCount > 0 && fallbackDir == null && !followChild && NativeIo.UsesGetdents64)
        {
            DirNode firstChild = node.SubdirCount == 1 ? node.SingleSubdir! : node.Subdirs![0]!;
            if (!firstChild.SkipDescent)
            {
                if (isWorkerThread)
                {
                    if (!IsBackpressureActive() || IsFrontierOrNear(node))
                    {
                        OpenPreOpenedChild(dirFd, firstChild);
                        inlineContinuation = firstChild;
                    }
                }
                else if (node.SubdirCount == 1 ||
                         !_canEverFanOut ||
                         _pendingInlineSiblings + (node.SubdirCount - 1) < FanOutSiblingThreshold)
                {
                    OpenPreOpenedChild(dirFd, firstChild);
                }
            }
        }

        if (isWorkerThread)
        {
            EnqueueRemainingSubdirs(node, startIndex: inlineContinuation != null ? 1 : 0);
            MarkNodeCompleted(node, isConcurrent: true);
            return inlineContinuation;
        }

        MarkNodeCompleted(node, isConcurrent);

        if (node.SubdirCount > 1)
        {
            _pendingInlineSiblings += node.SubdirCount - 1;
            if (_canEverFanOut && _pendingInlineSiblings >= FanOutSiblingThreshold)
            {
                if (TryActivateWorkersFromInline(node, scratch))
                    return null;
            }
        }

        return null;
    }

    private void PopulateNodeResults(
        DirNode node,
        ulong inlineBytes,
        FileTimestamp inlineTime,
        bool hasInlineTime,
        ReadOnlySpan<ScannedEntry> entries,
        ReadOnlySpan<byte> arena,
        List<DirNode> subdirs,
        bool isConcurrent)
    {
        node.InlineNonDirBytes = inlineBytes;
        node.InlineMaxTime = inlineTime;
        node.HasInlineTime = hasInlineTime;
        node.EntryCount = entries.Length;

        if (subdirs.Count == 1)
        {
            node.SingleSubdir = subdirs[0];
            node.SubdirCount = 1;
            if (entries.Length == 1)
            {
                // Single-subdir fast path: avoid renting a 1-element ScannedEntry[] from ArrayPool.
                if (arena.Length > 0)
                {
                    byte[] rentedArenaOnly = ArrayPool<byte>.Shared.Rent(arena.Length);
                    arena.CopyTo(rentedArenaOnly);
                    node.NameArena = rentedArenaOnly;
                }

                if (isConcurrent)
                {
                    node.UnprintedWeight = 2;
                    AddUnprintedEntries(2);
                }

                return;
            }
        }
        else if (subdirs.Count > 1)
        {
            node.Subdirs = subdirs.ToArray();
            node.SubdirCount = subdirs.Count;
        }

        if (entries.Length > 0)
        {
            ScannedEntry[] rentedEntries = ArrayPool<ScannedEntry>.Shared.Rent(entries.Length);
            entries.CopyTo(rentedEntries);
            node.Entries = rentedEntries;
        }

        if (arena.Length > 0)
        {
            byte[] rentedArena = ArrayPool<byte>.Shared.Rent(arena.Length);
            arena.CopyTo(rentedArena);
            node.NameArena = rentedArena;
        }

        if (isConcurrent)
        {
            int weight = entries.Length + 1;
            node.UnprintedWeight = weight;
            AddUnprintedEntries(weight);
        }
    }

    private void MarkNodeCompleted(DirNode node, bool isConcurrent)
    {
        if (!isConcurrent)
        {
            node.State = StateCompleted;
            return;
        }

        Volatile.Write(ref node.State, StateCompleted);
        Thread.MemoryBarrier();
        if (Volatile.Read(ref _waitingOnNode) == node)
        {
            lock (_emitterSync)
            {
                Monitor.PulseAll(_emitterSync);
            }
        }
    }

    private void DispatchLargeDirChunks(
        DirNode node,
        int dirFd,
        NativeDirectory? fallbackDir,
        int rawCount,
        WorkerScratch scratch,
        int finalReadErrno)
    {
        Interlocked.Increment(ref _chunkStatBatches);

        int targetSlices = Math.Clamp(_maxWorkerBudget * 2, 4, 32);
        int chunkSize = Math.Max(256, (rawCount + targetSlices - 1) / targetSlices);
        int chunkCount = (rawCount + chunkSize - 1) / chunkSize;
        ChunkSpec[] chunks = new ChunkSpec[chunkCount];
        for (int c = 0; c < chunkCount; c++)
        {
            int start = c * chunkSize;
            int end = Math.Min(rawCount, start + chunkSize);
            chunks[c] = new ChunkSpec(start, end);
        }

        RawDirEntry[] rawEntries = ArrayPool<RawDirEntry>.Shared.Rent(rawCount);
        scratch.TempEntries.AsSpan(0, rawCount).CopyTo(rawEntries);

        byte[] rawNames = ArrayPool<byte>.Shared.Rent(Math.Max(1, scratch.TempNameArenaLen));
        scratch.TempNameArena.AsSpan(0, scratch.TempNameArenaLen).CopyTo(rawNames);

        LargeDirBatch batch = new(
            node,
            dirFd,
            fallbackDir,
            rawEntries,
            rawCount,
            rawNames,
            chunks,
            finalReadErrno);

        lock (_workerSpawnLock)
        {
            _activeLargeBatches.Add(batch);
        }

        // Push chunks 1..N-1 onto High tier so already-opened dirFd completes without backpressure stalls.
        for (int c = chunkCount - 1; c >= 1; c--)
            _queue!.Enqueue(WorkItem.ForChunk(batch, c), ScanTier.High);

        ProcessChunkWork(batch, 0, scratch);
    }

    private void ProcessChunkWork(LargeDirBatch batch, int chunkIndex, WorkerScratch scratch)
    {
        ChunkSpec spec = batch.Chunks[chunkIndex];
        DirNode node = batch.Node;
        bool followChild = node.FollowChildSymlinks;
        ulong inlineBytes = 0;
        FileTimestamp inlineTime = default;
        bool hasInlineTime = false;
        List<ScannedEntry>? kept = null;
        List<DirNode>? subdirs = null;
        byte[]? chunkArena = _options.All ? ArrayPool<byte>.Shared.Rent(4096) : null;
        int chunkArenaLen = 0;

        bool needParentPath = followChild || (_hasExcludes && !_options.Excludes.MatchBasenameOnly);
        ReadOnlySpan<byte> parentDisplayPath = needParentPath && spec.Start < spec.End
            ? GetNodeParentDisplayPath(node, ref scratch.ParentPathBuffer)
            : [];

        for (int i = spec.Start; i < spec.End; i++)
        {
            if ((i & 255) == 0 && _cancellationToken.IsCancellationRequested)
                break;

            ref readonly RawDirEntry raw = ref batch.RawEntries[i];
            ReadOnlySpan<byte> nameSpan = batch.RawNameArena.AsSpan(raw.NameOffset, raw.NameLength);

            if (_hasExcludes && DuAccounting.ShouldExcludeChild(_options, parentDisplayPath, nameSpan, ref scratch.PathScratch))
                continue;

            NativeStat childStat;
            int statErrno;
            bool statOk;
            bool isBsdFatalSymlinkLoop;
            fixed (byte* pName = &batch.RawNameArena[raw.NameOffset])
            fixed (byte* pStat = scratch.StatBuffer)
            {
                statOk = DuAccounting.TryStatChildEntry(
                    batch.DirFd,
                    parentDisplayPath,
                    nameSpan,
                    pName,
                    followChild,
                    _options,
                    pStat,
                    ref scratch.PathScratch,
                    out childStat,
                    out statErrno,
                    out isBsdFatalSymlinkLoop);
            }

            if (!statOk)
            {
                int off = AppendToRentedArena(ref chunkArena, ref chunkArenaLen, nameSpan);
                (kept ??= []).Add(new ScannedEntry
                {
                    Kind = isBsdFatalSymlinkLoop ? ScannedEntryKind.BsdSymlinkLoop : ScannedEntryKind.StatError,
                    SubdirOrErrno = statErrno,
                    NameOffset = off,
                    NameLength = nameSpan.Length
                });
                continue;
            }

            if (!TryClassifyChild(node, nameSpan, in childStat, scratch, isConcurrent: true, out DirNode? childNode, out FileIdentity childId, out bool isBsdCycle))
            {
                if (isBsdCycle)
                {
                    int off = AppendToRentedArena(ref chunkArena, ref chunkArenaLen, nameSpan);
                    (kept ??= []).Add(new ScannedEntry
                    {
                        Kind = ScannedEntryKind.BsdSymlinkCycle,
                        Identity = childId,
                        NameOffset = off,
                        NameLength = nameSpan.Length
                    });
                }

                continue;
            }

            if (childNode != null)
            {
                subdirs ??= [];
                int localIdx = subdirs.Count;
                subdirs.Add(childNode);

                (kept ??= []).Add(new ScannedEntry
                {
                    Kind = ScannedEntryKind.Subdir,
                    SubdirOrErrno = localIdx,
                    Identity = childId,
                    TrackIdentity = true
                });
                continue;
            }

            ulong entryValue = DuAccounting.ComputeEntryValue(in childStat, _options);
            FileTimestamp entryTime = DuAccounting.ExtractTimestamp(in childStat, _options);
            bool trackIdentity = DuAccounting.ShouldTrackChildIdentity(in childStat, _options, followChild, _trackSingleLinkFiles);

            if (!_options.All && (_options.CountLinks || !trackIdentity))
            {
                inlineBytes += entryValue;
                if (_showTime)
                {
                    inlineTime = hasInlineTime ? FileTimestamp.Max(inlineTime, entryTime) : entryTime;
                    hasInlineTime = true;
                }

                continue;
            }

            int nameOff = _options.All ? AppendToRentedArena(ref chunkArena!, ref chunkArenaLen, nameSpan) : 0;
            (kept ??= []).Add(new ScannedEntry
            {
                Kind = ScannedEntryKind.NonDir,
                Value = entryValue,
                HardLinks = trackIdentity ? childStat.HardLinks : 0,
                Identity = childId,
                Time = entryTime,
                NameOffset = nameOff,
                NameLength = _options.All ? nameSpan.Length : 0,
                TrackIdentity = trackIdentity
            });
        }

        spec.InlineNonDirBytes = inlineBytes;
        spec.InlineMaxTime = inlineTime;
        spec.HasInlineTime = hasInlineTime;
        spec.KeptEntries = kept;
        spec.Subdirs = subdirs;
        if (chunkArenaLen > 0)
        {
            spec.Arena = chunkArena;
            spec.ArenaLen = chunkArenaLen;
        }
        else if (chunkArena != null)
        {
            ArrayPool<byte>.Shared.Return(chunkArena);
        }

        if (Interlocked.Decrement(ref batch.RemainingChunks) == 0)
            FinalizeLargeDirBatch(batch);
    }

    private void ReleaseBatchResources(LargeDirBatch batch)
    {
        if (Interlocked.Exchange(ref batch.ResourcesReleased, 1) != 0)
            return;

        if (batch.FallbackDir != null)
            batch.FallbackDir.Dispose();
        else if (batch.DirFd >= 0)
            _ = Libc.close(batch.DirFd);

        ArrayPool<RawDirEntry>.Shared.Return(batch.RawEntries);
        ArrayPool<byte>.Shared.Return(batch.RawNameArena);
        foreach (ChunkSpec c in batch.Chunks)
        {
            if (c.Arena != null)
            {
                ArrayPool<byte>.Shared.Return(c.Arena);
                c.Arena = null;
            }
        }
    }

    private void FinalizeLargeDirBatch(LargeDirBatch batch)
    {
        lock (_workerSpawnLock)
        {
            _activeLargeBatches.Remove(batch);
        }

        if (Interlocked.Exchange(ref batch.ResourcesReleased, 1) == 0)
        {
            if (batch.FallbackDir != null)
                batch.FallbackDir.Dispose();
            else if (batch.DirFd >= 0)
                _ = Libc.close(batch.DirFd);
            ArrayPool<RawDirEntry>.Shared.Return(batch.RawEntries);
            ArrayPool<byte>.Shared.Return(batch.RawNameArena);
        }

        DirNode node = batch.Node;
        int totalEntries = batch.FinalReadErrno != 0 ? 1 : 0;
        int totalArena = 0;
        int totalSubdirs = 0;
        ulong inlineBytes = 0;
        FileTimestamp inlineTime = default;
        bool hasInlineTime = false;

        foreach (ChunkSpec c in batch.Chunks)
        {
            inlineBytes += c.InlineNonDirBytes;
            if (c.HasInlineTime)
            {
                inlineTime = hasInlineTime ? FileTimestamp.Max(inlineTime, c.InlineMaxTime) : c.InlineMaxTime;
                hasInlineTime = true;
            }

            if (c.KeptEntries != null)
                totalEntries += c.KeptEntries.Count;
            if (c.Subdirs != null)
                totalSubdirs += c.Subdirs.Count;
            totalArena += c.ArenaLen;
        }

        ScannedEntry[]? finalEntries = totalEntries > 0 ? ArrayPool<ScannedEntry>.Shared.Rent(totalEntries) : null;
        byte[]? finalArena = totalArena > 0 ? ArrayPool<byte>.Shared.Rent(totalArena) : null;
        DirNode?[]? finalSubdirs = totalSubdirs > 1 ? new DirNode?[totalSubdirs] : null;
        DirNode? singleSubdir = null;

        int entryPos = 0;
        int arenaPos = 0;
        int subdirOffset = 0;

        foreach (ChunkSpec c in batch.Chunks)
        {
            if (c.Arena != null && c.ArenaLen > 0)
            {
                c.Arena.AsSpan(0, c.ArenaLen).CopyTo(finalArena.AsSpan(arenaPos));
                ArrayPool<byte>.Shared.Return(c.Arena);
                c.Arena = null;
            }

            if (c.KeptEntries != null)
            {
                foreach (ScannedEntry e in c.KeptEntries)
                {
                    ScannedEntry adjusted = e;
                    if (adjusted.Kind == ScannedEntryKind.Subdir)
                        adjusted.SubdirOrErrno += subdirOffset;
                    else if (adjusted.NameLength > 0)
                        adjusted.NameOffset += arenaPos;

                    finalEntries![entryPos++] = adjusted;
                }
            }

            if (c.Subdirs != null)
            {
                foreach (DirNode s in c.Subdirs)
                {
                    if (totalSubdirs == 1)
                        singleSubdir = s;
                    else
                        finalSubdirs![subdirOffset] = s;
                    subdirOffset++;
                }
            }

            arenaPos += c.ArenaLen;
        }

        if (batch.FinalReadErrno != 0)
        {
            finalEntries![entryPos++] = new ScannedEntry
            {
                Kind = ScannedEntryKind.ReadError,
                SubdirOrErrno = batch.FinalReadErrno
            };
        }

        node.InlineNonDirBytes = inlineBytes;
        node.InlineMaxTime = inlineTime;
        node.HasInlineTime = hasInlineTime;
        node.Entries = finalEntries;
        node.EntryCount = entryPos;
        node.NameArena = finalArena;
        node.SingleSubdir = singleSubdir;
        node.Subdirs = finalSubdirs;
        node.SubdirCount = subdirOffset;
        node.UnprintedWeight = entryPos + 1;
        AddUnprintedEntries(node.UnprintedWeight);

        EnqueueRemainingSubdirs(node, startIndex: 0);
        MarkNodeCompleted(node, isConcurrent: true);
    }

    private void EnqueueRemainingSubdirs(DirNode parent, int startIndex)
    {
        if (_stoppedEarly || _cancellationToken.IsCancellationRequested || _queue == null)
            return;

        if (parent.SubdirCount == 1 && parent.SingleSubdir != null)
        {
            if (startIndex == 0 && Volatile.Read(ref parent.SingleSubdir.State) == StatePending)
            {
                bool onFrontierSingle = IsFrontierOrNear(parent);
                int priority = onFrontierSingle ? ScanTier.High : ScanTier.Medium;
                _queue.Enqueue(WorkItem.ForDir(parent.SingleSubdir), priority);
            }

            return;
        }

        DirNode?[]? subdirs = parent.Subdirs;
        int subdirCount = parent.SubdirCount;
        if (subdirs == null || subdirCount <= startIndex)
            return;

        DirNode? frontier = Volatile.Read(ref _frontierTarget);
        bool onFrontier = frontier != null && IsFrontierOrNear(parent, frontier);
        ulong frontierDepth = frontier?.Depth ?? parent.Depth;

        int limit = Math.Min(subdirCount, subdirs.Length);
        for (int i = limit - 1; i >= startIndex; i--)
        {
            if (_stoppedEarly || _cancellationToken.IsCancellationRequested)
                return;

            DirNode? child = subdirs[i];
            if (child == null || Volatile.Read(ref child.State) != StatePending)
                continue;

            int priority = ComputeSubdirPriority(child, i, onFrontier, frontierDepth);
            _queue.Enqueue(WorkItem.ForDir(child), priority);
        }

        int queueCount = _queue.Count;
        if (_spawnedWorkers < _maxWorkerBudget && queueCount > _spawnedWorkers * 2)
            EnsureWorkersSpawned(Math.Min(_maxWorkerBudget, _spawnedWorkers + Math.Max(2, queueCount / 2)));
    }

    private int ComputeSubdirPriority(DirNode child, int siblingIndex, bool parentOnFrontier, ulong frontierDepth)
    {
        if (parentOnFrontier && siblingIndex == 0)
            return ScanTier.High;

        if (parentOnFrontier || (siblingIndex <= 2 && child.Depth <= frontierDepth + 2))
            return ScanTier.Medium;

        return ScanTier.Low;
    }

    private bool TryEnableWorkersForLargeDir(DirNode node, int rawCount, WorkerScratch scratch)
    {
        if (!_canEverFanOut)
            return false;

        if (!_heuristicsResolved)
            ResolveWorkerBudget(Encoding.UTF8.GetString(GetNodeDisplayPath(node, ref scratch.PathScratch)));

        if (!_canEverFanOut)
            return false;

        int desired = Math.Min(_maxWorkerBudget, Math.Max(2, rawCount / LargeDirChunkThreshold));
        EnsureQueueAndWorkersStarted(desired);
        return _workersStarted;
    }

    private bool TryActivateWorkersFromInline(DirNode latestNode, WorkerScratch scratch)
    {
        if (!_heuristicsResolved)
            ResolveWorkerBudget(Encoding.UTF8.GetString(GetNodeDisplayPath(latestNode, ref scratch.PathScratch)));

        if (!_canEverFanOut)
            return false;

        int initialDemand = Math.Min(_maxWorkerBudget, Math.Max(2, Math.Min(4, _pendingInlineSiblings + latestNode.SubdirCount)));
        EnsureQueueAndWorkersStarted(initialDemand);

        // Push all unvisited pending sibling subdirs from active emitter stack frames into queue (outermost first so innermost/leftmost pops first).
        for (int f = 0; f < _emitStackDepth; f++)
        {
            ref EmitFrame frame = ref _emitStack![f];
            DirNode frameNode = frame.Node;
            if (frameNode.SingleSubdir != null && frameNode.Entries == null)
            {
                if (frame.EntryIndex == 0 && Volatile.Read(ref frameNode.SingleSubdir.State) == StatePending)
                    _queue!.Enqueue(WorkItem.ForDir(frameNode.SingleSubdir), ScanTier.Medium);
                continue;
            }

            if (frameNode.Entries == null || (frameNode.Subdirs == null && frameNode.SingleSubdir == null))
                continue;

            for (int e = frameNode.EntryCount - 1; e >= frame.EntryIndex; e--)
            {
                ref readonly ScannedEntry se = ref frameNode.Entries[e];
                if (se.Kind != ScannedEntryKind.Subdir)
                    continue;

                DirNode? pendingSub = frameNode.SubdirCount == 1
                    ? frameNode.SingleSubdir
                    : frameNode.Subdirs![se.SubdirOrErrno];
                if (pendingSub != null && Volatile.Read(ref pendingSub.State) == StatePending)
                {
                    int tier = se.SubdirOrErrno <= 2 ? ScanTier.Medium : ScanTier.Low;
                    _queue!.Enqueue(WorkItem.ForDir(pendingSub), tier);
                }
            }
        }

        // Enqueue latestNode's own discovered subdirs with Subdirs[0] in High tier.
        if (latestNode.SubdirCount == 1 && latestNode.SingleSubdir != null)
        {
            if (Volatile.Read(ref latestNode.SingleSubdir.State) == StatePending)
                _queue!.Enqueue(WorkItem.ForDir(latestNode.SingleSubdir), ScanTier.High);
        }
        else if (latestNode.SubdirCount > 1 && latestNode.Subdirs != null)
        {
            for (int i = latestNode.SubdirCount - 1; i >= 0; i--)
            {
                DirNode? sub = latestNode.Subdirs[i];
                if (sub != null && Volatile.Read(ref sub.State) == StatePending)
                {
                    int tier = i == 0 ? ScanTier.High : (i <= 2 ? ScanTier.Medium : ScanTier.Low);
                    _queue!.Enqueue(WorkItem.ForDir(sub), tier);
                }
            }
        }

        return true;
    }

    private void ResolveWorkerBudget(string samplePath)
    {
        if (_heuristicsResolved)
            return;

        int budget = MountHeuristics.ResolveWorkerBudget(_requestedThreads, samplePath, out bool isNetwork, out _);
        _maxWorkerBudget = _requestedThreads.HasValue || isNetwork
            ? budget
            : Math.Min(budget, 16);
        _canEverFanOut = _maxWorkerBudget > 1;
        _heuristicsResolved = true;
    }

    private void EnsureQueueAndWorkersStarted(int targetWorkers)
    {
        if (_queue == null || !_workersStarted)
        {
            lock (_workerSpawnLock)
            {
                _queue ??= new FrontierWorkQueue<WorkItem>(IsBackpressureActive);
                _workersStarted = true;
            }
        }

        EnsureWorkersSpawned(targetWorkers);
    }

    private void EnsureWorkersSpawned(int targetWorkers)
    {
        int capped = Math.Min(_maxWorkerBudget, Math.Max(1, targetWorkers));
        if (_spawnedWorkers >= capped)
            return;

        lock (_workerSpawnLock)
        {
            if (_spawnedWorkers >= capped)
                return;

            while (_spawnedWorkers < capped)
            {
                _spawnedWorkers++;
                Thread thread = new(WorkerLoop)
                {
                    IsBackground = true
                };
                _workerThreads.Add(thread);
                thread.Start();
            }
        }
    }

    private bool IsFrontierOrNear(DirNode node, DirNode? frontierOverride = null)
    {
        DirNode? frontier = frontierOverride ?? Volatile.Read(ref _frontierTarget);
        if (frontier == null)
            return true;

        if (node == frontier)
            return true;

        DirNode? cur = node;
        while (cur != null && cur.Depth >= frontier.Depth)
        {
            if (cur == frontier)
                return true;
            cur = cur.Parent;
        }

        cur = frontier;
        while (cur != null && cur.Depth >= node.Depth)
        {
            if (cur == node)
                return true;
            cur = cur.Parent;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryClassifyChild(
        DirNode parent,
        ReadOnlySpan<byte> nameSpan,
        in NativeStat childStat,
        WorkerScratch scratch,
        bool isConcurrent,
        out DirNode? subdirNode,
        out FileIdentity childId,
        out bool isBsdCycle)
    {
        subdirNode = null;
        isBsdCycle = false;
        childId = new FileIdentity(childStat.Device, childStat.Inode);

        if (_options.Dialect == DuDialect.Bsd)
        {
            if (_options.IgnoreNoDump && childStat.HasNoDumpFlag)
                return false;

            if (childStat.Kind == NativeEntryKind.Directory && NativeIo.IsMacOS && nameSpan.SequenceEqual("fd"u8))
            {
                int len = WriteNodeChildPathInto(parent, nameSpan, ref scratch.PathScratch, appendNul: true);
                if (NativeIo.IsMacOsDevFdDirectory(scratch.PathScratch.AsSpan(0, len + 1), nameSpan))
                    return false;
            }
        }

        bool crossDevice = _options.OneFileSystem && childStat.Device != _currentRootDevice;
        if (crossDevice && _options.Dialect != DuDialect.Bsd)
            return false;

        if (childStat.Kind == NativeEntryKind.Directory)
        {
            if (IsAncestorCycle(parent, in childStat, childId, scratch, isConcurrent))
            {
                isBsdCycle = _options.Dialect == DuDialect.Bsd;
                return false;
            }

            byte[] segNullTerm = new byte[nameSpan.Length + 1];
            nameSpan.CopyTo(segNullTerm);
            segNullTerm[nameSpan.Length] = 0;

            subdirNode = new DirNode(
                parent: parent,
                pathOrSegmentNullTerm: segNullTerm,
                dirStat: in childStat,
                identity: childId,
                depth: parent.Depth + 1,
                options: _options,
                followChildSymlinks: parent.FollowChildSymlinks,
                skipDescent: crossDevice);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAncestorCycle(
        DirNode parent,
        in NativeStat childStat,
        FileIdentity childId,
        WorkerScratch scratch,
        bool isConcurrent)
    {
        if (!childStat.HasValidFileId)
            return false;

        if (!isConcurrent)
            return (parent.HasValidFileId && childId == parent.Identity) || _activeAncestors.Contains(childId);

        if (parent.Depth <= 32)
            return FindAncestorNode(parent, childId) != null;

        EnsureWorkerAncestorsAligned(parent, scratch);
        return scratch.AncestorSet!.Contains(childId);
    }

    private static void EnsureWorkerAncestorsAligned(DirNode parent, WorkerScratch scratch)
    {
        if (scratch.AncestorTip == parent)
            return;

        List<DirNode> path = scratch.AncestorPath ??= [];
        HashSet<FileIdentity> set = scratch.AncestorSet ??= [];

        // Fast path 1: descending directly into a child of the current tip.
        if (path.Count > 0 && parent.Parent == path[^1])
        {
            path.Add(parent);
            if (parent.HasValidFileId)
                set.Add(parent.Identity);
            scratch.AncestorTip = parent;
            return;
        }

        // Fast path 2: moving horizontally to a sibling of the current tip.
        if (path.Count >= 2 && parent.Parent == path[^2])
        {
            DirNode popped = path[^1];
            path.RemoveAt(path.Count - 1);
            if (popped.HasValidFileId)
                set.Remove(popped.Identity);
            path.Add(parent);
            if (parent.HasValidFileId)
                set.Add(parent.Identity);
            scratch.AncestorTip = parent;
            return;
        }

        // General LCA alignment: walk up from parent until meeting current path.
        List<DirNode> missing = scratch.TempAlignNodes ??= [];
        missing.Clear();
        DirNode? cur = parent;
        while (cur != null)
        {
            while (path.Count > 0 && path[^1].Depth > cur.Depth)
            {
                DirNode removed = path[^1];
                path.RemoveAt(path.Count - 1);
                if (removed.HasValidFileId)
                    set.Remove(removed.Identity);
            }

            if (path.Count > 0 && path[^1] == cur)
                break;

            if (path.Count > 0 && path[^1].Depth == cur.Depth)
            {
                DirNode removed = path[^1];
                path.RemoveAt(path.Count - 1);
                if (removed.HasValidFileId)
                    set.Remove(removed.Identity);
            }

            missing.Add(cur);
            cur = cur.Parent;
        }

        for (int i = missing.Count - 1; i >= 0; i--)
        {
            DirNode added = missing[i];
            path.Add(added);
            if (added.HasValidFileId)
                set.Add(added.Identity);
        }

        missing.Clear();
        scratch.AncestorTip = parent;
    }

    private static DirNode? FindAncestorNode(DirNode current, FileIdentity candidate)
    {
        if (candidate.Inode == 0 || candidate.Inode == ulong.MaxValue)
            return null;

        DirNode? cursor = current;
        while (cursor != null)
        {
            if (cursor.HasValidFileId && cursor.Identity == candidate)
                return cursor;
            cursor = cursor.Parent;
        }

        return null;
    }

    private static void FillNodeDisplayPath(DirNode node, byte[] destination)
    {
        int pos = node.FullDisplayLength;
        DirNode? cur = node;
        while (cur != null)
        {
            ReadOnlySpan<byte> seg = cur.SegmentOrRootDisplaySpan;
            pos -= seg.Length;
            seg.CopyTo(destination.AsSpan(pos));
            DirNode? p = cur.Parent;
            if (p != null && p.NeedsChildSeparator)
                destination[--pos] = DuAccounting.DefaultSeparator;
            cur = p;
        }
    }

    private static ReadOnlySpan<byte> GetNodeDisplayPath(DirNode node, ref byte[] scratch)
    {
        if (node.Parent == null)
            return node.SegmentOrRootDisplaySpan;

        int len = node.FullDisplayLength;
        if (len > scratch.Length)
            DuAccounting.GrowByteArray(ref scratch, len);
        FillNodeDisplayPath(node, scratch);
        return scratch.AsSpan(0, len);
    }

    private static ReadOnlySpan<byte> GetNodeNullTermPath(DirNode node, ref byte[] scratch)
    {
        if (node.Parent == null)
            return node.RawPathOrSegmentNullTerm;

        int len = node.FullDisplayLength;
        if (len + 1 > scratch.Length)
            DuAccounting.GrowByteArray(ref scratch, len + 1);
        FillNodeDisplayPath(node, scratch);
        scratch[len] = 0;
        return scratch.AsSpan(0, len + 1);
    }

    private static ReadOnlySpan<byte> GetNodeParentDisplayPath(DirNode node, ref byte[]? parentPathBuffer)
    {
        if (node.Parent == null)
            return node.SegmentOrRootDisplaySpan;

        int len = node.FullDisplayLength;
        if (parentPathBuffer == null)
            parentPathBuffer = new byte[Math.Max(1024, len)];
        else if (len > parentPathBuffer.Length)
            DuAccounting.GrowByteArray(ref parentPathBuffer, len);

        FillNodeDisplayPath(node, parentPathBuffer);
        return parentPathBuffer.AsSpan(0, len);
    }

    private static int WriteNodeChildPathInto(
        DirNode parent,
        ReadOnlySpan<byte> childName,
        ref byte[] scratch,
        bool appendNul = false)
    {
        int parentLen = parent.FullDisplayLength;
        bool needsSlash = parent.NeedsChildSeparator;
        int displayLen = parentLen + (needsSlash ? 1 : 0) + childName.Length;
        int totalLen = displayLen + (appendNul ? 1 : 0);
        if (totalLen > scratch.Length)
            DuAccounting.GrowByteArray(ref scratch, totalLen);

        FillNodeDisplayPath(parent, scratch);
        int pos = parentLen;
        if (needsSlash)
            scratch[pos++] = DuAccounting.DefaultSeparator;
        childName.CopyTo(scratch.AsSpan(pos));
        if (appendNul)
            scratch[displayLen] = 0;
        return displayLen;
    }

    private void AppendNodeToEmitPath(DirNode node)
    {
        if (node.Parent == null)
        {
            int rootLen = node.FullDisplayLength;
            if (_emitPathBuf == null)
                _emitPathBuf = new byte[Math.Max(1024, rootLen)];
            else if (rootLen > _emitPathBuf.Length)
                DuAccounting.GrowByteArray(ref _emitPathBuf, rootLen);

            node.SegmentOrRootDisplaySpan.CopyTo(_emitPathBuf);
            _emitPathLen = rootLen;
            return;
        }

        bool needsSlash = node.Parent.NeedsChildSeparator;
        ReadOnlySpan<byte> seg = node.SegmentOrRootDisplaySpan;
        int newLen = _emitPathLen + (needsSlash ? 1 : 0) + seg.Length;
        if (_emitPathBuf == null)
            _emitPathBuf = new byte[Math.Max(1024, newLen)];
        else if (newLen > _emitPathBuf.Length)
            DuAccounting.GrowByteArray(ref _emitPathBuf, newLen);

        if (needsSlash)
            _emitPathBuf[_emitPathLen++] = DuAccounting.DefaultSeparator;
        seg.CopyTo(_emitPathBuf.AsSpan(_emitPathLen));
        _emitPathLen = newLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetEmitNodeDisplayPath(DirNode node, ref byte[] scratch)
    {
        if (node.Parent == null)
            return node.SegmentOrRootDisplaySpan;
        if (_maxDepth > 0 && node.Depth <= _maxDepth)
            return _emitPathBuf.AsSpan(0, _emitPathLen);
        return GetNodeDisplayPath(node, ref scratch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int WriteEmitChildPath(DirNode node, ReadOnlySpan<byte> childName, ref byte[] scratch)
    {
        if (node.Parent == null)
            return DuAccounting.WriteChildPathInto(node.SegmentOrRootDisplaySpan, childName, ref scratch);
        if (_maxDepth > 0 && node.Depth <= _maxDepth)
            return DuAccounting.WriteChildPathInto(_emitPathBuf.AsSpan(0, _emitPathLen), childName, ref scratch);
        return WriteNodeChildPathInto(node, childName, ref scratch);
    }

    private void EmitBsdSymlinkCycleError(ReadOnlySpan<byte> childPath, DirNode cycleTarget)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, cycleTarget.FullDisplayLength));
        try
        {
            FillNodeDisplayPath(cycleTarget, rented);
            _writer!.WriteBsdSymlinkCycleError(childPath, rented.AsSpan(0, cycleTarget.FullDisplayLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void OpenPreOpenedChild(int parentFd, DirNode child)
    {
        byte[] segNullTerm = child.RawPathOrSegmentNullTerm;
        if (segNullTerm.Length <= 1)
            return;

        fixed (byte* pName = segNullTerm)
        {
            if (NativeIo.OpenRawDirectoryFdRelative(parentFd, pName, out int fd) == 0 && fd >= 0)
                child.PreOpenedFd = fd;
        }
    }

    private void CloseAndClearPreOpenedFd(DirNode node)
    {
        if (!_workersStarted)
        {
            int fd = node.PreOpenedFd;
            if (fd >= 0)
            {
                node.PreOpenedFd = -1;
                _ = Libc.close(fd);
            }

            return;
        }

        int atomicFd = Interlocked.Exchange(ref node.PreOpenedFd, -1);
        if (atomicFd >= 0)
            _ = Libc.close(atomicFd);
    }

    private void ReleaseNodePayload(DirNode node)
    {
        if (!_workersStarted)
        {
            if (node.PreOpenedFd >= 0)
            {
                _ = Libc.close(node.PreOpenedFd);
                node.PreOpenedFd = -1;
            }

            if (node.Entries != null)
            {
                ArrayPool<ScannedEntry>.Shared.Return(node.Entries);
                node.Entries = null;
            }

            if (node.NameArena != null)
            {
                ArrayPool<byte>.Shared.Return(node.NameArena);
                node.NameArena = null;
            }

            node.SingleSubdir = null;
            node.Subdirs = null;
            node.EntryCount = 0;
            node.SubdirCount = 0;
            if (node.UnprintedWeight > 0)
            {
                ReleaseUnprintedEntries(node.UnprintedWeight);
                node.UnprintedWeight = 0;
            }

            return;
        }

        CloseAndClearPreOpenedFd(node);

        ScannedEntry[]? entries = Interlocked.Exchange(ref node.Entries, null);
        if (entries != null)
            ArrayPool<ScannedEntry>.Shared.Return(entries);

        byte[]? arena = Interlocked.Exchange(ref node.NameArena, null);
        if (arena != null)
            ArrayPool<byte>.Shared.Return(arena);

        node.SingleSubdir = null;
        node.Subdirs = null;
        node.EntryCount = 0;
        node.SubdirCount = 0;

        if (node.UnprintedWeight != 0)
        {
            int weight = Interlocked.Exchange(ref node.UnprintedWeight, 0);
            if (weight > 0)
                ReleaseUnprintedEntries(weight);
        }
    }

    private void CancelAndReleaseSubtree(DirNode root)
    {
        Stack<DirNode>? toVisit = null;
        DirNode? current = root;
        while (current != null)
        {
            if (_workersStarted)
                _ = Interlocked.CompareExchange(ref current.State, StateCancelled, StatePending);
            else if (current.State == StatePending)
                current.State = StateCancelled;

            CloseAndClearPreOpenedFd(current);

            if (current.SingleSubdir != null)
                (toVisit ??= new Stack<DirNode>()).Push(current.SingleSubdir);

            DirNode?[]? subdirs = current.Subdirs;
            int count = current.SubdirCount;
            if (subdirs != null && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (subdirs[i] != null)
                        (toVisit ??= new Stack<DirNode>()).Push(subdirs[i]!);
                }
            }

            ReleaseNodePayload(current);
            current = toVisit is { Count: > 0 } ? toVisit.Pop() : null;
        }
    }

    private int ReadEntriesGetdents64(int dirFd, WorkerScratch scratch, out int errno)
    {
        int rawCount = 0;
        int order = 0;
        errno = 0;

        while (true)
        {
            nint read;
            fixed (byte* pBuf = scratch.DirentBuffer)
            {
                read = Libc.getdents64(dirFd, pBuf, (nuint)scratch.DirentBuffer.Length);
                if (read == 0)
                    return rawCount;

                if (read < 0)
                {
                    errno = NativeIo.GetLastErrno();
                    return rawCount;
                }

                int offset = 0;
                int limit = (int)read;
                while (offset < limit)
                {
                    byte* rec = pBuf + offset;
                    ushort reclen = *(ushort*)(rec + 16);
                    offset += reclen;

                    byte* namePtr = rec + 19;
                    if (namePtr[0] == '.' && (namePtr[1] == 0 || (namePtr[1] == '.' && namePtr[2] == 0)))
                        continue;

                    ulong inode = *(ulong*)rec;
                    ReadOnlySpan<byte> nameSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(namePtr);
                    PushRawEntry(scratch, ref rawCount, inode, order++, nameSpan);
                }
            }

            if (scratch.DirentBuffer.Length < DuAccounting.DirentReadBufferSize &&
                read >= scratch.DirentBuffer.Length - 512)
            {
                scratch.DirentBuffer = new byte[DuAccounting.DirentReadBufferSize];
            }
        }
    }

    private static int ReadEntriesFallback(NativeDirectory dir, WorkerScratch scratch, out int errno)
    {
        int rawCount = 0;
        int order = 0;
        bool hasInlineStats = dir.HasInlineStats;
        while (dir.MoveNext())
        {
            int entryOrder = order++;
            if (hasInlineStats && dir.TryGetInlineStat(out NativeStat inlineStat))
            {
                if (scratch.TempInlineStats.Length <= entryOrder)
                {
                    int newLen = scratch.TempInlineStats.Length == 0 ? 256 : scratch.TempInlineStats.Length * 2;
                    while (newLen <= entryOrder)
                        newLen *= 2;
                    Array.Resize(ref scratch.TempInlineStats, newLen);
                }

                scratch.TempInlineStats[entryOrder] = inlineStat;
            }

            PushRawEntry(scratch, ref rawCount, dir.CurrentInode, entryOrder, dir.CurrentName);
        }

        errno = dir.Error;
        return rawCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushRawEntry(
        WorkerScratch scratch,
        ref int rawCount,
        ulong inode,
        int order,
        ReadOnlySpan<byte> name)
    {
        int requiredArena = scratch.TempNameArenaLen + name.Length + 1;
        if (requiredArena > scratch.TempNameArena.Length)
            DuAccounting.GrowByteArray(ref scratch.TempNameArena, requiredArena);

        int nameOffset = scratch.TempNameArenaLen;
        name.CopyTo(scratch.TempNameArena.AsSpan(nameOffset));
        scratch.TempNameArena[nameOffset + name.Length] = 0;
        scratch.TempNameArenaLen = requiredArena;

        if (rawCount == scratch.TempEntries.Length)
            Array.Resize(ref scratch.TempEntries, scratch.TempEntries.Length * 2);

        scratch.TempEntries[rawCount++] = new RawDirEntry(inode, order, nameOffset, name.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AppendToCompactArena(WorkerScratch scratch, ReadOnlySpan<byte> name)
    {
        int required = scratch.CompactArenaLen + name.Length;
        if (required > scratch.CompactArena.Length)
            DuAccounting.GrowByteArray(ref scratch.CompactArena, Math.Max(2048, required));

        int offset = scratch.CompactArenaLen;
        name.CopyTo(scratch.CompactArena.AsSpan(offset));
        scratch.CompactArenaLen = required;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AppendToRentedArena(ref byte[]? arena, ref int currentLen, ReadOnlySpan<byte> name)
    {
        if (arena == null)
            arena = ArrayPool<byte>.Shared.Rent(Math.Max(4096, name.Length));

        int required = currentLen + name.Length;
        if (required > arena.Length)
        {
            int newSize = Math.Max(arena.Length * 2, required);
            byte[] grown = ArrayPool<byte>.Shared.Rent(newSize);
            arena.AsSpan(0, currentLen).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(arena);
            arena = grown;
        }

        int offset = currentLen;
        name.CopyTo(arena.AsSpan(offset));
        currentLen = required;
        return offset;
    }
}
