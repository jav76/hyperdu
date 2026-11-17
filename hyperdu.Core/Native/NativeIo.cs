using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace hyperdu.Core.Native;

internal enum NativeEntryKind : byte
{
    Unknown,
    File,
    Directory,
    Symlink,
    Other
}

internal readonly struct NativeStat
{
    public NativeStat(int mode, long size, long blocks, uint flags)
        : this(mode, size, blocks, flags, device: 0, inode: 0, hardLinks: 0, mtimeSec: 0, mtimeNsec: 0, atimeSec: 0, atimeNsec: 0, ctimeSec: 0, ctimeNsec: 0)
    {
    }

    public NativeStat(
        int mode,
        long size,
        long blocks,
        uint flags,
        ulong device,
        ulong inode,
        uint hardLinks,
        long mtimeSec,
        uint mtimeNsec)
        : this(mode, size, blocks, flags, device, inode, hardLinks, mtimeSec, mtimeNsec, atimeSec: 0, atimeNsec: 0, ctimeSec: 0, ctimeNsec: 0)
    {
    }

    public NativeStat(
        int mode,
        long size,
        long blocks,
        uint flags,
        ulong device,
        ulong inode,
        uint hardLinks,
        long mtimeSec,
        uint mtimeNsec,
        long atimeSec,
        uint atimeNsec,
        long ctimeSec,
        uint ctimeNsec)
    {
        Mode = mode;
        Size = size;
        Blocks = blocks;
        Flags = flags;
        Device = device;
        Inode = inode;
        HardLinks = hardLinks;
        MtimeSec = mtimeSec;
        MtimeNsec = mtimeNsec;
        AtimeSec = atimeSec;
        AtimeNsec = atimeNsec;
        CtimeSec = ctimeSec;
        CtimeNsec = ctimeNsec;
    }

    public int Mode { get; }
    public long Size { get; }

    /// <summary>Allocated size in 512-byte units.</summary>
    public long Blocks { get; }

    public uint Flags { get; }
    public ulong Device { get; }
    public ulong Inode { get; }
    public uint HardLinks { get; }
    public long MtimeSec { get; }
    public uint MtimeNsec { get; }
    public long AtimeSec { get; }
    public uint AtimeNsec { get; }
    public long CtimeSec { get; }
    public uint CtimeNsec { get; }

    public NativeEntryKind Kind => (Mode & Libc.S_IFMT) switch
    {
        Libc.S_IFDIR => NativeEntryKind.Directory,
        Libc.S_IFREG => NativeEntryKind.File,
        Libc.S_IFLNK => NativeEntryKind.Symlink,
        _ => NativeEntryKind.Other
    };

    public bool HasNoDumpFlag => !OperatingSystem.IsWindows() && (Flags & Libc.UF_NODUMP) != 0;
    public bool HasHiddenFlag => OperatingSystem.IsWindows()
        ? (Flags & (Win32.FILE_ATTRIBUTE_HIDDEN | Win32.FILE_ATTRIBUTE_SYSTEM)) != 0
        : (Flags & Libc.UF_HIDDEN) != 0;
    internal bool HasValidFileId => Inode != 0 && Inode != ulong.MaxValue;
}

/// <summary>
/// Byte-exact, directory-relative filesystem access for Linux, macOS, and Windows
/// (x64, arm64, and x86 on Windows via NtQueryDirectoryFile).
/// Any platform where the startup probe fails falls back to the managed enumerator.
/// </summary>
internal static unsafe class NativeIo
{
    internal const int StatBufferSize = 256;

    private enum Platform
    {
        Unsupported,
        Linux,
        MacOS,
        MacOSInode64,
        Windows
    }

    private static readonly Platform s_platform = DetectPlatform();
    private static readonly Lazy<bool> s_usesGetdents64 = new(ProbeGetdents64);
    private static readonly Lazy<bool> s_supported = new(Probe);

    public static bool IsSupported => s_supported.Value;

    public static bool IsWindows => s_platform == Platform.Windows;

    public static bool IsPosix => s_platform is Platform.Linux or Platform.MacOS or Platform.MacOSInode64;

    public static bool IsMacOS => s_platform is Platform.MacOS or Platform.MacOSInode64;

    internal static bool UsesGetdents64 => s_usesGetdents64.Value;

    internal static bool UsesWindowsNtQuery => IsWindows && IsSupported;

    internal static int GetLastErrno() =>
        OperatingSystem.IsMacOS() ? GetMacOSErrno() : GetLinuxErrno();

    internal static void ClearErrno()
    {
        if (OperatingSystem.IsMacOS())
            ClearMacOSErrno();
        else
            ClearLinuxErrno();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetLinuxErrno() => *Libc.errno_location_linux();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetMacOSErrno() => *Libc.errno_location_macos();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearLinuxErrno() => *Libc.errno_location_linux() = 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearMacOSErrno() => *Libc.errno_location_macos() = 0;

    // Linux dirent: d_ino u64, d_off i64, d_reclen u16, d_type u8, d_name.
    // macOS dirent (64-bit inode): d_ino u64, d_seekoff u64, d_reclen u16, d_namlen u16, d_type u8, d_name.
    internal static int DirentTypeOffset => IsMacOS ? 20 : 18;
    internal static int DirentNameOffset => IsMacOS ? 21 : 19;
    internal static int EloopErrno => IsWindows ? Win32.ERROR_CANT_RESOLVE_FILENAME : (IsMacOS ? 62 : 40);
    internal static bool IsNotFoundError(int error) =>
        IsWindows ? WindowsIo.IsNotFoundError(error) : error == Libc.ENOENT;
    internal const int MacDirentNameLengthOffset = 18;

    private static int OpenDirectoryFlags => IsMacOS
        ? Libc.O_NONBLOCK_MACOS | Libc.O_CLOEXEC_MACOS
        : Libc.O_NONBLOCK_LINUX | Libc.O_CLOEXEC_LINUX;

    private static int AtFdCwd => IsMacOS ? Libc.AT_FDCWD_MACOS : Libc.AT_FDCWD_LINUX;

    private static Platform DetectPlatform()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows())
            return arch is Architecture.X64 or Architecture.Arm64 or Architecture.X86
                ? Platform.Windows
                : Platform.Unsupported;

        if (arch is not (Architecture.X64 or Architecture.Arm64)) return Platform.Unsupported;
        if (OperatingSystem.IsLinux()) return Platform.Linux;
        if (OperatingSystem.IsMacOS()) return arch == Architecture.X64 ? Platform.MacOSInode64 : Platform.MacOS;
        return Platform.Unsupported;
    }

    /// <summary>Opens a directory file descriptor on Linux when getdents64 is active, or a DIR* stream otherwise.</summary>
    internal static int OpenDirectory(byte* path, out nint dir, out int fd)
    {
        fd = Libc.open(path, OpenDirectoryFlags);
        return WrapOpenDirectoryFd(fd, out dir);
    }

    /// <summary>Opens a directory relative to <paramref name="dirFd"/>. Returns 0 on success, otherwise the errno.</summary>
    internal static int OpenDirectoryRelative(int dirFd, byte* name, out nint dir, out int fd)
    {
        fd = Libc.openat(dirFd, name, OpenDirectoryFlags);
        return WrapOpenDirectoryFd(fd, out dir);
    }

    internal static int OpenRawDirectoryFd(byte* path, out int fd)
    {
        fd = Libc.open(path, OpenDirectoryFlags);
        return fd >= 0 ? 0 : GetLastErrno();
    }

    internal static int OpenRawDirectoryFdRelative(int dirFd, byte* name, out int fd)
    {
        fd = Libc.openat(dirFd, name, OpenDirectoryFlags);
        return fd >= 0 ? 0 : GetLastErrno();
    }

    internal static bool TryOpenReadOnly(string path, out int fd, out int errno)
    {
        if (!IsPosix)
        {
            fd = -1;
            errno = 0;
            return false;
        }

        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int len = System.Text.Encoding.UTF8.GetBytes(path, rented);
            rented[len] = 0;
            int flags = IsMacOS ? Libc.O_CLOEXEC_MACOS : Libc.O_CLOEXEC_LINUX;
            fixed (byte* pPath = rented)
            {
                fd = Libc.open(pPath, flags);
            }

            if (fd >= 0)
            {
                errno = 0;
                return true;
            }

            errno = GetLastErrno();
            return false;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static bool TryReadFd(int fd, Span<byte> buffer, out int bytesRead, out int errno)
    {
        if (!IsPosix)
        {
            bytesRead = 0;
            errno = 0;
            return false;
        }

        fixed (byte* pBuf = buffer)
        {
            nint n = Libc.read(fd, pBuf, (nuint)buffer.Length);
            if (n >= 0)
            {
                bytesRead = (int)n;
                errno = 0;
                return true;
            }

            bytesRead = 0;
            errno = GetLastErrno();
            return false;
        }
    }

    internal static void CloseFd(int fd)
    {
        if (IsPosix && fd >= 0)
            _ = Libc.close(fd);
    }

    internal static int WrapOpenDirectoryFd(int fd, out nint dir)
    {
        dir = 0;
        if (fd < 0) return GetLastErrno();

        if (UsesGetdents64)
            return 0;

        dir = s_platform == Platform.MacOSInode64 ? Libc.fdopendir_inode64(fd) : Libc.fdopendir(fd);
        if (dir != 0) return 0;

        int error = GetLastErrno();
        _ = Libc.close(fd);
        return error;
    }

    internal static byte* ReadEntry(nint dir)
    {
        ClearErrno();
        return s_platform == Platform.MacOSInode64 ? Libc.readdir_inode64(dir) : Libc.readdir(dir);
    }

    internal static NativeEntryKind KindFromDirentType(byte type) => type switch
    {
        Libc.DT_DIR => NativeEntryKind.Directory,
        Libc.DT_REG => NativeEntryKind.File,
        Libc.DT_LNK => NativeEntryKind.Symlink,
        Libc.DT_UNKNOWN => NativeEntryKind.Unknown,
        _ => NativeEntryKind.Other
    };

    /// <summary>Stats <paramref name="path"/> relative to <paramref name="dirFd"/>. <paramref name="buffer"/> must hold <see cref="StatBufferSize"/> bytes.</summary>
    internal static bool TryStatAt(int dirFd, byte* path, bool followSymlinks, byte* buffer, out NativeStat stat) =>
        TryStatAt(dirFd, path, followSymlinks, buffer, out stat, out _);

    /// <summary>Stats <paramref name="path"/> relative to <paramref name="dirFd"/> and outputs errno on failure.</summary>
    internal static bool TryStatAt(int dirFd, byte* path, bool followSymlinks, byte* buffer, out NativeStat stat, out int errno)
    {
        if (s_platform == Platform.Linux)
        {
            int flags = followSymlinks ? 0 : Libc.AT_SYMLINK_NOFOLLOW_LINUX;
            uint mask = Libc.STATX_TYPE | Libc.STATX_MODE | Libc.STATX_NLINK |
                        Libc.STATX_INO | Libc.STATX_SIZE | Libc.STATX_BLOCKS |
                        Libc.STATX_ATIME | Libc.STATX_CTIME | Libc.STATX_MTIME;
            if (Libc.statx(dirFd, path, flags, mask, buffer) != 0)
            {
                errno = GetLastErrno();
                stat = default;
                return false;
            }

            // struct statx: nlink u32 @16, mode u16 @28, ino u64 @32, size u64 @40, blocks u64 @48,
            // atime i64/u32 @64/72, ctime i64/u32 @96/104, mtime i64/u32 @112/120, dev_major/minor u32 @136/140.
            ulong device = ((ulong)*(uint*)(buffer + 136) << 32) | *(uint*)(buffer + 140);
            stat = new NativeStat(
                mode: *(ushort*)(buffer + 28),
                size: *(long*)(buffer + 40),
                blocks: *(long*)(buffer + 48),
                flags: 0,
                device: device,
                inode: *(ulong*)(buffer + 32),
                hardLinks: *(uint*)(buffer + 16),
                mtimeSec: *(long*)(buffer + 112),
                mtimeNsec: *(uint*)(buffer + 120),
                atimeSec: *(long*)(buffer + 64),
                atimeNsec: *(uint*)(buffer + 72),
                ctimeSec: *(long*)(buffer + 96),
                ctimeNsec: *(uint*)(buffer + 104));
            errno = 0;
            return true;
        }

        int atFlags = followSymlinks ? 0 : Libc.AT_SYMLINK_NOFOLLOW_MACOS;
        int result = s_platform == Platform.MacOSInode64
            ? Libc.fstatat_inode64(dirFd, path, buffer, atFlags)
            : Libc.fstatat(dirFd, path, buffer, atFlags);
        if (result != 0)
        {
            errno = GetLastErrno();
            stat = default;
            return false;
        }

        // 64-bit inode struct stat: dev u32 @0, mode u16 @4, nlink u16 @6, ino u64 @8,
        // atime i64/i64 @32/40, mtime i64/i64 @48/56, ctime i64/i64 @64/72, size i64 @96, blocks i64 @104, flags u32 @116.
        stat = new NativeStat(
            mode: *(ushort*)(buffer + 4),
            size: *(long*)(buffer + 96),
            blocks: *(long*)(buffer + 104),
            flags: *(uint*)(buffer + 116),
            device: *(uint*)(buffer + 0),
            inode: *(ulong*)(buffer + 8),
            hardLinks: *(ushort*)(buffer + 6),
            mtimeSec: *(long*)(buffer + 48),
            mtimeNsec: (uint)*(long*)(buffer + 56),
            atimeSec: *(long*)(buffer + 32),
            atimeNsec: (uint)*(long*)(buffer + 40),
            ctimeSec: *(long*)(buffer + 64),
            ctimeNsec: (uint)*(long*)(buffer + 72));
        errno = 0;
        return true;
    }

    /// <summary>Stats a null-terminated path relative to the current working directory.</summary>
    internal static bool TryStatPath(ReadOnlySpan<byte> nullTerminatedPath, bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (nullTerminatedPath.IsEmpty || nullTerminatedPath[^1] != 0)
            throw new ArgumentException("Path must be null-terminated.", nameof(nullTerminatedPath));

        if (IsWindows)
            return WindowsIo.TryStatPath(nullTerminatedPath[..^1], followSymlinks, out stat, out errno);

        if (buffer.Length < StatBufferSize)
            throw new ArgumentException("Buffer is smaller than StatBufferSize.", nameof(buffer));

        fixed (byte* p = nullTerminatedPath)
        fixed (byte* b = buffer)
        {
            return TryStatAt(AtFdCwd, p, followSymlinks, b, out stat, out errno);
        }
    }

    /// <summary>Maps an OS error code to the same messages the managed scanner reports.</summary>
    internal static string DescribeError(int errno) => IsWindows
        ? WindowsIo.DescribeError(errno)
        : errno switch
        {
            Libc.EACCES or Libc.EPERM => "Access Denied",
            Libc.ENOENT or Libc.ENOTDIR => "Directory Not Found",
            _ => Marshal.GetPInvokeErrorMessage(errno)
        };

    /// <summary>Returns the exact POSIX strerror / Win32 message matching OS error codes.</summary>
    internal static string DescribePosixError(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    /// <summary>Restores default SIGPIPE termination so downstream pipe closes yield exit code 141 without .NET exceptions.</summary>
    internal static void RestoreDefaultSigpipe()
    {
        if (IsPosix)
            _ = Libc.signal(Libc.SIGPIPE, 0);
    }

    /// <summary>Restores default SIGPIPE and raises SIGPIPE on EPIPE so POSIX callers (including pipefail) observe signal 13 (141).</summary>
    internal static void RestoreDefaultSigpipeAndReraise()
    {
        if (!IsPosix)
            return;

        RestoreDefaultSigpipe();
        _ = Libc.raise(Libc.SIGPIPE);
    }

    internal static bool TryGetNumericLocaleStrings(
        string localeName,
        out byte[] decimalPoint,
        out byte[] thousandsSep,
        out byte[] grouping)
    {
        decimalPoint = [];
        thousandsSep = [];
        grouping = [];

        if (!IsPosix || string.IsNullOrEmpty(localeName))
            return false;

        int mask = IsMacOS ? Libc.LC_ALL_MASK_MACOS : Libc.LC_NUMERIC_MASK_LINUX;
        nint loc = CreatePosixLocaleHandle(mask, localeName);
        if (loc == 0)
            return false;

        nint prev = Libc.uselocale(loc);
        try
        {
            byte** lconv = Libc.localeconv();
            if (lconv == null)
                return false;

            byte* decPtr = lconv[0];
            byte* sepPtr = lconv[1];
            byte* grpPtr = lconv[2];

            decimalPoint = decPtr != null
                ? MemoryMarshal.CreateReadOnlySpanFromNullTerminated(decPtr).ToArray()
                : [(byte)'.'];
            if (decimalPoint.Length == 0)
                decimalPoint = [(byte)'.'];

            thousandsSep = sepPtr != null
                ? MemoryMarshal.CreateReadOnlySpanFromNullTerminated(sepPtr).ToArray()
                : [];

            grouping = grpPtr != null
                ? MemoryMarshal.CreateReadOnlySpanFromNullTerminated(grpPtr).ToArray()
                : [];

            return true;
        }
        finally
        {
            if (prev != 0)
                _ = Libc.uselocale(prev);
            Libc.freelocale(loc);
        }
    }

    internal static bool IsPosixCtypeLocaleAvailable(string? localeName)
    {
        if (!IsPosix || string.IsNullOrEmpty(localeName))
            return false;

        int mask = IsMacOS ? Libc.LC_CTYPE_MASK_MACOS : Libc.LC_CTYPE_MASK_LINUX;
        nint loc = CreatePosixLocaleHandle(mask, localeName);
        if (loc == 0)
            return false;

        Libc.freelocale(loc);
        return true;
    }

    internal static nint CreateTimeLocaleHandle(string? localeName)
    {
        if (!IsPosix || string.IsNullOrEmpty(localeName))
            return 0;

        int mask = IsMacOS ? Libc.LC_ALL_MASK_MACOS : Libc.LC_TIME_MASK_LINUX;
        return CreatePosixLocaleHandle(mask, localeName);
    }

    internal static void FreeLocaleHandle(nint loc)
    {
        if (IsPosix && loc != 0)
            Libc.freelocale(loc);
    }

    internal static void SyncTimezone(string? tzValue)
    {
        if (!IsPosix)
            return;

        ReadOnlySpan<byte> tzName = "TZ\0"u8;
        fixed (byte* pName = tzName)
        {
            byte* existingPtr = Libc.getenv(pName);
            if (tzValue == null)
            {
                if (existingPtr != null)
                    _ = Libc.unsetenv(pName);
            }
            else
            {
                int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(tzValue.Length) + 1;
                byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
                try
                {
                    int len = System.Text.Encoding.UTF8.GetBytes(tzValue, rented);
                    rented[len] = 0;
                    ReadOnlySpan<byte> newSpan = rented.AsSpan(0, len);
                    bool same = existingPtr != null &&
                                MemoryMarshal.CreateReadOnlySpanFromNullTerminated(existingPtr).SequenceEqual(newSpan);
                    if (!same)
                    {
                        fixed (byte* pVal = rented)
                        {
                            _ = Libc.setenv(pName, pVal, 1);
                        }
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        Libc.tzset();
    }

    internal static bool TryFormatLocalTime(
        long seconds,
        ReadOnlySpan<byte> sentinelNullTermFormat,
        Span<byte> destination,
        nint timeLocale,
        out int bytesWritten,
        out bool outOfRange)
    {
        bytesWritten = 0;
        outOfRange = false;

        if (!IsPosix || destination.IsEmpty)
            return false;

        byte* tmBuf = stackalloc byte[128];
        long sec = seconds;
        if (Libc.localtime_r(&sec, tmBuf) == null)
        {
            outOfRange = true;
            return false;
        }

        nint prev = timeLocale != 0 ? Libc.uselocale(timeLocale) : 0;
        try
        {
            nuint len;
            fixed (byte* pDst = destination)
            fixed (byte* pFmt = sentinelNullTermFormat)
            {
                len = Libc.strftime(pDst, (nuint)destination.Length, pFmt, tmBuf);
            }

            if (len == 0)
                return false;

            int contentLen = (int)len - 1;
            if (contentLen > 0)
                destination.Slice(1, contentLen).CopyTo(destination);

            bytesWritten = contentLen;
            return true;
        }
        finally
        {
            if (prev != 0)
                _ = Libc.uselocale(prev);
        }
    }

    private static nint CreatePosixLocaleHandle(int mask, string localeName)
    {
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(localeName.Length) + 1;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int len = System.Text.Encoding.UTF8.GetBytes(localeName, rented);
            rented[len] = 0;
            fixed (byte* pLocale = rented)
            {
                return Libc.newlocale(mask, pLocale, 0);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static readonly int SegmentPathLimit = IsMacOS ? 900 : 3800;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ComputeEntryByteSize(in NativeStat stat, bool apparentSize)
    {
        if (apparentSize)
            return stat.Kind == NativeEntryKind.Directory || stat.Size <= 0 ? 0L : stat.Size;

        return stat.Blocks > 0 ? stat.Blocks * 512L : 0L;
    }

    /// <summary>
    /// Opens a directory file descriptor for a null-terminated path even when its total length
    /// exceeds PATH_MAX by stepping relative chunks through openat().
    /// </summary>
    internal static bool TryOpenDirectoryFdPathSafe(ReadOnlySpan<byte> nullTermPath, out int dirFd, out int errno)
    {
        int pathLen = nullTermPath.Length > 0 && nullTermPath[^1] == 0
            ? nullTermPath.Length - 1
            : nullTermPath.Length;

        if (pathLen == 0)
        {
            dirFd = -1;
            errno = Libc.ENOENT;
            return false;
        }

        if (pathLen < SegmentPathLimit)
        {
            fixed (byte* pPath = nullTermPath)
            {
                errno = OpenRawDirectoryFd(pPath, out dirFd);
            }

            if (errno == 0 && dirFd >= 0)
                return true;

            dirFd = -1;
            return false;
        }

        return OpenDirectoryFdBeyondPathMax(nullTermPath[..pathLen], out dirFd, out errno);
    }

    private static bool OpenDirectoryFdBeyondPathMax(ReadOnlySpan<byte> path, out int dirFd, out int errno)
    {
        byte[] segBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(SegmentPathLimit + 256);
        int curFd = -1;
        errno = 0;
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
                    int searchStart = first && path[pos] == (byte)'/' ? pos + 1 : pos;
                    int slash = path[searchStart..maxEnd].LastIndexOf((byte)'/');
                    if (slash >= 0)
                    {
                        chunkEnd = searchStart + slash;
                    }
                    else
                    {
                        int nextSlash = path[searchStart..].IndexOf((byte)'/');
                        chunkEnd = nextSlash >= 0 ? searchStart + nextSlash : path.Length;
                    }
                }

                int chunkLen = chunkEnd - pos;
                if (chunkLen == 0)
                    break;

                if (chunkLen + 1 > segBuf.Length)
                {
                    byte[] grown = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkLen + 1);
                    System.Buffers.ArrayPool<byte>.Shared.Return(segBuf);
                    segBuf = grown;
                }

                path.Slice(pos, chunkLen).CopyTo(segBuf);
                segBuf[chunkLen] = 0;

                int nextFd;
                fixed (byte* pSeg = segBuf)
                {
                    errno = first
                        ? OpenRawDirectoryFd(pSeg, out nextFd)
                        : OpenRawDirectoryFdRelative(curFd, pSeg, out nextFd);
                }

                CloseFd(curFd);

                if (errno != 0 || nextFd < 0)
                {
                    dirFd = -1;
                    return false;
                }

                curFd = nextFd;
                pos = chunkEnd;
                first = false;
            }

            if (curFd < 0)
            {
                if (!path.SequenceEqual("/"u8))
                {
                    dirFd = -1;
                    errno = Libc.ENOENT;
                    return false;
                }

                ReadOnlySpan<byte> root = "/\0"u8;
                fixed (byte* pRoot = root)
                {
                    errno = OpenRawDirectoryFd(pRoot, out curFd);
                }
            }

            if (errno != 0 || curFd < 0)
            {
                dirFd = -1;
                return false;
            }

            dirFd = curFd;
            errno = 0;
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(segBuf);
        }
    }

    internal static bool DirectoryExistsPathSafe(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (Directory.Exists(path))
            return true;

        if (!IsSupported)
            return false;

        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int len = System.Text.Encoding.UTF8.GetBytes(path, rented);
            rented[len] = 0;
            using NativeDirectory? dir = NativeDirectory.OpenPathSafe(rented.AsSpan(0, len + 1), out _, followSymlinks: true);
            if (dir == null)
                return false;

            Span<byte> statBuf = stackalloc byte[StatBufferSize];
            return dir.TryStatSelf(statBuf, out NativeStat stat) &&
                   stat.Kind == NativeEntryKind.Directory;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static string? TryGetMacOsRawArgv0()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            byte*** argvp = Libc._NSGetArgv();
            if (argvp == null || *argvp == null || (*argvp)[0] == null)
                return null;

            ReadOnlySpan<byte> raw = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((*argvp)[0]);
            return raw.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsMacOsDevFdDirectory(ReadOnlySpan<byte> nullTermPath, ReadOnlySpan<byte> entryName)
    {
        if (!IsMacOS || !entryName.SequenceEqual("fd"u8))
            return false;

        return TryStatFsMacOs(nullTermPath, out string fsTypeName, out _) &&
               string.Equals(fsTypeName, "devfs", StringComparison.Ordinal);
    }

    internal static bool TryGetMacOsMountInfo(string path, out string fsTypeName, out string mountFromName)
    {
        fsTypeName = string.Empty;
        mountFromName = string.Empty;

        if (!IsMacOS || string.IsNullOrEmpty(path))
            return false;

        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(path.Length) + 1;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int len = System.Text.Encoding.UTF8.GetBytes(path, rented);
            rented[len] = 0;
            return TryStatFsMacOs(rented.AsSpan(0, len + 1), out fsTypeName, out mountFromName);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryStatFsMacOs(ReadOnlySpan<byte> nullTermPath, out string fsTypeName, out string mountFromName)
    {
        fsTypeName = string.Empty;
        mountFromName = string.Empty;

        if (!IsMacOS || nullTermPath.IsEmpty || nullTermPath[^1] != 0)
            return false;

        try
        {
            byte* buf = stackalloc byte[2200];
            new Span<byte>(buf, 2200).Clear();

            int rc;
            fixed (byte* pPath = nullTermPath)
            {
                rc = s_platform == Platform.MacOSInode64
                    ? Libc.statfs_inode64(pPath, buf)
                    : Libc.statfs(pPath, buf);
            }

            if (rc != 0)
                return false;

            // XNU __DARWIN_STRUCT_STATFS64: f_fstypename[16] at offset 72, f_mntfromname[1024] at offset 1112.
            ReadOnlySpan<byte> fsTypeSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buf + 72);
            if (fsTypeSpan.Length > 16)
                fsTypeSpan = fsTypeSpan[..16];
            fsTypeName = System.Text.Encoding.UTF8.GetString(fsTypeSpan);

            ReadOnlySpan<byte> mntFromSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buf + 1112);
            if (mntFromSpan.Length > 1024)
                mntFromSpan = mntFromSpan[..1024];
            mountFromName = System.Text.Encoding.UTF8.GetString(mntFromSpan);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Matches gnulib fts_build inode-sort filesystem check (sorting >10k-entry local disk
    /// directories by d_ino to avoid random seeks), while keeping BSD du unsorted.
    /// </summary>
    internal static bool ShouldSortDirectoryByInode(int dirFd, bool allowOnMacOs = false)
    {
        if (s_platform == Platform.Linux)
        {
            byte* buf = stackalloc byte[128];
            if (Libc.fstatfs(dirFd, buf) != 0)
                return false;

            ulong fsType = *(ulong*)buf;
            return fsType is not (Libc.TMPFS_MAGIC or Libc.RAMFS_MAGIC or Libc.NFS_SUPER_MAGIC);
        }

        if (!IsMacOS || !allowOnMacOs || dirFd < 0)
            return false;

        try
        {
            byte* buf = stackalloc byte[2200];
            new Span<byte>(buf, 2200).Clear();
            int rc = s_platform == Platform.MacOSInode64
                ? Libc.fstatfs_inode64(dirFd, buf)
                : Libc.fstatfs(dirFd, buf);
            if (rc != 0)
                return false;

            ReadOnlySpan<byte> fsTypeSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buf + 72);
            if (fsTypeSpan.Length > 16)
                fsTypeSpan = fsTypeSpan[..16];

            return !fsTypeSpan.SequenceEqual("nfs"u8) &&
                   !fsTypeSpan.SequenceEqual("smbfs"u8) &&
                   !fsTypeSpan.SequenceEqual("cifs"u8) &&
                   !fsTypeSpan.SequenceEqual("afpfs"u8) &&
                   !fsTypeSpan.SequenceEqual("devfs"u8) &&
                   !fsTypeSpan.SequenceEqual("autofs"u8);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeGetdents64()
    {
        if (s_platform != Platform.Linux)
            return false;

        int fd = -1;
        try
        {
            ReadOnlySpan<byte> root = "/\0"u8;
            fixed (byte* pRoot = root)
            {
                fd = Libc.open(pRoot, OpenDirectoryFlags);
            }

            if (fd < 0)
                return false;

            byte* buf = stackalloc byte[512];
            nint bytesRead = Libc.getdents64(fd, buf, 512);
            if (bytesRead <= 0)
                return false;

            ushort reclen = *(ushort*)(buf + 16);
            int nameLen = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buf + 19).Length;
            return nameLen > 0 && reclen >= 19 + nameLen + 1 && reclen <= bytesRead;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (fd >= 0)
                _ = Libc.close(fd);
        }
    }

    /// <summary>
    /// Binds every entry point once and checks the struct layouts against "/", so a platform
    /// with unexpected symbols or layouts falls back to the managed scanner instead of misreading data.
    /// </summary>
    private static bool Probe()
    {
        if (s_platform == Platform.Unsupported) return false;
        if (s_platform == Platform.Windows) return WindowsIo.Probe();

        try
        {
            ReadOnlySpan<byte> root = "/\0"u8;
            byte* buffer = stackalloc byte[StatBufferSize];

            fixed (byte* rootPath = root)
            {
                if (!TryStatAt(AtFdCwd, rootPath, true, buffer, out NativeStat rootStat) ||
                    rootStat.Kind != NativeEntryKind.Directory)
                    return false;
            }

            using NativeDirectory? directory = NativeDirectory.Open(root, out _);
            if (directory == null) return false;

            while (directory.MoveNext())
            {
                if (!directory.IsCurrentEntryWellFormed()) return false;
            }

            return directory.Error == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
