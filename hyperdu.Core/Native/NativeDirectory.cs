using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace hyperdu.Core.Native;

/// <summary>
/// One open directory stream. Entry names stay valid only until the next <see cref="MoveNext"/>.
/// Uses raw getdents64 on Linux, fdopendir/readdir on macOS, and batched NtQueryDirectoryFile on Windows.
/// </summary>
internal sealed unsafe class NativeDirectory : IDisposable
{
    private const int GetdentsBufferSize = 32768;
    private const int WinNtInitialBufferSize = 65536;
    private const int WinNtMaxBufferSize = 1048576;

    private enum Backend : byte
    {
        UnixDirent = 0,
        UnixGetdents64 = 1,
        WindowsNtQuery = 2
    }

    private readonly Backend _backend;
    private int _fd;
    private nint _dir;
    private byte* _entry;
    private byte[]? _buf;
    private int _bufPos;
    private int _bufLen;
    private int _entryOffset;
    private int _nameLength;

    private nint _winHandle;
    private readonly ulong _volumeSerial;
    private byte* _winNtBuf;
    private int _winNtBufSize = WinNtInitialBufferSize;
    private int _winNtNextOffset = -1;
    private bool _winNtStarted;
    private bool _winNtExhausted;
    private char* _winCurNamePtr;
    private int _winCurNameChars;
    private byte[]? _winUtf8NameBuf;
    private NativeStat _winCurStat;

    private NativeDirectory(nint dir, int fd, bool useGetdents64)
    {
        _dir = dir;
        _fd = fd;
        _winHandle = Win32.INVALID_HANDLE_VALUE;
        _backend = useGetdents64 ? Backend.UnixGetdents64 : Backend.UnixDirent;
    }

    private NativeDirectory(nint winHandle, ulong volumeSerial)
    {
        _fd = -1;
        _winHandle = winHandle;
        _volumeSerial = volumeSerial;
        _backend = Backend.WindowsNtQuery;
    }

    /// <summary>The directory's file descriptor on Unix, or -1 on Windows.</summary>
    public int Fd => _fd;

    /// <summary>The errno or Win32 error code that ended enumeration early, or 0 when the end was reached normally.</summary>
    public int Error { get; private set; }

    internal bool HasInlineStats => _backend == Backend.WindowsNtQuery;

    public ReadOnlySpan<byte> CurrentName => _backend switch
    {
        Backend.WindowsNtQuery => new(_winUtf8NameBuf!, 0, _nameLength),
        Backend.UnixGetdents64 => new(_buf!, _entryOffset + NativeIo.DirentNameOffset, _nameLength),
        _ => new(_entry + NativeIo.DirentNameOffset, _nameLength)
    };

    public ulong CurrentInode
    {
        get
        {
            if (_backend == Backend.WindowsNtQuery)
                return _winCurStat.Inode;

            if (_backend == Backend.UnixGetdents64)
            {
                fixed (byte* p = &_buf![_entryOffset])
                    return *(ulong*)p;
            }

            return *(ulong*)_entry;
        }
    }

    public NativeEntryKind CurrentKind => _backend switch
    {
        Backend.WindowsNtQuery => _winCurStat.Kind,
        Backend.UnixGetdents64 => NativeIo.KindFromDirentType(_buf![_entryOffset + NativeIo.DirentTypeOffset]),
        _ => NativeIo.KindFromDirentType(_entry[NativeIo.DirentTypeOffset])
    };

    internal bool TryGetInlineStat(out NativeStat stat)
    {
        if (_backend == Backend.WindowsNtQuery)
        {
            stat = _winCurStat;
            return true;
        }

        stat = default;
        return false;
    }

    /// <summary>Opens <paramref name="path"/> (null-terminated). Returns null and sets <paramref name="error"/> on failure.</summary>
    public static NativeDirectory? Open(ReadOnlySpan<byte> path, out int error, bool followSymlinks = true)
    {
        if (path.IsEmpty || path[^1] != 0)
            throw new ArgumentException("Path must be null-terminated.", nameof(path));

        if (NativeIo.IsWindows)
        {
            if (!WindowsIo.OpenDirectory(path, followSymlinks, out nint handle, out ulong volSerial, out error))
                return null;
            return new NativeDirectory(handle, volSerial);
        }

        nint dir;
        int fd;
        fixed (byte* p = path)
        {
            error = NativeIo.OpenDirectory(p, out dir, out fd);
        }

        return error == 0 ? new NativeDirectory(dir, fd, dir == 0) : null;
    }

    internal static NativeDirectory? OpenPathSafe(ReadOnlySpan<byte> path, out int error, bool followSymlinks = true)
    {
        if (path.IsEmpty || path[^1] != 0)
            throw new ArgumentException("Path must be null-terminated.", nameof(path));

        if (NativeIo.IsWindows)
            return Open(path, out error, followSymlinks);

        if (!NativeIo.TryOpenDirectoryFdPathSafe(path, out int fd, out error))
            return null;

        // WrapFd calls WrapOpenDirectoryFd, which closes fd on failure.
        return WrapFd(fd, out error);
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

    /// <summary>Opens <paramref name="name"/> (null-terminated) relative to this directory.</summary>
    internal NativeDirectory? OpenRelative(ReadOnlySpan<byte> name, out int error, bool followSymlinks = false)
    {
        if (name.IsEmpty || name[^1] != 0)
            throw new ArgumentException("Name must be null-terminated.", nameof(name));

        if (_backend == Backend.WindowsNtQuery)
        {
            if (!WindowsIo.OpenRelativeDirectory(_winHandle, name, followSymlinks, _volumeSerial, out nint childHandle, out ulong childVol, out error))
                return null;
            return new NativeDirectory(childHandle, childVol);
        }

        return OpenRelative(_fd, name, out error);
    }

    /// <summary>Opens a child directory relative to the current directory stream.</summary>
    public NativeDirectory? OpenCurrentDirectory(out int error, bool followSymlinks = false)
    {
        if (_backend == Backend.WindowsNtQuery)
        {
            if (_winUtf8NameBuf == null || _nameLength <= 0)
            {
                error = Win32.ERROR_INVALID_NAME;
                return null;
            }

            return OpenRelative(_winUtf8NameBuf.AsSpan(0, _nameLength + 1), out error, followSymlinks);
        }

        nint dir;
        int fd;
        if (_backend == Backend.UnixGetdents64)
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
        if (_backend == Backend.WindowsNtQuery)
            return MoveNextWindows();

        if (_backend == Backend.UnixGetdents64)
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

    private bool MoveNextWindows()
    {
        if (_winNtExhausted)
            return false;

        while (true)
        {
            if (_winNtNextOffset < 0)
            {
                if (_winNtBuf == null)
                    _winNtBuf = (byte*)NativeMemory.AlignedAlloc((nuint)_winNtBufSize, 8);

                int status;
                Win32.IO_STATUS_BLOCK iosb = default;
                while (true)
                {
                    status = Win32.NtQueryDirectoryFile(
                        _winHandle,
                        0,
                        0,
                        0,
                        &iosb,
                        _winNtBuf,
                        (uint)_winNtBufSize,
                        Win32.FileIdFullDirectoryInformation,
                        returnSingleEntry: 0,
                        null,
                        restartScan: _winNtStarted ? (byte)0 : (byte)1);
                    _winNtStarted = true;

                    if ((status == Win32.STATUS_BUFFER_OVERFLOW || status == Win32.STATUS_INFO_LENGTH_MISMATCH) &&
                        _winNtBufSize < WinNtMaxBufferSize)
                    {
                        _winNtBufSize *= 2;
                        NativeMemory.AlignedFree(_winNtBuf);
                        _winNtBuf = (byte*)NativeMemory.AlignedAlloc((nuint)_winNtBufSize, 8);
                        continue;
                    }

                    break;
                }

                if (status == Win32.STATUS_NO_MORE_FILES || status == Win32.STATUS_NO_SUCH_FILE)
                {
                    _winNtExhausted = true;
                    Error = 0;
                    ReleaseWinNtBuffer();
                    return false;
                }

                if (status < 0 || iosb.Information == 0)
                {
                    _winNtExhausted = true;
                    Error = status < 0 ? WindowsIo.ToWin32Error(status) : 0;
                    ReleaseWinNtBuffer();
                    return false;
                }

                _winNtNextOffset = 0;
            }

            byte* entry = _winNtBuf + _winNtNextOffset;
            uint nextOffset = *(uint*)(entry + Win32.FdiOffsetNextEntryOffset);
            _winNtNextOffset = nextOffset == 0 ? -1 : _winNtNextOffset + (int)nextOffset;

            int nameBytes = (int)*(uint*)(entry + Win32.FdiOffsetFileNameLength);
            int charCount = nameBytes >> 1;
            char* nameChars = (char*)(entry + Win32.FdiOffsetFileName);

            if (charCount == 1 && nameChars[0] == '.')
                continue;
            if (charCount == 2 && nameChars[0] == '.' && nameChars[1] == '.')
                continue;

            ReadOnlySpan<char> charSpan = new(nameChars, charCount);
            int maxUtf8 = Encoding.UTF8.GetMaxByteCount(charCount) + 1;
            if (_winUtf8NameBuf == null || _winUtf8NameBuf.Length < maxUtf8)
                _winUtf8NameBuf = new byte[Math.Max(256, maxUtf8)];

            _nameLength = Encoding.UTF8.GetBytes(charSpan, _winUtf8NameBuf);
            _winUtf8NameBuf[_nameLength] = 0;
            _winCurNamePtr = nameChars;
            _winCurNameChars = charCount;
            _winCurStat = WindowsIo.BuildStatFromFileIdFullDirEntry(entry, _volumeSerial);
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

    private void ReleaseWinNtBuffer()
    {
        if (_winNtBuf != null)
        {
            NativeMemory.AlignedFree(_winNtBuf);
            _winNtBuf = null;
        }

        _winNtNextOffset = -1;
        _winCurNamePtr = null;
        _winCurNameChars = 0;
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

    /// <summary>Stats this open directory itself.</summary>
    internal bool TryStatSelf(Span<byte> buffer, out NativeStat stat)
    {
        if (_backend == Backend.WindowsNtQuery)
            return WindowsIo.TryStatHandle(_winHandle, followSymlinks: true, out stat, out _);

        ReadOnlySpan<byte> dot = ".\0"u8;
        fixed (byte* pDot = dot)
        fixed (byte* pBuf = buffer)
        {
            return NativeIo.TryStatAt(_fd, pDot, false, pBuf, out stat);
        }
    }

    /// <summary>Stats the current entry. <paramref name="buffer"/> must hold at least <see cref="NativeIo.StatBufferSize"/> bytes.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat) =>
        TryStatCurrent(followSymlinks, buffer, out stat, out _);

    /// <summary>Stats the current entry and outputs errno on failure.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (_backend == Backend.WindowsNtQuery)
        {
            if (!followSymlinks || _winCurStat.Kind != NativeEntryKind.Symlink)
            {
                stat = _winCurStat;
                errno = 0;
                return true;
            }

            if (_winCurNamePtr == null || _winCurNameChars <= 0)
            {
                stat = default;
                errno = Win32.ERROR_INVALID_NAME;
                return false;
            }

            return WindowsIo.TryStatRelative(
                _winHandle,
                new ReadOnlySpan<char>(_winCurNamePtr, _winCurNameChars),
                followSymlinks: true,
                out stat,
                out errno);
        }

        if (buffer.Length < NativeIo.StatBufferSize)
            throw new ArgumentException("Buffer is smaller than NativeIo.StatBufferSize.", nameof(buffer));

        fixed (byte* pBuf = buffer)
        {
            if (_backend == Backend.UnixGetdents64)
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

        if (_backend == Backend.WindowsNtQuery)
        {
            ReadOnlySpan<byte> utf8Name = nullTerminatedName[..^1];
            int maxChars = Encoding.UTF8.GetMaxCharCount(utf8Name.Length);
            char[]? rented = null;
            Span<char> chars = maxChars <= 260
                ? stackalloc char[260]
                : (rented = ArrayPool<char>.Shared.Rent(maxChars));
            try
            {
                int charLen = Encoding.UTF8.GetChars(utf8Name, chars);
                return WindowsIo.TryStatRelative(_winHandle, chars[..charLen], followSymlinks, out stat, out errno);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<char>.Shared.Return(rented);
            }
        }

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
        if (_backend == Backend.WindowsNtQuery)
            return _nameLength > 0 && _winCurNamePtr != null;

        if (_backend == Backend.UnixGetdents64)
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
        ReleaseWinNtBuffer();
        ReturnGetdentsBuffer();
        if (_backend == Backend.WindowsNtQuery)
        {
            if (_winHandle != Win32.INVALID_HANDLE_VALUE && _winHandle != 0)
            {
                _ = Win32.CloseHandle(_winHandle);
                _winHandle = Win32.INVALID_HANDLE_VALUE;
            }

            return;
        }

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
