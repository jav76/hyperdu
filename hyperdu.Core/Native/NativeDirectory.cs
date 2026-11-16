using System.Runtime.InteropServices;

namespace hyperdu.Core.Native;

/// <summary>
/// One open directory stream. Entry names are raw bytes and stay valid only until the next <see cref="MoveNext"/>.
/// Not thread-safe; each worker opens its own.
/// </summary>
internal sealed unsafe class NativeDirectory : IDisposable
{
    private nint _dir;
    private byte* _entry;
    private int _nameLength;

    private NativeDirectory(nint dir, int fd)
    {
        _dir = dir;
        Fd = fd;
    }

    /// <summary>The directory's file descriptor, for *at() calls relative to it.</summary>
    public int Fd { get; }

    /// <summary>The errno that ended enumeration early, or 0 when the end was reached normally.</summary>
    public int Error { get; private set; }

    /// <summary>Null-terminated name of the current entry.</summary>
    private byte* CurrentNamePointer => _entry + NativeIo.DirentNameOffset;

    public ReadOnlySpan<byte> CurrentName => new(CurrentNamePointer, _nameLength);

    public ulong CurrentInode => *(ulong*)_entry;

    public NativeEntryKind CurrentKind => NativeIo.KindFromDirentType(_entry[NativeIo.DirentTypeOffset]);

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

        return error == 0 ? new NativeDirectory(dir, fd) : null;
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

        return error == 0 ? new NativeDirectory(dir, fd) : null;
    }

    /// <summary>Opens a child directory relative to the current directory stream.</summary>
    public NativeDirectory? OpenCurrentDirectory(out int error)
    {
        int fd;
        error = NativeIo.OpenDirectoryRelative(Fd, CurrentNamePointer, out nint dir, out fd);
        return error == 0 ? new NativeDirectory(dir, fd) : null;
    }

    /// <summary>Advances to the next entry, skipping "." and "..".</summary>
    public bool MoveNext()
    {
        while (true)
        {
            byte* entry = NativeIo.ReadEntry(_dir);
            if (entry == null)
            {
                Error = Marshal.GetLastPInvokeError();
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

    /// <summary>Stats the current entry. <paramref name="buffer"/> must hold at least <see cref="NativeIo.StatBufferSize"/> bytes.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat) =>
        TryStatCurrent(followSymlinks, buffer, out stat, out _);

    /// <summary>Stats the current entry and outputs errno on failure.</summary>
    public bool TryStatCurrent(bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (buffer.Length < NativeIo.StatBufferSize)
            throw new ArgumentException("Buffer is smaller than NativeIo.StatBufferSize.", nameof(buffer));

        fixed (byte* p = buffer)
        {
            return NativeIo.TryStatAt(Fd, CurrentNamePointer, followSymlinks, p, out stat, out errno);
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
            return NativeIo.TryStatAt(Fd, pName, followSymlinks, pBuf, out stat, out errno);
        }
    }

    /// <summary>Layout sanity check used by the startup probe.</summary>
    internal bool IsCurrentEntryWellFormed()
    {
        int terminatedLength = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(CurrentNamePointer).Length;
        ushort recordLength = *(ushort*)(_entry + 16);
        return terminatedLength == _nameLength &&
               terminatedLength > 0 &&
               recordLength >= NativeIo.DirentNameOffset + terminatedLength + 1;
    }

    public void Dispose()
    {
        if (_dir == 0) return;
        _ = Libc.closedir(_dir);
        _dir = 0;
    }
}
