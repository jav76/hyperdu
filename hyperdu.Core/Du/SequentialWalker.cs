using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

/// <summary>
/// Sequential post-order directory walker providing exact GNU du Phase 1 accounting,
/// raw byte path handling, trailing slash preservation, and hard-link deduplication.
/// Uses reusable stack arenas and direct Linux getdents64 reads to minimize GC allocations.
/// </summary>
public sealed unsafe class SequentialWalker
{
    private readonly record struct FileIdentity(ulong Device, ulong Inode);
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

    private const int OutputBufferSize = 65536;
    private const int DirentReadBufferSize = 65536;
    private const int InodeSortThreshold = 10000;
    private static readonly int OpenFdSafetyCap = OperatingSystem.IsMacOS() ? 200 : 850;

    private readonly DuOptions _options;
    private readonly string _programName;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly byte[] _statBuffer = new byte[NativeIo.StatBufferSize];
    private readonly byte[] _outBuffer = new byte[OutputBufferSize];
    private readonly byte[] _direntBuffer = new byte[DirentReadBufferSize];
    private readonly HashSet<FileIdentity> _seen = [];
    private readonly ulong _maxDepth;
    private readonly byte _lineTerminator;

    private byte[] _nameArena = new byte[65536];
    private int _nameArenaPos;
    private DirEntry[] _entryStack = new DirEntry[4096];
    private int _entryStackPos;
    private byte[] _pathStack = new byte[8192];
    private int _pathLen;

    private int _outPos;
    private int _openDirCount;
    private ulong _grandTotal;
    private bool _hasError;

    public SequentialWalker(DuOptions options, string programName, Stream stdout, Stream stderr)
    {
        _options = options;
        _programName = programName;
        _stdout = stdout;
        _stderr = stderr;
        _maxDepth = options.Summarize ? 0UL : (options.MaxDepth ?? ulong.MaxValue);
        _lineTerminator = options.NullTerminator ? (byte)'\0' : (byte)'\n';
    }

    public static void RestoreDefaultSigpipe() => NativeIo.RestoreDefaultSigpipe();

    public int Run()
    {
        Span<byte> operandStackBuf = stackalloc byte[512];
        foreach (string operand in _options.Operands)
        {
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

        if (_options.Total)
            EmitLine(_grandTotal, "total"u8);

        FlushOutput();
        return _hasError ? 1 : 0;
    }

    internal int Run(IReadOnlyList<byte[]> rawOperands)
    {
        foreach (byte[] rawBytes in rawOperands)
            ProcessOperand(rawBytes);

        if (_options.Total)
            EmitLine(_grandTotal, "total"u8);

        FlushOutput();
        return _hasError ? 1 : 0;
    }

    private void ProcessOperand(ReadOnlySpan<byte> rawOperand)
    {
        SetNormalizedRootPath(rawOperand);

        byte[]? rentedNullTerm = null;
        Span<byte> nullTermSpan = rawOperand.Length + 1 <= 512
            ? stackalloc byte[512]
            : (rentedNullTerm = ArrayPool<byte>.Shared.Rent(rawOperand.Length + 1));

        try
        {
            rawOperand.CopyTo(nullTermSpan);
            nullTermSpan[rawOperand.Length] = 0;
            ReadOnlySpan<byte> nullTermPath = nullTermSpan[..(rawOperand.Length + 1)];

            if (!NativeIo.TryStatPath(nullTermPath, followSymlinks: false, _statBuffer, out NativeStat stat, out int errno))
            {
                FlushOutput();
                Messages.WritePosixError(_stderr, _programName, "cannot access", CurrentPathSpan, errno);
                _hasError = true;
                return;
            }

            if (!_seen.Add(new FileIdentity(stat.Device, stat.Inode)))
                return;

            if (stat.Kind != NativeEntryKind.Directory)
            {
                ulong bytes = ComputeEntryBytes(in stat);
                _grandTotal += bytes;
                EmitLine(bytes, CurrentPathSpan);
                return;
            }

            OpenRootDirectory(nullTermPath, out int dirFd, out NativeDirectory? fallbackDir, out int openErrno);
            (ulong _, ulong fullBytes) = WalkDirectory(dirFd, fallbackDir, openErrno, in stat, depth: 0);
            _grandTotal += fullBytes;
        }
        finally
        {
            if (rentedNullTerm != null)
                ArrayPool<byte>.Shared.Return(rentedNullTerm);
        }
    }

    private (ulong ReportedToParent, ulong FullBytes) WalkDirectory(
        int dirFd,
        NativeDirectory? fallbackDir,
        int openErrno,
        in NativeStat dirStat,
        ulong depth)
    {
        ulong ownBytes = ComputeEntryBytes(in dirStat);
        ulong directNonDirBytes = 0;
        ulong directSubdirReportedBytes = 0;
        ulong subdirFullBytes = 0;
        ulong childDepth = depth + 1;

        if (openErrno != 0)
        {
            FlushOutput();
            Messages.WritePosixError(_stderr, _programName, "cannot read directory", CurrentPathSpan, openErrno);
            _hasError = true;
            if (depth <= _maxDepth)
                EmitLine(ownBytes, CurrentPathSpan);
            return (ownBytes, ownBytes);
        }

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

            if (count > InodeSortThreshold && NativeIo.ShouldSortDirectoryByInode(dirFd))
                Array.Sort(_entryStack, entryStart, count, DirEntryInodeComparer.Instance);

            for (int i = entryStart; i < entryEnd; i++)
            {
                DirEntry entry = _entryStack[i];
                if (!TryStatArenaEntry(dirFd, entry.NameOffset, out NativeStat childStat, out int statErrno))
                {
                    int savedPath = PushPathSegment(entry.NameOffset, entry.NameLength);
                    FlushOutput();
                    Messages.WritePosixError(_stderr, _programName, "cannot access", CurrentPathSpan, statErrno);
                    _pathLen = savedPath;
                    _hasError = true;
                    continue;
                }

                bool trackIdentity = childStat.Kind == NativeEntryKind.Directory ||
                                     childStat.HardLinks > 1 ||
                                     _options.All;
                if (trackIdentity && !_seen.Add(new FileIdentity(childStat.Device, childStat.Inode)))
                    continue;

                if (childStat.Kind == NativeEntryKind.Directory)
                {
                    int savedPath = PushPathSegment(entry.NameOffset, entry.NameLength);
                    if (deferredSubdirs != null)
                    {
                        deferredSubdirs.Add((CurrentPathSpan.ToArray(), childStat));
                    }
                    else
                    {
                        OpenChildDirectory(dirFd, fallbackDir, entry.NameOffset, entry.NameLength, out int childFd, out NativeDirectory? childFallback, out int subOpenErrno);
                        (ulong subReported, ulong subFull) = WalkDirectory(
                            childFd,
                            childFallback,
                            subOpenErrno,
                            in childStat,
                            childDepth);
                        directSubdirReportedBytes += subReported;
                        subdirFullBytes += subFull;
                    }

                    _pathLen = savedPath;
                }
                else
                {
                    ulong entryBytes = ComputeEntryBytes(in childStat);
                    directNonDirBytes += entryBytes;
                    if (_options.All && childDepth <= _maxDepth)
                    {
                        int savedPath = PushPathSegment(entry.NameOffset, entry.NameLength);
                        EmitLine(entryBytes, CurrentPathSpan);
                        _pathLen = savedPath;
                    }
                }
            }
        }
        finally
        {
            if (fallbackDir != null)
                fallbackDir.Dispose();
            else if (dirFd >= 0)
                _ = Libc.close(dirFd);

            _entryStackPos = savedEntryPos;
            _nameArenaPos = savedArenaPos;
            _openDirCount--;
        }

        if (deferredSubdirs != null)
        {
            int savedPath = _pathLen;
            Span<byte> deferredStackBuf = stackalloc byte[512];
            foreach ((byte[] childPath, NativeStat childStat) in deferredSubdirs)
            {
                SetExactPath(childPath);
                byte[]? rentedNullTerm = null;
                Span<byte> buf = childPath.Length + 1 <= deferredStackBuf.Length
                    ? deferredStackBuf
                    : (rentedNullTerm = ArrayPool<byte>.Shared.Rent(childPath.Length + 1));

                try
                {
                    childPath.CopyTo(buf);
                    buf[childPath.Length] = 0;
                    OpenRootDirectory(buf[..(childPath.Length + 1)], out int subFd, out NativeDirectory? subFallback, out int subOpenErrno);
                    (ulong subReported, ulong subFull) = WalkDirectory(
                        subFd,
                        subFallback,
                        subOpenErrno,
                        in childStat,
                        childDepth);
                    directSubdirReportedBytes += subReported;
                    subdirFullBytes += subFull;
                }
                finally
                {
                    if (rentedNullTerm != null)
                        ArrayPool<byte>.Shared.Return(rentedNullTerm);
                }
            }

            _pathLen = savedPath;
        }

        ulong ownAndFiles = ownBytes + directNonDirBytes;
        ulong reportedTotal = _options.SeparateDirs ? ownAndFiles : ownAndFiles + directSubdirReportedBytes;
        ulong fullTotal = ownAndFiles + subdirFullBytes;

        if (depth <= _maxDepth)
            EmitLine(reportedTotal, CurrentPathSpan);

        return (_options.SeparateDirs ? 0UL : reportedTotal, fullTotal);
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
                    FlushOutput();
                    Messages.WritePosixError(_stderr, _programName, "cannot read directory", CurrentPathSpan, NativeIo.GetLastErrno());
                    _hasError = true;
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
        int order = 0;
        while (dir.MoveNext())
            PushEntry(dir.CurrentInode, order++, dir.CurrentName);

        if (dir.Error != 0)
        {
            FlushOutput();
            Messages.WritePosixError(_stderr, _programName, "cannot read directory", CurrentPathSpan, dir.Error);
            _hasError = true;
        }
    }

    private void PushEntry(ulong inode, int order, ReadOnlySpan<byte> name)
    {
        int requiredArena = _nameArenaPos + name.Length + 1;
        if (requiredArena > _nameArena.Length)
            GrowByteArray(ref _nameArena, requiredArena);

        int nameOffset = _nameArenaPos;
        name.CopyTo(_nameArena.AsSpan(nameOffset));
        _nameArena[nameOffset + name.Length] = 0;
        _nameArenaPos = requiredArena;

        if (_entryStackPos == _entryStack.Length)
            Array.Resize(ref _entryStack, _entryStack.Length * 2);

        _entryStack[_entryStackPos++] = new DirEntry(inode, order, nameOffset, name.Length);
    }

    private bool TryStatArenaEntry(int dirFd, int nameOffset, out NativeStat stat, out int errno)
    {
        fixed (byte* pName = &_nameArena[nameOffset])
        fixed (byte* pStat = _statBuffer)
        {
            return NativeIo.TryStatAt(dirFd, pName, followSymlinks: false, pStat, out stat, out errno);
        }
    }

    private void OpenRootDirectory(
        ReadOnlySpan<byte> nullTermPath,
        out int dirFd,
        out NativeDirectory? fallbackDir,
        out int errno)
    {
        if (NativeIo.UsesGetdents64)
        {
            fixed (byte* pPath = nullTermPath)
            {
                errno = NativeIo.OpenRawDirectoryFd(pPath, out dirFd);
            }

            fallbackDir = null;
            return;
        }

        fallbackDir = NativeDirectory.Open(nullTermPath, out errno);
        dirFd = fallbackDir?.Fd ?? -1;
    }

    private void OpenChildDirectory(
        int parentFd,
        NativeDirectory? parentFallback,
        int nameOffset,
        int nameLength,
        out int childFd,
        out NativeDirectory? childFallback,
        out int errno)
    {
        if (parentFallback == null)
        {
            fixed (byte* pName = &_nameArena[nameOffset])
            {
                errno = NativeIo.OpenRawDirectoryFdRelative(parentFd, pName, out childFd);
            }

            childFallback = null;
            return;
        }

        childFallback = NativeDirectory.OpenRelative(parentFd, _nameArena.AsSpan(nameOffset, nameLength + 1), out errno);
        childFd = childFallback?.Fd ?? -1;
    }

    private ReadOnlySpan<byte> CurrentPathSpan => _pathStack.AsSpan(0, _pathLen);

    private void SetExactPath(ReadOnlySpan<byte> path)
    {
        if (path.Length > _pathStack.Length)
            GrowByteArray(ref _pathStack, path.Length);

        path.CopyTo(_pathStack);
        _pathLen = path.Length;
    }

    private void SetNormalizedRootPath(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
        {
            _pathLen = 0;
            return;
        }

        bool hadTrailingSlash = raw[^1] == (byte)'/';
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == (byte)'/')
            end--;

        if (end == 0)
        {
            _pathStack[0] = (byte)'/';
            _pathLen = 1;
            return;
        }

        int targetLen = hadTrailingSlash ? end + 1 : raw.Length;
        if (targetLen > _pathStack.Length)
            GrowByteArray(ref _pathStack, targetLen);

        if (hadTrailingSlash)
        {
            raw[..end].CopyTo(_pathStack);
            _pathStack[end] = (byte)'/';
        }
        else
        {
            raw.CopyTo(_pathStack);
        }

        _pathLen = targetLen;
    }

    private int PushPathSegment(int nameOffset, int nameLength)
    {
        int saved = _pathLen;
        bool needsSlash = _pathLen > 0 && _pathStack[_pathLen - 1] != (byte)'/';
        int newLen = _pathLen + (needsSlash ? 1 : 0) + nameLength;
        if (newLen > _pathStack.Length)
            GrowByteArray(ref _pathStack, newLen);

        int pos = _pathLen;
        if (needsSlash)
            _pathStack[pos++] = (byte)'/';

        _nameArena.AsSpan(nameOffset, nameLength).CopyTo(_pathStack.AsSpan(pos));
        _pathLen = newLen;
        return saved;
    }

    private static void GrowByteArray(ref byte[] buffer, int minLength)
    {
        int newSize = buffer.Length * 2;
        if (newSize < minLength)
            newSize = minLength;
        Array.Resize(ref buffer, newSize);
    }

    private ulong ComputeEntryBytes(in NativeStat stat)
    {
        if (_options.ApparentSize)
            return stat.Kind == NativeEntryKind.Directory || stat.Size <= 0 ? 0UL : (ulong)stat.Size;

        return stat.Blocks > 0 ? (ulong)stat.Blocks * 512UL : 0UL;
    }

    private void EmitLine(ulong bytes, ReadOnlySpan<byte> pathBytes)
    {
        Span<byte> sizeBuf = stackalloc byte[64];
        if (!HumanFormat.TryFormatBytes(bytes, _options.BlockSizeSpec, sizeBuf, out int sizeLen))
            return;

        int totalLen = sizeLen + 1 + pathBytes.Length + 1;
        if (totalLen > _outBuffer.Length)
        {
            FlushOutput();
            byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen);
            try
            {
                int pos = WriteFormattedLine(rented, sizeBuf[..sizeLen], pathBytes);
                _stdout.Write(rented, 0, pos);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            return;
        }

        if (_outPos + totalLen > _outBuffer.Length)
            FlushOutput();

        _outPos += WriteFormattedLine(_outBuffer.AsSpan(_outPos), sizeBuf[..sizeLen], pathBytes);
    }

    private int WriteFormattedLine(Span<byte> dest, ReadOnlySpan<byte> sizeBytes, ReadOnlySpan<byte> pathBytes)
    {
        sizeBytes.CopyTo(dest);
        int pos = sizeBytes.Length;
        dest[pos++] = (byte)'\t';
        pathBytes.CopyTo(dest[pos..]);
        pos += pathBytes.Length;
        dest[pos++] = _lineTerminator;
        return pos;
    }

    private void FlushOutput()
    {
        if (_outPos > 0)
        {
            _stdout.Write(_outBuffer, 0, _outPos);
            _outPos = 0;
        }

        _stdout.Flush();
    }

    internal static byte[] NormalizeDisplayPath(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
            return [];

        bool hadTrailingSlash = raw[^1] == (byte)'/';
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == (byte)'/')
            end--;

        if (end == 0)
            return [(byte)'/'];

        if (!hadTrailingSlash)
            return raw.ToArray();

        byte[] normalized = new byte[end + 1];
        raw[..end].CopyTo(normalized);
        normalized[end] = (byte)'/';
        return normalized;
    }
}
