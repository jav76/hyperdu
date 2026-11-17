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
        public readonly byte[] NullTermPath;
        public readonly NativeStat DirStat;
        public readonly FileIdentity Identity;
        public readonly ulong RootDevice;
        public readonly ulong Depth;
        public readonly int ChildIndex;
        public readonly bool FollowChildSymlinks;
        public readonly bool SkipDescent;

        public int PreOpenedFd = -1;
        public int OpenErrno;
        public ulong InlineNonDirBytes;
        public FileTimestamp InlineMaxTime;
        public bool HasInlineTime;
        public ScannedEntry[]? Entries;
        public int EntryCount;
        public byte[]? NameArena;
        public DirNode?[]? Subdirs;
        public int SubdirCount;
        public int UnprintedWeight;

        public int State;
        public int Promoted;

        public DirNode(
            DirNode? parent,
            byte[] nullTermPath,
            in NativeStat dirStat,
            FileIdentity identity,
            ulong rootDevice,
            ulong depth,
            int childIndex,
            bool followChildSymlinks,
            bool skipDescent = false)
        {
            Parent = parent;
            NullTermPath = nullTermPath;
            DirStat = dirStat;
            Identity = identity;
            RootDevice = rootDevice;
            Depth = depth;
            ChildIndex = childIndex;
            FollowChildSymlinks = followChildSymlinks;
            SkipDescent = skipDescent;
            if (skipDescent)
                State = StateCompleted;
        }

        public ReadOnlySpan<byte> DisplayPath =>
            NullTermPath.Length > 0 ? NullTermPath.AsSpan(0, NullTermPath.Length - 1) : [];
    }

    private sealed class LargeDirBatch
    {
        public readonly DirNode Node;
        public readonly int DirFd;
        public readonly NativeDirectory? FallbackDir;
        public readonly ulong RootDevice;
        public readonly RawDirEntry[] RawEntries;
        public readonly int RawCount;
        public readonly byte[] RawNameArena;
        public readonly ChunkSpec[] Chunks;
        public readonly int FinalReadErrno;
        public int RemainingChunks;

        public LargeDirBatch(
            DirNode node,
            int dirFd,
            NativeDirectory? fallbackDir,
            ulong rootDevice,
            RawDirEntry[] rawEntries,
            int rawCount,
            byte[] rawNameArena,
            ChunkSpec[] chunks,
            int finalReadErrno)
        {
            Node = node;
            DirFd = dirFd;
            FallbackDir = fallbackDir;
            RootDevice = rootDevice;
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
        public readonly byte[] DirentBuffer = new byte[DuAccounting.DirentReadBufferSize];
        public readonly byte[] StatBuffer = new byte[NativeIo.StatBufferSize];
        public byte[] PathScratch = new byte[8192];
        public byte[] TempNameArena = new byte[65536];
        public int TempNameArenaLen;
        public RawDirEntry[] TempEntries = new RawDirEntry[1024];
        public ScannedEntry[] TempScanned = new ScannedEntry[1024];
        public readonly List<DirNode> TempSubdirs = new(32);
        public byte[] CompactArena = new byte[65536];
        public int CompactArenaLen;
        public NativeStat[] TempInlineStats = [];
    }

    private struct EmitFrame
    {
        public DirNode Node;
        public int EntryIndex;
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
    private WorkerScratch? _inlineScratch;

    private readonly object _emitterSync = new();
    private readonly object _workerSpawnLock = new();
    private readonly List<Thread> _workerThreads = [];

    private DuOutputWriter? _writer;
    private FrontierWorkQueue<WorkItem>? _queue;
    private DirNode? _frontierTarget;
    private DirNode? _waitingOnNode;
    private CancellationToken _cancellationToken;

    private EmitFrame[]? _emitStack;
    private int _emitStackDepth;

    private ulong _grandTotal;
    private FileTimestamp _grandMaxTime = new(0, 0);

    private long _unprintedEntries;
    private long _peakUnprintedEntries;
    private int _backpressurePauseCount;
    private int _chunkStatBatches;
    private int _pendingInlineSiblings;
    private bool _heuristicsResolved;
    private bool _canEverFanOut = true;
    private bool _workersStarted;
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

        byte[] displayPath = DuAccounting.NormalizeDisplayPath(rawOperand);
        if (_hasExcludes && _options.Excludes.IsExcluded(displayPath))
            return;

        byte[] nullTermPath = new byte[displayPath.Length + 1];
        displayPath.CopyTo(nullTermPath, 0);
        nullTermPath[displayPath.Length] = 0;

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

        bool followChild = _options.SymlinkPolicy == SymlinkPolicy.DereferenceAll;
        DirNode rootNode = new(
            parent: null,
            nullTermPath: nullTermPath,
            dirStat: in stat,
            identity: operandId,
            rootDevice: stat.Device,
            depth: 0,
            childIndex: 0,
            followChildSymlinks: followChild);

        Volatile.Write(ref _frontierTarget, rootNode);
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
        _emitStackDepth = 0;
        if (!PushEmitFrame(rootNode))
            return default;

        DirWalkResult rootResult = default;
        while (_emitStackDepth > 0)
        {
            if (_cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(_cancellationToken);

            ref EmitFrame frame = ref _emitStack![_emitStackDepth - 1];
            DirNode node = frame.Node;
            ScannedEntry[]? entries = node.Entries;
            int entryCount = node.EntryCount;

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
                        int len = DuAccounting.WriteChildPathInto(node.DisplayPath, name, ref inlineScratch.PathScratch);
                        _writer!.WritePosixError("cannot access", inlineScratch.PathScratch.AsSpan(0, len), entry.SubdirOrErrno);
                        break;
                    }
                    case ScannedEntryKind.ReadError:
                    {
                        _writer!.WritePosixError("cannot read directory", node.DisplayPath, entry.SubdirOrErrno);
                        break;
                    }
                    case ScannedEntryKind.BsdSymlinkLoop:
                    {
                        ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                        int len = DuAccounting.WriteChildPathInto(node.DisplayPath, name, ref inlineScratch.PathScratch);
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
                            int len = DuAccounting.WriteChildPathInto(node.DisplayPath, name, ref inlineScratch.PathScratch);
                            _writer!.WriteBsdSymlinkCycleError(inlineScratch.PathScratch.AsSpan(0, len), cycleTarget.DisplayPath);
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

                        if (_options.All && node.Depth + 1 <= _maxDepth && DuAccounting.ShouldEmitForThreshold(entry.Value, _options, isDirectory: false))
                        {
                            ReadOnlySpan<byte> name = node.NameArena.AsSpan(entry.NameOffset, entry.NameLength);
                            int len = DuAccounting.WriteChildPathInto(node.DisplayPath, name, ref inlineScratch.PathScratch);
                            _writer!.EmitLine(entry.Value, entry.Time, inlineScratch.PathScratch.AsSpan(0, len));
                        }

                        break;
                    }
                    case ScannedEntryKind.Subdir:
                    {
                        DirNode? child = node.Subdirs![entry.SubdirOrErrno];
                        if (child == null)
                            break;

                        if (!_workersStarted)
                            _pendingInlineSiblings = Math.Max(0, _pendingInlineSiblings - 1);

                        if (_activeAncestors.Contains(child.Identity))
                        {
                            if (_options.Dialect == DuDialect.Bsd)
                            {
                                DirNode? cycleTarget = FindAncestorNode(node, child.Identity);
                                if (cycleTarget != null)
                                {
                                    _writer!.WriteBsdSymlinkCycleError(child.DisplayPath, cycleTarget.DisplayPath);
                                    ShutdownWorkers();
                                    break;
                                }
                            }

                            CancelAndReleaseSubtree(child);
                            break;
                        }

                        if (!_options.CountLinks && _options.Dialect == DuDialect.Gnu && !_seen.TryClaim(child.Identity, child.DirStat.HardLinks))
                        {
                            CancelAndReleaseSubtree(child);
                            break;
                        }

                        Volatile.Write(ref _frontierTarget, child);
                        _ = PushEmitFrame(child);
                        break;
                    }
                }

                if (_stoppedEarly)
                    break;

                continue;
            }

            // Directory children finished: compute totals, emit line if within max-depth/threshold, and pop frame.
            _activeAncestors.Remove(node.Identity);

            ulong ownBytes = DuAccounting.ComputeEntryValue(in node.DirStat, _options);
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
                _writer!.EmitLine(reportedTotal, reportedTime, node.DisplayPath);

            DirWalkResult dirResult = new(
                _options.SeparateDirs ? 0UL : reportedTotal,
                fullTotal,
                reportedTime,
                fullTime);

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

        if (_emitStackDepth > 0)
        {
            ShutdownWorkers();
            while (_emitStackDepth > 0)
            {
                ref EmitFrame remaining = ref _emitStack![--_emitStackDepth];
                _activeAncestors.Remove(remaining.Node.Identity);
                CancelAndReleaseSubtree(remaining.Node);
            }
        }

        return rootResult;
    }

    private bool PushEmitFrame(DirNode node)
    {
        EnsureNodeCompleted(node);

        if (node.OpenErrno != 0)
        {
            ulong ownBytes = DuAccounting.ComputeEntryValue(in node.DirStat, _options);
            FileTimestamp ownTime = DuAccounting.ExtractTimestamp(in node.DirStat, _options);
            _writer!.WritePosixError("cannot read directory", node.DisplayPath, node.OpenErrno);
            if (_options.Dialect == DuDialect.Bsd)
            {
                ReleaseNodePayload(node);
                return _emitStackDepth != 0;
            }

            if (node.Depth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(ownBytes, _options))
                _writer.EmitLine(ownBytes, ownTime, node.DisplayPath);

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

        _activeAncestors.Add(node.Identity);

        FileTimestamp dirOwnTime = DuAccounting.ExtractTimestamp(in node.DirStat, _options);
        FileTimestamp initialMaxTime = node.HasInlineTime
            ? FileTimestamp.Max(dirOwnTime, node.InlineMaxTime)
            : dirOwnTime;

        _emitStack ??= new EmitFrame[128];
        if (_emitStackDepth == _emitStack.Length)
            Array.Resize(ref _emitStack, _emitStack.Length * 2);

        _emitStack[_emitStackDepth++] = new EmitFrame
        {
            Node = node,
            EntryIndex = 0,
            DirectNonDirBytes = node.InlineNonDirBytes,
            DirectMaxTime = initialMaxTime
        };

        return true;
    }

    private void EnsureNodeCompleted(DirNode node)
    {
        if (Volatile.Read(ref node.State) == StateCompleted)
            return;

        if (!_workersStarted)
        {
            if (Interlocked.CompareExchange(ref node.State, StateClaimed, StatePending) == StatePending)
            {
                ScanDirectoryNode(node, EnsureInlineScratch(), isWorkerThread: false);
            }

            if (Volatile.Read(ref node.State) == StateCompleted)
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
        int dirFd;
        NativeDirectory? fallbackDir;
        int openErrno;

        int preOpenedFd = Interlocked.Exchange(ref node.PreOpenedFd, -1);
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
            DuAccounting.OpenDirectoryPathSafe(
                node.NullTermPath,
                out dirFd,
                out fallbackDir,
                out openErrno,
                followOpen);
        }

        if (openErrno != 0)
        {
            node.OpenErrno = openErrno;
            MarkNodeCompleted(node);
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
                TryEnableWorkersForLargeDir(node, rawCount))
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
                isWorkerThread);
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
        bool isWorkerThread)
    {
        scratch.TempSubdirs.Clear();
        scratch.CompactArenaLen = 0;
        if (scratch.TempScanned.Length < rawCount + 1)
            scratch.TempScanned = new ScannedEntry[rawCount + 1];

        int keptCount = 0;
        ulong inlineBytes = 0;
        FileTimestamp inlineTime = default;
        bool hasInlineTime = false;
        bool followChild = node.FollowChildSymlinks;

        for (int i = 0; i < rawCount; i++)
        {
            if ((i & 255) == 0 && _cancellationToken.IsCancellationRequested)
                break;

            ref readonly RawDirEntry raw = ref scratch.TempEntries[i];
            ReadOnlySpan<byte> nameSpan = scratch.TempNameArena.AsSpan(raw.NameOffset, raw.NameLength);

            if (_hasExcludes && DuAccounting.ShouldExcludeChild(_options, node.DisplayPath, nameSpan, ref scratch.PathScratch))
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
                        node.DisplayPath,
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

            if (!TryClassifyChild(node, node.RootDevice, nameSpan, in childStat, scratch.TempSubdirs.Count, ref scratch.PathScratch, out DirNode? childNode, out FileIdentity childId, out bool isBsdCycle))
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
            scratch.TempSubdirs);

        // Pre-open Subdirs[0] via openat while parent dirFd is still open for fast FD-relative spine traversal.
        DirNode? inlineContinuation = null;
        if (node.SubdirCount > 0 && fallbackDir == null && !followChild && NativeIo.UsesGetdents64)
        {
            DirNode firstChild = node.Subdirs![0]!;
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
                else if (node.SubdirCount == 1)
                {
                    OpenPreOpenedChild(dirFd, firstChild);
                }
            }
        }

        if (isWorkerThread)
        {
            EnqueueRemainingSubdirs(node, startIndex: inlineContinuation != null ? 1 : 0);
            MarkNodeCompleted(node);
            return inlineContinuation;
        }

        MarkNodeCompleted(node);

        if (node.SubdirCount > 0)
        {
            _pendingInlineSiblings += Math.Max(0, node.SubdirCount - 1);
            if (_canEverFanOut && _pendingInlineSiblings >= FanOutSiblingThreshold)
            {
                if (TryActivateWorkersFromInline(node))
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
        List<DirNode> subdirs)
    {
        node.InlineNonDirBytes = inlineBytes;
        node.InlineMaxTime = inlineTime;
        node.HasInlineTime = hasInlineTime;
        node.EntryCount = entries.Length;

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

        if (subdirs.Count > 0)
        {
            node.Subdirs = subdirs.ToArray();
            node.SubdirCount = subdirs.Count;
        }

        int weight = entries.Length + 1;
        node.UnprintedWeight = weight;
        AddUnprintedEntries(weight);
    }

    private void MarkNodeCompleted(DirNode node)
    {
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
            node.RootDevice,
            rawEntries,
            rawCount,
            rawNames,
            chunks,
            finalReadErrno);

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

        for (int i = spec.Start; i < spec.End; i++)
        {
            if ((i & 255) == 0 && _cancellationToken.IsCancellationRequested)
                break;

            ref readonly RawDirEntry raw = ref batch.RawEntries[i];
            ReadOnlySpan<byte> nameSpan = batch.RawNameArena.AsSpan(raw.NameOffset, raw.NameLength);

            if (_hasExcludes && DuAccounting.ShouldExcludeChild(_options, node.DisplayPath, nameSpan, ref scratch.PathScratch))
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
                    node.DisplayPath,
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

            if (!TryClassifyChild(node, batch.RootDevice, nameSpan, in childStat, subdirs?.Count ?? 0, ref scratch.PathScratch, out DirNode? childNode, out FileIdentity childId, out bool isBsdCycle))
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

    private void FinalizeLargeDirBatch(LargeDirBatch batch)
    {
        if (batch.FallbackDir != null)
            batch.FallbackDir.Dispose();
        else if (batch.DirFd >= 0)
            _ = Libc.close(batch.DirFd);
        ArrayPool<RawDirEntry>.Shared.Return(batch.RawEntries);
        ArrayPool<byte>.Shared.Return(batch.RawNameArena);

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
        DirNode?[]? finalSubdirs = totalSubdirs > 0 ? new DirNode?[totalSubdirs] : null;

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
                    finalSubdirs![subdirOffset++] = s;
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
        node.Subdirs = finalSubdirs;
        node.SubdirCount = subdirOffset;
        node.UnprintedWeight = entryPos + 1;
        AddUnprintedEntries(node.UnprintedWeight);

        EnqueueRemainingSubdirs(node, startIndex: 0);
        MarkNodeCompleted(node);
    }

    private void EnqueueRemainingSubdirs(DirNode parent, int startIndex)
    {
        if (_stoppedEarly || _cancellationToken.IsCancellationRequested)
            return;

        DirNode?[]? subdirs = parent.Subdirs;
        int subdirCount = parent.SubdirCount;
        if (subdirs == null || subdirCount <= startIndex || _queue == null)
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

    private bool TryEnableWorkersForLargeDir(DirNode node, int rawCount)
    {
        if (!_canEverFanOut)
            return false;

        if (!_heuristicsResolved)
            ResolveWorkerBudget(Encoding.UTF8.GetString(node.DisplayPath));

        if (!_canEverFanOut)
            return false;

        int desired = Math.Min(_maxWorkerBudget, Math.Max(2, rawCount / LargeDirChunkThreshold));
        EnsureQueueAndWorkersStarted(desired);
        return _workersStarted;
    }

    private bool TryActivateWorkersFromInline(DirNode latestNode)
    {
        if (!_heuristicsResolved)
            ResolveWorkerBudget(Encoding.UTF8.GetString(latestNode.DisplayPath));

        if (!_canEverFanOut)
            return false;

        int initialDemand = Math.Min(_maxWorkerBudget, Math.Max(2, Math.Min(4, _pendingInlineSiblings + latestNode.SubdirCount)));
        EnsureQueueAndWorkersStarted(initialDemand);

        // Push all unvisited pending sibling subdirs from active emitter stack frames into queue (outermost first so innermost/leftmost pops first).
        for (int f = 0; f < _emitStackDepth; f++)
        {
            ref EmitFrame frame = ref _emitStack![f];
            DirNode frameNode = frame.Node;
            if (frameNode.Entries == null || frameNode.Subdirs == null)
                continue;

            for (int e = frameNode.EntryCount - 1; e >= frame.EntryIndex; e--)
            {
                ref readonly ScannedEntry se = ref frameNode.Entries[e];
                if (se.Kind != ScannedEntryKind.Subdir)
                    continue;

                DirNode? pendingSub = frameNode.Subdirs[se.SubdirOrErrno];
                if (pendingSub != null && Volatile.Read(ref pendingSub.State) == StatePending)
                {
                    int tier = se.SubdirOrErrno <= 2 ? ScanTier.Medium : ScanTier.Low;
                    _queue!.Enqueue(WorkItem.ForDir(pendingSub), tier);
                }
            }
        }

        // Enqueue latestNode's own discovered subdirs with Subdirs[0] in High tier.
        if (latestNode.SubdirCount > 0 && latestNode.Subdirs != null)
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
        if (_queue == null)
        {
            lock (_workerSpawnLock)
            {
                _queue ??= new FrontierWorkQueue<WorkItem>(IsBackpressureActive);
            }
        }

        EnsureWorkersSpawned(targetWorkers);
        _workersStarted = true;
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
        ulong rootDevice,
        ReadOnlySpan<byte> nameSpan,
        in NativeStat childStat,
        int childIndex,
        ref byte[] pathScratch,
        out DirNode? subdirNode,
        out FileIdentity childId,
        out bool isBsdCycle)
    {
        subdirNode = null;
        isBsdCycle = false;
        childId = new FileIdentity(childStat.Device, childStat.Inode);

        if (DuAccounting.ShouldIgnoreBsdChild(in childStat, _options, parent.DisplayPath, nameSpan, ref pathScratch))
            return false;

        bool crossDevice = _options.OneFileSystem && childStat.Device != rootDevice;
        if (crossDevice && _options.Dialect != DuDialect.Bsd)
            return false;

        if (childStat.Kind == NativeEntryKind.Directory)
        {
            if (FindAncestorNode(parent, childId) != null)
            {
                isBsdCycle = _options.Dialect == DuDialect.Bsd;
                return false;
            }

            byte[] childNullTermPath = DuAccounting.BuildNullTerminatedChildPath(parent.DisplayPath, nameSpan);
            subdirNode = new DirNode(
                parent: parent,
                nullTermPath: childNullTermPath,
                dirStat: in childStat,
                identity: childId,
                rootDevice: rootDevice,
                depth: parent.Depth + 1,
                childIndex: childIndex,
                followChildSymlinks: parent.FollowChildSymlinks,
                skipDescent: crossDevice);
        }

        return true;
    }

    private static DirNode? FindAncestorNode(DirNode current, FileIdentity candidate)
    {
        if (candidate.Inode == 0 || candidate.Inode == ulong.MaxValue)
            return null;

        DirNode? cursor = current;
        while (cursor != null)
        {
            if (cursor.Identity == candidate)
                return cursor;
            cursor = cursor.Parent;
        }

        return null;
    }

    private static void OpenPreOpenedChild(int parentFd, DirNode child)
    {
        ReadOnlySpan<byte> full = child.DisplayPath;
        int lastSlash = DuAccounting.LastSeparatorIndex(full);
        ReadOnlySpan<byte> childName = lastSlash >= 0 ? full[(lastSlash + 1)..] : full;
        if (childName.IsEmpty)
            return;

        byte* nameBuf = stackalloc byte[childName.Length + 1];
        childName.CopyTo(new Span<byte>(nameBuf, childName.Length));
        nameBuf[childName.Length] = 0;

        if (NativeIo.OpenRawDirectoryFdRelative(parentFd, nameBuf, out int fd) == 0 && fd >= 0)
            child.PreOpenedFd = fd;
    }

    private static void CloseAndClearPreOpenedFd(DirNode node)
    {
        int fd = Interlocked.Exchange(ref node.PreOpenedFd, -1);
        if (fd >= 0)
            _ = Libc.close(fd);
    }

    private void ReleaseNodePayload(DirNode node)
    {
        CloseAndClearPreOpenedFd(node);

        ScannedEntry[]? entries = Interlocked.Exchange(ref node.Entries, null);
        if (entries != null)
            ArrayPool<ScannedEntry>.Shared.Return(entries);

        byte[]? arena = Interlocked.Exchange(ref node.NameArena, null);
        if (arena != null)
            ArrayPool<byte>.Shared.Return(arena);

        node.Subdirs = null;
        node.EntryCount = 0;
        node.SubdirCount = 0;

        int weight = Interlocked.Exchange(ref node.UnprintedWeight, 0);
        if (weight > 0)
            ReleaseUnprintedEntries(weight);
    }

    private void CancelAndReleaseSubtree(DirNode root)
    {
        Stack<DirNode>? toVisit = null;
        DirNode? current = root;
        while (current != null)
        {
            _ = Interlocked.CompareExchange(ref current.State, StateCancelled, StatePending);
            CloseAndClearPreOpenedFd(current);

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

        fixed (byte* pBuf = scratch.DirentBuffer)
        {
            while (true)
            {
                nint read = Libc.getdents64(dirFd, pBuf, (nuint)scratch.DirentBuffer.Length);
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
            DuAccounting.GrowByteArray(ref scratch.CompactArena, required);

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
