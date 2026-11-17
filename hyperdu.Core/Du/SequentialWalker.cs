using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

/// <summary>
/// Sequential post-order directory walker providing exact GNU du accounting,
/// raw byte path handling, trailing slash preservation, and hard-link deduplication.
/// Uses reusable stack arenas and direct Linux getdents64 reads to minimize GC allocations.
/// </summary>
public sealed unsafe class SequentialWalker
{
    private readonly record struct DirEntry(ulong Inode, int Order, int NameOffset, int NameLength);

    private sealed class DirEntryInodeComparer : IComparer<DirEntry>
    {
        public static readonly DirEntryInodeComparer Instance = new();

        public int Compare(DirEntry x, DirEntry y)
        {
            int cmp = x.Inode.CompareTo(y.Inode);
            return cmp != 0 ? cmp : x.Order.CompareTo(y.Order);
        }
    }

    private static readonly int OpenFdSafetyCap = OperatingSystem.IsMacOS() ? 200 : 850;

    private readonly DuOptions _options;
    private readonly string _programName;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly byte[] _statBuffer = new byte[NativeIo.StatBufferSize];
    private readonly byte[] _direntBuffer = new byte[DuAccounting.DirentReadBufferSize];
    private byte[] _ignoreScratch = new byte[512];
    private readonly DuIdentityTracker _seen;
    private readonly HashSet<FileIdentity> _activeAncestors = [];
    private readonly List<(FileIdentity Id, int PathLen)> _ancestorStack = [];
    private readonly ulong _maxDepth;
    private readonly bool _hasExcludes;
    private readonly bool _showTime;
    private bool _trackSingleLinkFiles;
    private bool _stoppedEarly;

    private byte[] _nameArena = new byte[65536];
    private int _nameArenaPos;
    private DirEntry[] _entryStack = new DirEntry[4096];
    private NativeStat[]? _inlineStats;
    private int _entryStackPos;
    private byte[] _pathStack = new byte[8192];
    private int _pathLen;

    private DuOutputWriter? _writer;
    private int _openDirCount;
    private ulong _rootDevice;
    private ulong _grandTotal;
    private FileTimestamp _grandMaxTime = new(0, 0);

    public SequentialWalker(DuOptions options, string programName, Stream stdout, Stream stderr)
        : this(options, programName, Stream.Null, stdout, stderr)
    {
    }

    public SequentialWalker(DuOptions options, string programName, Stream stdin, Stream stdout, Stream stderr)
    {
        _options = options;
        _programName = programName;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _seen = new DuIdentityTracker(options.Dialect);
        _maxDepth = options.Summarize ? 0UL : (options.MaxDepth ?? ulong.MaxValue);
        _hasExcludes = !options.Excludes.IsEmpty;
        _showTime = options.ShowTime;
        _trackSingleLinkFiles = options.Dialect == DuDialect.Gnu &&
                                (options.Operands.Count > 1 || options.Files0From != null);
    }

    public static void RestoreDefaultSigpipe() => NativeIo.RestoreDefaultSigpipe();

    public int Run() => RunWithTimeContext(() =>
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
            if (_stoppedEarly)
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
    });

    internal int Run(IReadOnlyList<byte[]> rawOperands) => RunWithTimeContext(() =>
    {
        if (rawOperands.Count > 1)
            _trackSingleLinkFiles = true;
        foreach (byte[] rawBytes in rawOperands)
        {
            if (_stoppedEarly)
                break;

            ProcessOperand(rawBytes);
        }
    });

    private int RunWithTimeContext(Action scanAction)
    {
        using DuOutputWriter writer = new(_options, _programName, _stdout, _stderr);
        _writer = writer;
        try
        {
            scanAction();

            if (!_stoppedEarly && _options.Total)
                writer.EmitLine(_grandTotal, _grandMaxTime, "total"u8);

            writer.Flush();
            return writer.HasError ? 1 : 0;
        }
        finally
        {
            _writer = null;
        }
    }

    private void ProcessOperand(ReadOnlySpan<byte> rawOperand)
    {
        if (_stoppedEarly)
            return;

        SetNormalizedRootPath(rawOperand);

        if (_hasExcludes && _options.Excludes.IsExcluded(CurrentPathSpan))
            return;

        byte[]? rentedNullTerm = null;
        Span<byte> nullTermSpan = rawOperand.Length + 1 <= 512
            ? stackalloc byte[512]
            : (rentedNullTerm = ArrayPool<byte>.Shared.Rent(rawOperand.Length + 1));

        try
        {
            rawOperand.CopyTo(nullTermSpan);
            nullTermSpan[rawOperand.Length] = 0;
            ReadOnlySpan<byte> nullTermPath = nullTermSpan[..(rawOperand.Length + 1)];

            bool followOperand = _options.SymlinkPolicy is SymlinkPolicy.DereferenceArgs or SymlinkPolicy.DereferenceAll;
            NativeStat stat;
            int errno;
            bool statOk;
            bool isBsdFatalSymlinkLoop;
            fixed (byte* pStat = _statBuffer)
            {
                statOk = DuAccounting.TryStatPathWithPolicy(nullTermPath, followOperand, _options, pStat, out stat, out errno, out isBsdFatalSymlinkLoop);
            }

            if (!statOk)
            {
                if (isBsdFatalSymlinkLoop)
                {
                    _writer!.WriteBsdTooManySymlinksError(CurrentPathSpan);
                    _stoppedEarly = true;
                    return;
                }

                _writer!.WritePosixError("cannot access", CurrentPathSpan, errno);
                return;
            }

            if (DuAccounting.ShouldIgnoreBsdEntry(in stat, _options, nullTermPath, DuAccounting.ExtractBasename(CurrentPathSpan)))
                return;

            _rootDevice = stat.Device;
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
                    _writer!.EmitLine(count, entryTime, CurrentPathSpan);
                return;
            }

            DuAccounting.OpenRootDirectory(nullTermPath, out int dirFd, out NativeDirectory? fallbackDir, out int openErrno, followOperand);
            DirWalkResult walked = WalkDirectory(dirFd, fallbackDir, openErrno, in stat, depth: 0);
            _grandTotal += walked.FullBytes;
            if (_showTime)
                _grandMaxTime = FileTimestamp.Max(_grandMaxTime, walked.FullTime);
        }
        finally
        {
            if (rentedNullTerm != null)
                ArrayPool<byte>.Shared.Return(rentedNullTerm);
        }
    }

    private DirWalkResult WalkDirectory(
        int dirFd,
        NativeDirectory? fallbackDir,
        int openErrno,
        in NativeStat dirStat,
        ulong depth)
    {
        ulong ownBytes = DuAccounting.ComputeEntryValue(in dirStat, _options);
        FileTimestamp directMaxTime = DuAccounting.ExtractTimestamp(in dirStat, _options);
        ulong directNonDirBytes = 0;
        ulong directSubdirReportedBytes = 0;
        ulong subdirFullBytes = 0;
        bool hasSubdirReportedTime = false;
        FileTimestamp subdirReportedMaxTime = default;
        bool hasSubdirFullTime = false;
        FileTimestamp subdirFullMaxTime = default;
        ulong childDepth = depth + 1;

        if (openErrno != 0)
        {
            _writer!.WritePosixError("cannot read directory", CurrentPathSpan, openErrno);
            if (_options.Dialect == DuDialect.Bsd)
                return default;

            if (depth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(ownBytes, _options))
                _writer.EmitLine(ownBytes, directMaxTime, CurrentPathSpan);
            return new DirWalkResult(ownBytes, ownBytes, directMaxTime, directMaxTime);
        }

        FileIdentity dirId = new(dirStat.Device, dirStat.Inode);
        bool trackAncestor = dirStat.HasValidFileId;
        if (trackAncestor)
            _activeAncestors.Add(dirId);
        if (_options.Dialect == DuDialect.Bsd)
            _ancestorStack.Add((dirId, _pathLen));
        _openDirCount++;
        int savedArenaPos = _nameArenaPos;
        int savedEntryPos = _entryStackPos;
        List<(byte[] ChildPath, NativeStat Stat)>? deferredSubdirs =
            _openDirCount >= OpenFdSafetyCap ? [] : null;

        try
        {
            int entryStart = _entryStackPos;
            if (fallbackDir == null)
                ReadEntriesGetdents64(dirFd);
            else
                ReadEntriesFallback(fallbackDir);
            int entryEnd = _entryStackPos;
            int count = entryEnd - entryStart;

            if (count > DuAccounting.InodeSortThreshold &&
                NativeIo.ShouldSortDirectoryByInode(dirFd, allowOnMacOs: _options.Dialect == DuDialect.Gnu))
                Array.Sort(_entryStack, entryStart, count, DirEntryInodeComparer.Instance);

            bool hasInlineStats = fallbackDir?.HasInlineStats == true;
            bool followChild = _options.SymlinkPolicy == SymlinkPolicy.DereferenceAll;
            for (int i = entryStart; i < entryEnd; i++)
            {
                if (_stoppedEarly)
                    break;

                int savedPath = _pathLen;
                ref readonly DirEntry childEntry = ref _entryStack[i];
                ProcessChildEntry(
                    dirFd,
                    fallbackDir,
                    childEntry,
                    hasInlineStats,
                    entryStart + childEntry.Order,
                    followChild,
                    childDepth,
                    deferredSubdirs,
                    ref directNonDirBytes,
                    ref directSubdirReportedBytes,
                    ref subdirFullBytes,
                    ref directMaxTime,
                    ref hasSubdirReportedTime,
                    ref subdirReportedMaxTime,
                    ref hasSubdirFullTime,
                    ref subdirFullMaxTime);
                _pathLen = savedPath;
            }
        }
        finally
        {
            if (_options.Dialect == DuDialect.Bsd && _ancestorStack.Count > 0)
                _ancestorStack.RemoveAt(_ancestorStack.Count - 1);
            if (trackAncestor)
                _activeAncestors.Remove(dirId);
            if (fallbackDir != null)
                fallbackDir.Dispose();
            else if (dirFd >= 0)
                _ = Libc.close(dirFd);

            _entryStackPos = savedEntryPos;
            _nameArenaPos = savedArenaPos;
            _openDirCount--;
        }

        if (_stoppedEarly)
            return default;

        if (deferredSubdirs != null)
        {
            ProcessDeferredSubdirs(
                deferredSubdirs,
                childDepth,
                ref directSubdirReportedBytes,
                ref subdirFullBytes,
                ref hasSubdirReportedTime,
                ref subdirReportedMaxTime,
                ref hasSubdirFullTime,
                ref subdirFullMaxTime);
            if (_stoppedEarly)
                return default;
        }

        ulong ownAndFiles = ownBytes + directNonDirBytes;
        ulong reportedTotal = _options.SeparateDirs ? ownAndFiles : ownAndFiles + directSubdirReportedBytes;
        ulong fullTotal = ownAndFiles + subdirFullBytes;

        FileTimestamp reportedTime = (!_options.SeparateDirs && hasSubdirReportedTime)
            ? FileTimestamp.Max(directMaxTime, subdirReportedMaxTime)
            : directMaxTime;
        FileTimestamp fullTime = hasSubdirFullTime
            ? FileTimestamp.Max(directMaxTime, subdirFullMaxTime)
            : directMaxTime;

        if (depth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(reportedTotal, _options))
            _writer!.EmitLine(reportedTotal, reportedTime, CurrentPathSpan);

        return new DirWalkResult(
            _options.SeparateDirs ? 0UL : reportedTotal,
            fullTotal,
            reportedTime,
            fullTime);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ProcessDeferredSubdirs(
        List<(byte[] ChildPath, NativeStat Stat)> deferredSubdirs,
        ulong childDepth,
        ref ulong directSubdirReportedBytes,
        ref ulong subdirFullBytes,
        ref bool hasSubdirReportedTime,
        ref FileTimestamp subdirReportedMaxTime,
        ref bool hasSubdirFullTime,
        ref FileTimestamp subdirFullMaxTime)
    {
        int savedPath = _pathLen;
        Span<byte> deferredStackBuf = stackalloc byte[512];
        foreach ((byte[] childPath, NativeStat childStat) in deferredSubdirs)
        {
            if (_stoppedEarly)
                break;

            SetExactPath(childPath);
            byte[]? rentedNullTerm = null;
            Span<byte> buf = childPath.Length + 1 <= deferredStackBuf.Length
                ? deferredStackBuf
                : (rentedNullTerm = ArrayPool<byte>.Shared.Rent(childPath.Length + 1));

            try
            {
                childPath.CopyTo(buf);
                buf[childPath.Length] = 0;
                DuAccounting.OpenRootDirectory(
                    buf[..(childPath.Length + 1)],
                    out int subFd,
                    out NativeDirectory? subFallback,
                    out int subOpenErrno,
                    _options.SymlinkPolicy == SymlinkPolicy.DereferenceAll);
                DirWalkResult sub = WalkDirectory(
                    subFd,
                    subFallback,
                    subOpenErrno,
                    in childStat,
                    childDepth);
                directSubdirReportedBytes += sub.ReportedToParent;
                subdirFullBytes += sub.FullBytes;
                DuAccounting.AccumulateSubdirTime(
                    in sub,
                    _options.SeparateDirs,
                    _showTime,
                    ref hasSubdirReportedTime,
                    ref subdirReportedMaxTime,
                    ref hasSubdirFullTime,
                    ref subdirFullMaxTime);
            }
            finally
            {
                if (rentedNullTerm != null)
                    ArrayPool<byte>.Shared.Return(rentedNullTerm);
            }
        }

        _pathLen = savedPath;
    }

    private void ProcessChildEntry(
        int dirFd,
        NativeDirectory? fallbackDir,
        DirEntry entry,
        bool hasInlineStats,
        int inlineStatIndex,
        bool followChild,
        ulong childDepth,
        List<(byte[] ChildPath, NativeStat Stat)>? deferredSubdirs,
        ref ulong directNonDirBytes,
        ref ulong directSubdirReportedBytes,
        ref ulong subdirFullBytes,
        ref FileTimestamp directMaxTime,
        ref bool hasSubdirReportedTime,
        ref FileTimestamp subdirReportedMaxTime,
        ref bool hasSubdirFullTime,
        ref FileTimestamp subdirFullMaxTime)
    {
        bool pathPushed = false;
        if (_hasExcludes)
        {
            if (_options.Excludes.MatchBasenameOnly)
            {
                if (_options.Excludes.IsExcludedChildBasename(_nameArena.AsSpan(entry.NameOffset, entry.NameLength)))
                    return;
            }
            else
            {
                PushPathSegment(entry.NameOffset, entry.NameLength);
                pathPushed = true;
                if (_options.Excludes.IsExcluded(CurrentPathSpan))
                    return;
            }
        }

        NativeStat childStat;
        int statErrno;
        bool useInline = hasInlineStats &&
                         (!followChild || _inlineStats![inlineStatIndex].Kind != NativeEntryKind.Symlink);
        if (useInline)
        {
            childStat = _inlineStats![inlineStatIndex];
        }
        else if (followChild)
        {
            // gnulib fts uses FTS_NOCHDIR under -L so kernel path resolution enforces ELOOP across symlink chains.
            if (!pathPushed)
            {
                PushPathSegment(entry.NameOffset, entry.NameLength);
                pathPushed = true;
            }

            ReadOnlySpan<byte> nullTermChildPath = NullTerminatedCurrentPath();
            bool statOk;
            bool isBsdFatalSymlinkLoop;
            fixed (byte* pStat = _statBuffer)
            {
                statOk = DuAccounting.TryStatFollowed(nullTermChildPath, _options, pStat, out childStat, out statErrno, out isBsdFatalSymlinkLoop);
            }

            if (!statOk)
            {
                if (isBsdFatalSymlinkLoop)
                {
                    _writer!.WriteBsdTooManySymlinksError(CurrentPathSpan);
                    _stoppedEarly = true;
                    return;
                }

                _writer!.WritePosixError("cannot access", CurrentPathSpan, statErrno);
                return;
            }
        }
        else if (!TryStatArenaEntry(dirFd, entry.NameOffset, followSymlinks: false, out childStat, out statErrno))
        {
            if (!pathPushed)
                PushPathSegment(entry.NameOffset, entry.NameLength);

            _writer!.WritePosixError("cannot access", CurrentPathSpan, statErrno);
            return;
        }

        if (_options.Dialect == DuDialect.Bsd &&
            DuAccounting.ShouldIgnoreBsdChild(
                in childStat,
                _options,
                CurrentPathWithoutLastSegment(pathPushed, entry.NameLength),
                _nameArena.AsSpan(entry.NameOffset, entry.NameLength),
                ref _ignoreScratch))
        {
            return;
        }

        if (_options.OneFileSystem && childStat.Device != _rootDevice)
        {
            if (_options.Dialect != DuDialect.Bsd)
                return;

            if (childStat.Kind == NativeEntryKind.Directory)
            {
                ulong mountOwn = DuAccounting.ComputeEntryValue(in childStat, _options);
                FileTimestamp mountTime = DuAccounting.ExtractTimestamp(in childStat, _options);
                directSubdirReportedBytes += mountOwn;
                subdirFullBytes += mountOwn;
                DuAccounting.AccumulateSubdirTime(
                    new DirWalkResult(mountOwn, mountOwn, mountTime, mountTime),
                    _options.SeparateDirs,
                    _showTime,
                    ref hasSubdirReportedTime,
                    ref subdirReportedMaxTime,
                    ref hasSubdirFullTime,
                    ref subdirFullMaxTime);
                if (childDepth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(mountOwn, _options, isDirectory: true))
                {
                    if (!pathPushed)
                        PushPathSegment(entry.NameOffset, entry.NameLength);
                    _writer!.EmitLine(mountOwn, mountTime, CurrentPathSpan);
                }

                return;
            }
        }

        FileIdentity childId = new(childStat.Device, childStat.Inode);
        if (childStat.HasValidFileId &&
            childStat.Kind == NativeEntryKind.Directory &&
            _activeAncestors.Contains(childId))
        {
            if (_options.Dialect == DuDialect.Bsd)
            {
                int cyclePathLen = FindAncestorPathLength(childId);
                if (cyclePathLen >= 0)
                {
                    if (!pathPushed)
                        PushPathSegment(entry.NameOffset, entry.NameLength);
                    _writer!.WriteBsdSymlinkCycleError(CurrentPathSpan, _pathStack.AsSpan(0, cyclePathLen));
                    _stoppedEarly = true;
                }
            }

            return;
        }

        if (!_options.CountLinks &&
            DuAccounting.ShouldTrackChildIdentity(in childStat, _options, followChild, _trackSingleLinkFiles) &&
            !_seen.TryClaim(childId, childStat.HardLinks))
        {
            return;
        }

        if (childStat.Kind == NativeEntryKind.Directory)
        {
            if (!pathPushed)
                PushPathSegment(entry.NameOffset, entry.NameLength);

            if (deferredSubdirs != null)
            {
                deferredSubdirs.Add((CurrentPathSpan.ToArray(), childStat));
                return;
            }

            int childFd;
            NativeDirectory? childFallback;
            int subOpenErrno;
            if (followChild)
                DuAccounting.OpenDirectoryPathSafe(NullTerminatedCurrentPath(), out childFd, out childFallback, out subOpenErrno, followSymlinks: true);
            else
                DuAccounting.OpenChildDirectory(dirFd, fallbackDir, _nameArena, entry.NameOffset, entry.NameLength, out childFd, out childFallback, out subOpenErrno, followSymlinks: false);

            DirWalkResult sub = WalkDirectory(
                childFd,
                childFallback,
                subOpenErrno,
                in childStat,
                childDepth);
            directSubdirReportedBytes += sub.ReportedToParent;
            subdirFullBytes += sub.FullBytes;
            DuAccounting.AccumulateSubdirTime(
                in sub,
                _options.SeparateDirs,
                _showTime,
                ref hasSubdirReportedTime,
                ref subdirReportedMaxTime,
                ref hasSubdirFullTime,
                ref subdirFullMaxTime);
            return;
        }

        ulong entryValue = DuAccounting.ComputeEntryValue(in childStat, _options);
        directNonDirBytes += entryValue;
        FileTimestamp entryTime = DuAccounting.ExtractTimestamp(in childStat, _options);
        if (_showTime)
            directMaxTime = FileTimestamp.Max(directMaxTime, entryTime);
        if (_options.All && childDepth <= _maxDepth && DuAccounting.ShouldEmitForThreshold(entryValue, _options, isDirectory: false))
        {
            if (!pathPushed)
                PushPathSegment(entry.NameOffset, entry.NameLength);
            _writer!.EmitLine(entryValue, entryTime, CurrentPathSpan);
        }
    }

    private ReadOnlySpan<byte> CurrentPathWithoutLastSegment(bool pathPushed, int childNameLength)
    {
        if (!pathPushed)
            return CurrentPathSpan;

        int end = _pathLen - childNameLength;
        if (end > 1 && DuAccounting.IsSeparator(_pathStack[end - 1]))
            end--;
        return _pathStack.AsSpan(0, Math.Max(0, end));
    }

    private ReadOnlySpan<byte> NullTerminatedCurrentPath()
    {
        if (_pathLen + 1 > _pathStack.Length)
            DuAccounting.GrowByteArray(ref _pathStack, _pathLen + 1);
        _pathStack[_pathLen] = 0;
        return _pathStack.AsSpan(0, _pathLen + 1);
    }

    private void ReadEntriesGetdents64(int dirFd)
    {
        int order = 0;
        fixed (byte* pBuf = _direntBuffer)
        {
            while (true)
            {
                nint read = Libc.getdents64(dirFd, pBuf, (nuint)_direntBuffer.Length);
                if (read == 0)
                    return;

                if (read < 0)
                {
                    _writer!.WritePosixError("cannot read directory", CurrentPathSpan, NativeIo.GetLastErrno());
                    return;
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
                    PushEntry(inode, order++, nameSpan);
                }
            }
        }
    }

    private void ReadEntriesFallback(NativeDirectory dir)
    {
        bool hasInlineStats = dir.HasInlineStats;
        int order = 0;
        while (dir.MoveNext())
        {
            int idx = _entryStackPos;
            PushEntry(dir.CurrentInode, order++, dir.CurrentName);
            if (hasInlineStats && dir.TryGetInlineStat(out NativeStat inlineStat))
            {
                if (_inlineStats == null || idx >= _inlineStats.Length)
                    Array.Resize(ref _inlineStats, Math.Max(4096, (_inlineStats?.Length ?? 0) * 2));
                _inlineStats[idx] = inlineStat;
            }
        }

        if (dir.Error != 0)
            _writer!.WritePosixError("cannot read directory", CurrentPathSpan, dir.Error);
    }

    private void PushEntry(ulong inode, int order, ReadOnlySpan<byte> name)
    {
        int requiredArena = _nameArenaPos + name.Length + 1;
        if (requiredArena > _nameArena.Length)
            DuAccounting.GrowByteArray(ref _nameArena, requiredArena);

        int nameOffset = _nameArenaPos;
        name.CopyTo(_nameArena.AsSpan(nameOffset));
        _nameArena[nameOffset + name.Length] = 0;
        _nameArenaPos = requiredArena;

        if (_entryStackPos == _entryStack.Length)
            Array.Resize(ref _entryStack, _entryStack.Length * 2);

        _entryStack[_entryStackPos++] = new DirEntry(inode, order, nameOffset, name.Length);
    }

    private int FindAncestorPathLength(FileIdentity cycleId)
    {
        for (int i = _ancestorStack.Count - 1; i >= 0; i--)
        {
            if (_ancestorStack[i].Id == cycleId)
                return _ancestorStack[i].PathLen;
        }

        return -1;
    }

    private bool TryStatArenaEntry(int dirFd, int nameOffset, bool followSymlinks, out NativeStat stat, out int errno)
    {
        fixed (byte* pName = &_nameArena[nameOffset])
        fixed (byte* pStat = _statBuffer)
        {
            return NativeIo.TryStatAt(dirFd, pName, followSymlinks, pStat, out stat, out errno);
        }
    }

    private ReadOnlySpan<byte> CurrentPathSpan => _pathStack.AsSpan(0, _pathLen);

    private void SetExactPath(ReadOnlySpan<byte> path)
    {
        if (path.Length > _pathStack.Length)
            DuAccounting.GrowByteArray(ref _pathStack, path.Length);

        path.CopyTo(_pathStack);
        _pathLen = path.Length;
    }

    private void SetNormalizedRootPath(ReadOnlySpan<byte> raw)
    {
        int targetLen = DuAccounting.ComputeNormalizedRootSpec(raw, out int copyLen, out bool appendSep, out byte sepByte);
        if (targetLen == 0)
        {
            _pathLen = 0;
            return;
        }

        if (targetLen > _pathStack.Length)
            DuAccounting.GrowByteArray(ref _pathStack, targetLen);

        if (copyLen > 0)
            raw[..copyLen].CopyTo(_pathStack);
        if (appendSep)
            _pathStack[copyLen] = sepByte;

        _pathLen = targetLen;
    }

    private int PushPathSegment(int nameOffset, int nameLength)
    {
        int saved = _pathLen;
        bool needsSlash = _pathLen > 0 && !DuAccounting.IsSeparator(_pathStack[_pathLen - 1]);
        int newLen = _pathLen + (needsSlash ? 1 : 0) + nameLength;
        if (newLen > _pathStack.Length)
            DuAccounting.GrowByteArray(ref _pathStack, newLen);

        int pos = _pathLen;
        if (needsSlash)
            _pathStack[pos++] = DuAccounting.DefaultSeparator;

        _nameArena.AsSpan(nameOffset, nameLength).CopyTo(_pathStack.AsSpan(pos));
        _pathLen = newLen;
        return saved;
    }

    internal static byte[] NormalizeDisplayPath(ReadOnlySpan<byte> raw) =>
        DuAccounting.NormalizeDisplayPath(raw);
}
