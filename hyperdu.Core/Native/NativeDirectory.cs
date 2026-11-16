using System.Buffers;
using System.Runtime.InteropServices;

namespace hyperdu.Core.Native;

/// <summary>
/// One open directory stream. Entry names are raw bytes and stay valid only until the next <see cref="MoveNext"/>.
/// Uses raw getdents64 on Linux (no fdopendir/readdir overhead) and fdopendir/readdir on macOS or fallback platforms.
/// </summary>
internal sealed unsafe class NativeDirectory : IDisposable
{
    private const int GetdentsBufferSize = 32768;

    private readonly bool _useGetdents64;
    private int _fd;
    private nint _dir;
    private byte* _entry;
    private byte[]? _buf;
    private int _bufPos;
    private int _bufLen;
    private int _entryOffset;
    private int _nameLength;

    private NativeDirectory(nint dir, int fd, bool useGetdents64)
    {
        _dir = dir;
        _fd = fd;
        _useGetdents64 = useGetdents64;
    }

    /// <summary>The directory's file descriptor, for *at() calls relative to it.</summary>
    public int Fd => _fd;

    /// <summary>The errno that ended enumeration early, or 0 when the end was reached normally.</summary>
    public int Error { get; private set; }

    public ReadOnlySpan<byte> CurrentName => _useGetdents64
        ? new(_buf!, _entryOffset + NativeIo.DirentNameOffset, _nameLength)
        : new(_entry + NativeIo.DirentNameOffset, _nameLength);

    public ulong CurrentInode
    {
        get
        {
            if (_useGetdents64)
            {
                fixed (byte* p = &_buf![_entryOffset])
                    return *(ulong*)p;
            }

            return *(ulong*)_entry;
        }
    }

    public NativeEntryKind CurrentKind => _useGetdents64
        ? NativeIo.KindFromDirentType(_buf![_entryOffset + NativeIo.DirentTypeOffset])
        : NativeIo.KindFromDirentType(_entry[NativeIo.DirentTypeOffset]);

    /// <summary>Opens <paramref name="path"/> (null-terminated). Returns null and sets <paramref name="error"/> on failure.</summary>
    public static NativeDirectory? Open(ReadOnlySpan<byte> path, out int error)
    {
        if (path.IsEmpty || path[^1] != 0)
            throw new ArgumentException("Path must be null-terminated.", nameof(path));

        nint dir;
        int fd;
        fixed (byte* p = path)
        {
            error = NativeIo.OpenDirectory(p, out dir, out fd);
        }

        return error == 0 ? new NativeDirectory(dir, fd, dir == 0) : null;
    }

    internal static NativeDirectory? WrapFd(int fd, out int error)
    {
        error = NativeIo.WrapOpenDirectoryFd(fd, out nint dir);
        return error == 0 ? new NativeDirectory(dir, fd, dir == 0) : null;
    }

    /// <summary>Opens <paramref name="name"/> (null-terminated) relative to <paramref name="parentFd"/>.</summary>
    public static NativeDirectory? OpenRelative(int parentFd, ReadOnlySpan<byte> name, out int error)
    {
        if (name.IsEmpty || name[^1] != 0)
            throw new ArgumentException("Name must be null-terminated.", nameof(name));

        nint dir;
        int fd;
        fixed (byte* p = name)
        {
            error = NativeIo.OpenDirectoryRelative(parentFd, p, out dir, out fd);
        }

        return error == 0 ? new NativeDirectory(dir, fd, dir == 0) : null;
    }

    /// <summary>Opens a child directory relative to the current directory stream.</summary>
    public NativeDirectory? OpenCurrentDirectory(out int error)
    {
        nint dir;
        int fd;
        if (_useGetdents64)
        {
            fixed (byte* pName = &_buf![_entryOffset + NativeIo.DirentNameOffset])
            {
                error = NativeIo.OpenDirectoryRelative(_fd, pName, out dir, out fd);
            }
        }
        else
        {
            error = NativeIo.OpenDirectoryRelative(_fd, _entry + NativeIo.DirentNameOffset, out dir, out fd);
        }

        return error == 0 ? new NativeDirectory(dir, fd, dir == 0) : null;
    }

    /// <summary>Advances to the next entry, skipping "." and "..".</summary>
    public bool MoveNext()
    {
        if (_useGetdents64)
            return MoveNextGetdents64();

        while (true)
        {
            byte* entry = NativeIo.ReadEntry(_dir);
            if (entry == null)
            {
                Error = NativeIo.GetLastErrno();
                return false;
            }

            byte* name = entry + NativeIo.DirentNameOffset;
            if (name[0] == '.' && (name[1] == 0 || (name[1] == '.' && name[2] == 0)))
                continue;

            _entry = entry;
            _nameLength = NativeIo.IsMacOS
                ? *(ushort*)(entry + NativeIo.MacDirentNameLengthOffset)
                : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name).Length;
            return true;
        }
    }

    private bool MoveNextGetdents64()
    {
        _buf ??= ArrayPool<byte>.Shared.Rent(GetdentsBufferSize);

        while (true)
        {
            if (_bufPos >= _bufLen)
            {
                nint read;
                fixed (byte* pBuf = _buf)
                {
                    read = Libc.getdents64(_fd, pBuf, (nuint)_buf.Length);
                }

                if (read <= 0)
                {
                    Error = read < 0 ? NativeIo.GetLastErrno() : 0;
                    ReturnGetdentsBuffer();
                    return false;
                }

                _bufLen = (int)read;
                _bufPos = 0;
            }

            fixed (byte* pBuf = _buf)
            {
                while (_bufPos < _bufLen)
                {
                    byte* rec = pBuf + _bufPos;
                    ushort reclen = *(ushort*)(rec + 16);
                    int curOffset = _bufPos;
                    _bufPos += reclen;

                    byte* name = rec + NativeIo.DirentNameOffset;
                    if (name[0] == '.' && (name[1] == 0 || (name[1] == '.' && name[2] == 0)))
                        continue;

                    _entryOffset = curOffset;
                    _nameLength = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name).Length;
                    return true;
                }
            }
        }
    }

    private void ReturnGetdentsBuffer()
    {
        if (_buf == null)
            return;

        ArrayPool<byte>.Shared.Return(_buf);
        _buf = null;
        _bufPos = 0;
        _bufLen = 0;
    }

    /// <summary>Stats the current entry. <paramref name="buffer"/> must hold at least <see cref="NativeIo.StatBufferSize"/> bytes.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat) =>
        TryStatCurrent(followSymlinks, buffer, out stat, out _);

    /// <summary>Stats the current entry and outputs errno on failure.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (buffer.Length < NativeIo.StatBufferSize)
            throw new ArgumentException("Buffer is smaller than NativeIo.StatBufferSize.", nameof(buffer));

        fixed (byte* pBuf = buffer)
        {
            if (_useGetdents64)
            {
                fixed (byte* pName = &_buf![_entryOffset + NativeIo.DirentNameOffset])
                {
                    return NativeIo.TryStatAt(_fd, pName, followSymlinks, pBuf, out stat, out errno);
                }
            }

            return NativeIo.TryStatAt(_fd, _entry + NativeIo.DirentNameOffset, followSymlinks, pBuf, out stat, out errno);
        }
    }

    /// <summary>Stats <paramref name="nullTerminatedName"/> relative to this directory and outputs errno on failure.</summary>
    public bool TryStatRelative(ReadOnlySpan<byte> nullTerminatedName, bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (nullTerminatedName.IsEmpty || nullTerminatedName[^1] != 0)
            throw new ArgumentException("Name must be null-terminated.", nameof(nullTerminatedName));
        if (buffer.Length < NativeIo.StatBufferSize)
            throw new ArgumentException("Buffer is smaller than NativeIo.StatBufferSize.", nameof(buffer));

        fixed (byte* pName = nullTerminatedName)
        fixed (byte* pBuf = buffer)
        {
            return NativeIo.TryStatAt(_fd, pName, followSymlinks, pBuf, out stat, out errno);
        }
    }

    /// <summary>Layout sanity check used by the startup probe.</summary>
    internal bool IsCurrentEntryWellFormed()
    {
        if (_useGetdents64)
        {
            fixed (byte* pRec = &_buf![_entryOffset])
            {
                int termLen = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pRec + NativeIo.DirentNameOffset).Length;
                ushort reclen = *(ushort*)(pRec + 16);
                return termLen == _nameLength &&
                       termLen > 0 &&
                       reclen >= NativeIo.DirentNameOffset + termLen + 1;
            }
        }

        int terminatedLength = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(_entry + NativeIo.DirentNameOffset).Length;
        ushort recordLength = *(ushort*)(_entry + 16);
        return terminatedLength == _nameLength &&
               terminatedLength > 0 &&
               recordLength >= NativeIo.DirentNameOffset + terminatedLength + 1;
    }

    public void Dispose()
    {
        ReturnGetdentsBuffer();
        if (_dir != 0)
        {
            _ = Libc.closedir(_dir);
            _dir = 0;
            _fd = -1;
        }
        else if (_fd >= 0)
        {
            _ = Libc.close(_fd);
            _fd = -1;
        }
    }
}
