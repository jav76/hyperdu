using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

internal readonly record struct FileIdentity(ulong Device, ulong Inode);

internal readonly record struct DirWalkResult(
    ulong ReportedToParent,
    ulong FullBytes,
    FileTimestamp ReportedTime,
    FileTimestamp FullTime);

internal static unsafe class DuAccounting
{
    internal const int DirentReadBufferSize = 65536;
    internal const int InodeSortThreshold = 10000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ComputeEntryValue(in NativeStat stat, DuOptions options)
    {
        if (options.Dialect == DuDialect.Bsd)
        {
            ulong raw = (ulong)Math.Max(0L, options.ApparentSize ? stat.Size : stat.Blocks);
            ulong div = options.BsdCountDivisor;
            return div <= 1UL ? raw : (raw + div - 1) / div;
        }

        if (options.Inodes)
            return 1UL;

        return (ulong)NativeIo.ComputeEntryByteSize(in stat, options.ApparentSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldEmitForThreshold(ulong value, DuOptions options, bool isDirectory = true)
    {
        if (options.Threshold is not ThresholdSpec t)
            return true;

        if (options.Dialect == DuDialect.Bsd)
            return !isDirectory || t.Matches(options.ComputeBsdDisplayBlocks(value));

        return t.Matches(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldIgnoreBsdEntry(
        in NativeStat stat,
        DuOptions options,
        ReadOnlySpan<byte> nullTermPath,
        ReadOnlySpan<byte> entryName)
    {
        if (options.Dialect != DuDialect.Bsd)
            return false;

        if (options.IgnoreNoDump && stat.HasNoDumpFlag)
            return true;

        return stat.Kind == NativeEntryKind.Directory && NativeIo.IsMacOsDevFdDirectory(nullTermPath, entryName);
    }

    internal static ReadOnlySpan<byte> ExtractBasename(ReadOnlySpan<byte> path)
    {
        int slash = path.LastIndexOf((byte)'/');
        // Mirrors FreeBSD fts_load: keeps "/" as "/", strips prefix when '/' has trailing text or is non-leading.
        return slash >= 0 && (slash > 0 || slash + 1 < path.Length)
            ? path[(slash + 1)..]
            : path;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldTrackOperandIdentity(in NativeStat stat, DuOptions options) =>
        options.Dialect == DuDialect.Gnu
        || (stat.Kind != NativeEntryKind.Directory && stat.HardLinks > 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldTrackChildIdentity(
        in NativeStat stat,
        DuOptions options,
        bool followChild,
        bool trackSingleLinkFiles) =>
        options.Dialect == DuDialect.Bsd
            ? (stat.Kind != NativeEntryKind.Directory && stat.HardLinks > 1)
            : (stat.Kind == NativeEntryKind.Directory ||
               stat.HardLinks > 1 ||
               followChild ||
               trackSingleLinkFiles);

    internal static bool ShouldExcludeChild(
        DuOptions options,
        ReadOnlySpan<byte> parentDisplayPath,
        ReadOnlySpan<byte> childName,
        ref byte[] pathScratch)
    {
        if (options.Excludes.MatchBasenameOnly)
            return options.Excludes.IsExcludedChildBasename(childName);

        int len = WriteChildPathInto(parentDisplayPath, childName, ref pathScratch);
        return options.Excludes.IsExcluded(pathScratch.AsSpan(0, len));
    }

    internal static bool ShouldIgnoreBsdChild(
        in NativeStat stat,
        DuOptions options,
        ReadOnlySpan<byte> parentDisplayPath,
        ReadOnlySpan<byte> childName,
        ref byte[] pathScratch)
    {
        if (options.Dialect != DuDialect.Bsd)
            return false;

        if (options.IgnoreNoDump && stat.HasNoDumpFlag)
            return true;

        if (stat.Kind == NativeEntryKind.Directory && NativeIo.IsMacOS && childName.SequenceEqual("fd"u8))
        {
            int len = WriteChildPathInto(parentDisplayPath, childName, ref pathScratch, appendNul: true);
            return NativeIo.IsMacOsDevFdDirectory(pathScratch.AsSpan(0, len + 1), childName);
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FileTimestamp ExtractTimestamp(in NativeStat stat, DuOptions options)
    {
        if (!options.ShowTime)
            return default;

        return options.TimeKind switch
        {
            TimeKind.Atime => new FileTimestamp(stat.AtimeSec, stat.AtimeNsec),
            TimeKind.Ctime => new FileTimestamp(stat.CtimeSec, stat.CtimeNsec),
            _ => new FileTimestamp(stat.MtimeSec, stat.MtimeNsec)
        };
    }

    internal static void AccumulateSubdirTime(
        in DirWalkResult sub,
        bool separateDirs,
        bool showTime,
        ref bool hasSubdirReportedTime,
        ref FileTimestamp subdirReportedMaxTime,
        ref bool hasSubdirFullTime,
        ref FileTimestamp subdirFullMaxTime)
    {
        if (!showTime)
            return;

        if (!separateDirs)
        {
            subdirReportedMaxTime = hasSubdirReportedTime
                ? FileTimestamp.Max(subdirReportedMaxTime, sub.ReportedTime)
                : sub.ReportedTime;
            hasSubdirReportedTime = true;
        }

        subdirFullMaxTime = hasSubdirFullTime
            ? FileTimestamp.Max(subdirFullMaxTime, sub.FullTime)
            : sub.FullTime;
        hasSubdirFullTime = true;
    }

    internal static int NormalizeDanglingSymlinkErrno(ReadOnlySpan<byte> nullTermPath, int statErrno, byte* statBuffer)
    {
        if (statErrno == Libc.ENOENT)
        {
            fixed (byte* pPath = nullTermPath)
            {
                if (NativeIo.TryStatAt(
                        NativeIo.IsMacOS ? Libc.AT_FDCWD_MACOS : Libc.AT_FDCWD_LINUX,
                        pPath,
                        followSymlinks: false,
                        statBuffer,
                        out NativeStat lstatStat,
                        out _) &&
                    lstatStat.Kind == NativeEntryKind.Symlink)
                {
                    return 0;
                }
            }
        }

        return statErrno;
    }

    internal static bool TryStatPathWithPolicy(
        ReadOnlySpan<byte> nullTermPath,
        bool followSymlinks,
        DuOptions options,
        byte* statBuffer,
        out NativeStat stat,
        out int errno)
    {
        if (followSymlinks)
            return TryStatFollowed(nullTermPath, options, statBuffer, out stat, out errno);

        return NativeIo.TryStatPath(nullTermPath, followSymlinks: false, new Span<byte>(statBuffer, NativeIo.StatBufferSize), out stat, out errno);
    }

    internal static bool TryStatChildEntry(
        int dirFd,
        ReadOnlySpan<byte> parentDisplayPath,
        ReadOnlySpan<byte> childName,
        byte* nullTermChildNamePtr,
        bool followChild,
        DuOptions options,
        byte* statBuffer,
        ref byte[] pathScratch,
        out NativeStat childStat,
        out int statErrno)
    {
        if (followChild)
        {
            int len = WriteChildPathInto(parentDisplayPath, childName, ref pathScratch, appendNul: true);
            return TryStatFollowed(pathScratch.AsSpan(0, len + 1), options, statBuffer, out childStat, out statErrno);
        }

        return NativeIo.TryStatAt(dirFd, nullTermChildNamePtr, followSymlinks: false, statBuffer, out childStat, out statErrno);
    }

    internal static bool TryStatFollowed(
        ReadOnlySpan<byte> nullTermPath,
        DuOptions options,
        byte* statBuffer,
        out NativeStat stat,
        out int errno)
    {
        Span<byte> statSpan = new(statBuffer, NativeIo.StatBufferSize);
        if (NativeIo.TryStatPath(nullTermPath, followSymlinks: true, statSpan, out stat, out errno))
            return true;

        if (options.Dialect == DuDialect.Bsd)
        {
            if (errno != NativeIo.EloopErrno &&
                NativeIo.TryStatPath(nullTermPath, followSymlinks: false, statSpan, out NativeStat lstatStat, out _) &&
                lstatStat.Kind == NativeEntryKind.Symlink)
            {
                stat = lstatStat;
                errno = 0;
                return true;
            }

            return false;
        }

        errno = NormalizeDanglingSymlinkErrno(nullTermPath, errno, statBuffer);
        return false;
    }

    internal static void OpenRootDirectory(
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

    /// <summary>
    /// Opens a directory whose null-terminated full path may exceed PATH_MAX by stepping
    /// through openat(fd, relativeSegmentChunk) and closing intermediate file descriptors.
    /// </summary>
    internal static void OpenDirectoryPathSafe(
        ReadOnlySpan<byte> nullTermPath,
        out int dirFd,
        out NativeDirectory? fallbackDir,
        out int errno)
    {
        if (!NativeIo.TryOpenDirectoryFdPathSafe(nullTermPath, out int fd, out errno))
        {
            dirFd = -1;
            fallbackDir = null;
            return;
        }

        if (NativeIo.UsesGetdents64)
        {
            dirFd = fd;
            fallbackDir = null;
            return;
        }

        // WrapFd closes fd when fdopendir fails.
        fallbackDir = NativeDirectory.WrapFd(fd, out errno);
        dirFd = fallbackDir?.Fd ?? -1;
    }

    internal static void OpenChildDirectory(
        int parentFd,
        NativeDirectory? parentFallback,
        byte[] nameArena,
        int nameOffset,
        int nameLength,
        out int childFd,
        out NativeDirectory? childFallback,
        out int errno)
    {
        if (parentFallback == null)
        {
            fixed (byte* pName = &nameArena[nameOffset])
            {
                errno = NativeIo.OpenRawDirectoryFdRelative(parentFd, pName, out childFd);
            }

            childFallback = null;
            return;
        }

        childFallback = NativeDirectory.OpenRelative(parentFd, nameArena.AsSpan(nameOffset, nameLength + 1), out errno);
        childFd = childFallback?.Fd ?? -1;
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

    internal static byte[] BuildNullTerminatedChildPath(ReadOnlySpan<byte> parentPath, ReadOnlySpan<byte> childName)
    {
        bool needsSlash = parentPath.Length > 0 && parentPath[^1] != (byte)'/';
        int len = parentPath.Length + (needsSlash ? 1 : 0) + childName.Length;
        byte[] result = new byte[len + 1];
        parentPath.CopyTo(result);
        int pos = parentPath.Length;
        if (needsSlash)
            result[pos++] = (byte)'/';
        childName.CopyTo(result.AsSpan(pos));
        result[len] = 0;
        return result;
    }

    internal static int WriteChildPathInto(
        ReadOnlySpan<byte> parentPath,
        ReadOnlySpan<byte> childName,
        ref byte[] scratchBuffer,
        bool appendNul = false)
    {
        bool needsSlash = parentPath.Length > 0 && parentPath[^1] != (byte)'/';
        int displayLen = parentPath.Length + (needsSlash ? 1 : 0) + childName.Length;
        int totalLen = displayLen + (appendNul ? 1 : 0);
        if (totalLen > scratchBuffer.Length)
            GrowByteArray(ref scratchBuffer, totalLen);

        parentPath.CopyTo(scratchBuffer);
        int pos = parentPath.Length;
        if (needsSlash)
            scratchBuffer[pos++] = (byte)'/';
        childName.CopyTo(scratchBuffer.AsSpan(pos));
        if (appendNul)
            scratchBuffer[displayLen] = 0;
        return displayLen;
    }

    internal static int WriteChildPathIntoPooled(
        ReadOnlySpan<byte> parentPath,
        ReadOnlySpan<byte> childName,
        ref byte[] pooledScratch,
        bool appendNul = false)
    {
        bool needsSlash = parentPath.Length > 0 && parentPath[^1] != (byte)'/';
        int displayLen = parentPath.Length + (needsSlash ? 1 : 0) + childName.Length;
        int totalLen = displayLen + (appendNul ? 1 : 0);
        if (totalLen > pooledScratch.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(totalLen);
            ArrayPool<byte>.Shared.Return(pooledScratch);
            pooledScratch = grown;
        }

        parentPath.CopyTo(pooledScratch);
        int pos = parentPath.Length;
        if (needsSlash)
            pooledScratch[pos++] = (byte)'/';
        childName.CopyTo(pooledScratch.AsSpan(pos));
        if (appendNul)
            pooledScratch[displayLen] = 0;
        return displayLen;
    }

    internal static void GrowByteArray(ref byte[] buffer, int minLength)
    {
        int newSize = buffer.Length * 2;
        if (newSize < minLength)
            newSize = minLength;
        Array.Resize(ref buffer, newSize);
    }
}

internal sealed class DuOutputWriter : IDisposable
{
    private const int OutputBufferSize = 65536;
    private const int InitialFlushThreshold = 4096;
    private const int InitialFlushWarmupBytes = 16384;

    private readonly DuOptions _options;
    private readonly string _programName;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly byte[] _outBuffer = new byte[OutputBufferSize];
    private readonly byte _lineTerminator;
    private readonly NumericLocaleFormat _numericLocale;
    private TimeFormat.Formatter? _timeFormatter;
    private int _outPos;
    private long _totalFlushedBytes;

    public bool HasError { get; set; }

    public DuOutputWriter(DuOptions options, string programName, Stream stdout, Stream stderr)
    {
        _options = options;
        _programName = programName;
        _stdout = stdout;
        _stderr = stderr;
        _lineTerminator = options.NullTerminator ? (byte)'\0' : (byte)'\n';
        _numericLocale = Messages.GetNumericLocaleFormat();
        _timeFormatter = options.ShowTime
            ? TimeFormat.CreateFormatter(options, programName, stderr, Flush)
            : null;
    }

    public void EmitLine(ulong bytes, in FileTimestamp time, ReadOnlySpan<byte> pathBytes)
    {
        Span<byte> sizeBuf = stackalloc byte[128];
        int sizeLen;
        if (_options.Dialect == DuDialect.Bsd)
        {
            if (_options.BlockSizeSpec.Mode is HumanMode.BsdBinary or HumanMode.BsdSi)
            {
                UInt128 rawBytes = (UInt128)bytes * _options.BsdCountBytesPerUnit;
                ulong totalBytes = rawBytes > ulong.MaxValue ? ulong.MaxValue : (ulong)rawBytes;
                if (!HumanFormat.TryFormatBytes(totalBytes, _options.BlockSizeSpec, in _numericLocale, sizeBuf, out sizeLen))
                    return;
            }
            else
            {
                ulong blocks = _options.ComputeBsdDisplayBlocks(bytes);
                if (!HumanFormat.TryFormatBytes(blocks, BlockSizeSpec.Bytes, in _numericLocale, sizeBuf, out sizeLen))
                    return;
            }
        }
        else if (!HumanFormat.TryFormatBytes(bytes, _options.BlockSizeSpec, in _numericLocale, sizeBuf, out sizeLen))
        {
            return;
        }

        ReadOnlySpan<byte> timeBytes = _timeFormatter != null ? _timeFormatter.Format(in time) : [];
        bool includeTime = _timeFormatter != null;
        int totalLen = sizeLen + 1 + (includeTime ? timeBytes.Length + 1 : 0) + pathBytes.Length + 1;
        if (totalLen > _outBuffer.Length)
        {
            Flush();
            byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen);
            try
            {
                int pos = WriteFormattedLine(rented, sizeBuf[..sizeLen], includeTime, timeBytes, pathBytes);
                _stdout.Write(rented, 0, pos);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            return;
        }

        int activeLimit = _totalFlushedBytes < InitialFlushWarmupBytes ? InitialFlushThreshold : _outBuffer.Length;
        if (_outPos + totalLen > activeLimit)
            Flush();

        _outPos += WriteFormattedLine(_outBuffer.AsSpan(_outPos), sizeBuf[..sizeLen], includeTime, timeBytes, pathBytes);
    }

    public void WritePosixError(string action, ReadOnlySpan<byte> path, int errno)
    {
        Flush();
        if (_options.Dialect == DuDialect.Bsd)
            Messages.WriteBsdPosixError(_stderr, _programName, path, errno);
        else
            Messages.WritePosixError(_stderr, _programName, action, path, errno);
        HasError = true;
    }

    public void Flush()
    {
        if (_outPos > 0)
        {
            _stdout.Write(_outBuffer, 0, _outPos);
            _totalFlushedBytes += _outPos;
            _outPos = 0;
        }

        _stdout.Flush();
    }

    private int WriteFormattedLine(
        Span<byte> dest,
        ReadOnlySpan<byte> sizeBytes,
        bool includeTime,
        ReadOnlySpan<byte> timeBytes,
        ReadOnlySpan<byte> pathBytes)
    {
        sizeBytes.CopyTo(dest);
        int pos = sizeBytes.Length;
        dest[pos++] = (byte)'\t';
        if (includeTime)
        {
            timeBytes.CopyTo(dest[pos..]);
            pos += timeBytes.Length;
            dest[pos++] = (byte)'\t';
        }

        pathBytes.CopyTo(dest[pos..]);
        pos += pathBytes.Length;
        dest[pos++] = _lineTerminator;
        return pos;
    }

    public void Dispose()
    {
        _timeFormatter?.Dispose();
        _timeFormatter = null;
    }
}
