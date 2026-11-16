using System.Buffers;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

/// <summary>
/// Sequential post-order directory walker providing exact GNU du Phase 1 accounting,
/// raw byte path handling, trailing slash preservation, and hard-link deduplication.
/// </summary>
public sealed class SequentialWalker
{
    private readonly record struct FileIdentity(ulong Device, ulong Inode);

    private const int OutputBufferSize = 65536;
    private static readonly int OpenFdSafetyCap = OperatingSystem.IsMacOS() ? 200 : 850;

    private readonly DuOptions _options;
    private readonly string _programName;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly byte[] _statBuffer = new byte[NativeIo.StatBufferSize];
    private readonly byte[] _outBuffer = new byte[OutputBufferSize];
    private readonly HashSet<FileIdentity> _seen = [];
    private readonly ulong _maxDepth;
    private readonly byte _lineTerminator;

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
        foreach (string operand in _options.Operands)
        {
            byte[] rawBytes = Encoding.UTF8.GetBytes(operand);
            ProcessOperand(rawBytes);
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

    private void ProcessOperand(byte[] rawOperand)
    {
        byte[] displayPath = NormalizeDisplayPath(rawOperand);
        byte[] nullTermPath = NullTerminate(rawOperand);

        if (!NativeIo.TryStatPath(nullTermPath, followSymlinks: false, _statBuffer, out NativeStat stat, out int errno))
        {
            FlushOutput();
            Messages.WritePosixError(_stderr, _programName, "cannot access", displayPath, errno);
            _hasError = true;
            return;
        }

        if (!_seen.Add(new FileIdentity(stat.Device, stat.Inode)))
            return;

        if (stat.Kind != NativeEntryKind.Directory)
        {
            ulong bytes = ComputeEntryBytes(in stat);
            _grandTotal += bytes;
            EmitLine(bytes, displayPath);
            return;
        }

        using NativeDirectory? rootDir = NativeDirectory.Open(nullTermPath, out int openErrno);
        (ulong _, ulong fullBytes) = WalkDirectory(rootDir, openErrno, displayPath, in stat, depth: 0);
        _grandTotal += fullBytes;
    }

    private (ulong ReportedToParent, ulong FullBytes) WalkDirectory(
        NativeDirectory? dir,
        int openErrno,
        byte[] displayPath,
        in NativeStat dirStat,
        ulong depth)
    {
        ulong ownBytes = ComputeEntryBytes(in dirStat);
        ulong directNonDirBytes = 0;
        ulong directSubdirReportedBytes = 0;
        ulong subdirFullBytes = 0;
        ulong childDepth = depth + 1;

        if (dir == null)
        {
            FlushOutput();
            Messages.WritePosixError(_stderr, _programName, "cannot read directory", displayPath, openErrno);
            _hasError = true;
            if (depth <= _maxDepth)
                EmitLine(ownBytes, displayPath);
            return (ownBytes, ownBytes);
        }

        // Keep open FDs bounded so deeply nested trees cannot hit RLIMIT_NOFILE.
        _openDirCount++;
        List<(byte[] Name, byte[] ChildPath, NativeStat Stat)>? deferredSubdirs =
            _openDirCount >= OpenFdSafetyCap ? [] : null;

        try
        {
            while (dir.MoveNext())
            {
                ReadOnlySpan<byte> entryName = dir.CurrentName;
                byte[] childPath = JoinPath(displayPath, entryName);

                if (!dir.TryStatCurrent(followSymlinks: false, _statBuffer, out NativeStat childStat, out int statErrno))
                {
                    FlushOutput();
                    Messages.WritePosixError(_stderr, _programName, "cannot access", childPath, statErrno);
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
                    if (deferredSubdirs != null)
                    {
                        byte[] nameNullTerm = NullTerminate(entryName);
                        deferredSubdirs.Add((nameNullTerm, childPath, childStat));
                    }
                    else
                    {
                        using NativeDirectory? subDir = dir.OpenCurrentDirectory(out int subOpenErrno);
                        (ulong subReported, ulong subFull) = WalkDirectory(
                            subDir,
                            subOpenErrno,
                            childPath,
                            in childStat,
                            childDepth);
                        directSubdirReportedBytes += subReported;
                        subdirFullBytes += subFull;
                    }
                }
                else
                {
                    ulong entryBytes = ComputeEntryBytes(in childStat);
                    directNonDirBytes += entryBytes;
                    if (_options.All && childDepth <= _maxDepth)
                        EmitLine(entryBytes, childPath);
                }
            }

            if (dir.Error != 0)
            {
                FlushOutput();
                Messages.WritePosixError(_stderr, _programName, "cannot read directory", displayPath, dir.Error);
                _hasError = true;
            }
        }
        finally
        {
            dir.Dispose();
            _openDirCount--;
        }

        if (deferredSubdirs != null)
        {
            foreach ((byte[] _, byte[] childPath, NativeStat childStat) in deferredSubdirs)
            {
                byte[] pathNullTerm = NullTerminate(childPath);
                using NativeDirectory? subDir = NativeDirectory.Open(pathNullTerm, out int subOpenErrno);
                (ulong subReported, ulong subFull) = WalkDirectory(
                    subDir,
                    subOpenErrno,
                    childPath,
                    in childStat,
                    childDepth);
                directSubdirReportedBytes += subReported;
                subdirFullBytes += subFull;
            }
        }

        ulong ownAndFiles = ownBytes + directNonDirBytes;
        ulong reportedTotal = _options.SeparateDirs ? ownAndFiles : ownAndFiles + directSubdirReportedBytes;
        ulong fullTotal = ownAndFiles + subdirFullBytes;

        if (depth <= _maxDepth)
            EmitLine(reportedTotal, displayPath);

        return (_options.SeparateDirs ? 0UL : reportedTotal, fullTotal);
    }

    private ulong ComputeEntryBytes(in NativeStat stat)
    {
        if (_options.ApparentSize)
            return stat.Kind == NativeEntryKind.Directory || stat.Size <= 0 ? 0UL : (ulong)stat.Size;

        return stat.Blocks > 0 ? (ulong)stat.Blocks * 512UL : 0UL;
    }

    private void EmitLine(ulong bytes, ReadOnlySpan<byte> pathBytes)
    {
        string formatted = HumanFormat.Format(bytes, _options.BlockSizeSpec);
        int sizeLen = Encoding.ASCII.GetByteCount(formatted);
        int totalLen = sizeLen + 1 + pathBytes.Length + 1;

        if (totalLen > _outBuffer.Length)
        {
            FlushOutput();
            byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen);
            try
            {
                int pos = WriteFormattedLine(rented, formatted, pathBytes);
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

        _outPos += WriteFormattedLine(_outBuffer.AsSpan(_outPos), formatted, pathBytes);
    }

    private int WriteFormattedLine(Span<byte> dest, string formattedSize, ReadOnlySpan<byte> pathBytes)
    {
        int pos = Encoding.ASCII.GetBytes(formattedSize, dest);
        dest[pos++] = (byte)'\t';
        pathBytes.CopyTo(dest.Slice(pos));
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
        raw.Slice(0, end).CopyTo(normalized);
        normalized[end] = (byte)'/';
        return normalized;
    }

    private static byte[] JoinPath(ReadOnlySpan<byte> parent, ReadOnlySpan<byte> child)
    {
        if (parent.IsEmpty)
            return child.ToArray();

        bool needsSlash = parent[^1] != (byte)'/';
        byte[] joined = new byte[parent.Length + (needsSlash ? 1 : 0) + child.Length];
        parent.CopyTo(joined);
        int offset = parent.Length;
        if (needsSlash)
            joined[offset++] = (byte)'/';
        child.CopyTo(joined.AsSpan(offset));
        return joined;
    }

    private static byte[] NullTerminate(ReadOnlySpan<byte> bytes)
    {
        byte[] buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer);
        buffer[bytes.Length] = 0;
        return buffer;
    }
}
