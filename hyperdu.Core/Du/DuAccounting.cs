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
    private static readonly int SegmentPathLimit = OperatingSystem.IsMacOS() ? 900 : 3800;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ComputeEntryValue(in NativeStat stat, DuOptions options)
    {
        if (options.Inodes)
            return 1UL;

        if (options.ApparentSize)
            return stat.Kind == NativeEntryKind.Directory || stat.Size <= 0 ? 0UL : (ulong)stat.Size;

        return stat.Blocks > 0 ? (ulong)stat.Blocks * 512UL : 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldEmitForThreshold(ulong value, DuOptions options)
    {
        if (options.Threshold is not ThresholdSpec t)
            return true;

        return t.IsNegative ? value <= t.Value : value >= t.Value;
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
        bool followSymlinks,
        out int dirFd,
        out NativeDirectory? fallbackDir,
        out int errno)
    {
        int pathLen = nullTermPath.Length > 0 && nullTermPath[^1] == 0
            ? nullTermPath.Length - 1
            : nullTermPath.Length;

        if (followSymlinks || pathLen < SegmentPathLimit)
        {
            OpenRootDirectory(nullTermPath, out dirFd, out fallbackDir, out errno);
            return;
        }

        OpenDirectoryBeyondPathMax(nullTermPath[..pathLen], out dirFd, out fallbackDir, out errno);
    }

    private static void OpenDirectoryBeyondPathMax(
        ReadOnlySpan<byte> path,
        out int dirFd,
        out NativeDirectory? fallbackDir,
        out int errno)
    {
        byte[] segBuf = ArrayPool<byte>.Shared.Rent(SegmentPathLimit + 256);
        int curFd = -1;
        try
        {
            int pos = 0;
            bool first = true;
            while (pos < path.Length)
            {
                if (!first)
                {
                    while (pos < path.Length && path[pos] == (byte)'/')
                        pos++;
                    if (pos >= path.Length)
                        break;
                }

                int maxEnd = Math.Min(path.Length, pos + SegmentPathLimit);
                int chunkEnd = maxEnd;
                if (maxEnd < path.Length)
                {
                    int slash = path[pos..maxEnd].LastIndexOf((byte)'/');
                    if (slash > 0)
                        chunkEnd = pos + slash;
                    else
                    {
                        int nextSlash = path[pos..].IndexOf((byte)'/');
                        chunkEnd = nextSlash >= 0 ? pos + nextSlash : path.Length;
                    }
                }

                int chunkLen = chunkEnd - pos;
                if (chunkLen == 0)
                    break;

                if (chunkLen + 1 > segBuf.Length)
                {
                    byte[] grown = ArrayPool<byte>.Shared.Rent(chunkLen + 1);
                    ArrayPool<byte>.Shared.Return(segBuf);
                    segBuf = grown;
                }

                path.Slice(pos, chunkLen).CopyTo(segBuf);
                segBuf[chunkLen] = 0;

                int nextFd;
                fixed (byte* pSeg = segBuf)
                {
                    errno = first
                        ? NativeIo.OpenRawDirectoryFd(pSeg, out nextFd)
                        : NativeIo.OpenRawDirectoryFdRelative(curFd, pSeg, out nextFd);
                }

                if (curFd >= 0)
                    _ = Libc.close(curFd);

                if (errno != 0)
                {
                    dirFd = -1;
                    fallbackDir = null;
                    return;
                }

                curFd = nextFd;
                pos = chunkEnd;
                first = false;
            }

            if (curFd < 0)
            {
                OpenRootDirectory("/\0"u8, out dirFd, out fallbackDir, out errno);
                return;
            }

            if (NativeIo.UsesGetdents64)
            {
                dirFd = curFd;
                fallbackDir = null;
                errno = 0;
                return;
            }

            fallbackDir = NativeDirectory.WrapFd(curFd, out errno);
            if (errno != 0)
            {
                _ = Libc.close(curFd);
                dirFd = -1;
                fallbackDir = null;
                return;
            }

            dirFd = fallbackDir?.Fd ?? -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(segBuf);
        }
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
        if (!HumanFormat.TryFormatBytes(bytes, _options.BlockSizeSpec, in _numericLocale, sizeBuf, out int sizeLen))
            return;

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
