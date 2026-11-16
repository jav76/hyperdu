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
        : this(mode, size, blocks, flags, device: 0, inode: 0, hardLinks: 0, mtimeSec: 0, mtimeNsec: 0)
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

    public NativeEntryKind Kind => (Mode & Libc.S_IFMT) switch
    {
        Libc.S_IFDIR => NativeEntryKind.Directory,
        Libc.S_IFREG => NativeEntryKind.File,
        Libc.S_IFLNK => NativeEntryKind.Symlink,
        _ => NativeEntryKind.Other
    };

    public bool HasHiddenFlag => (Flags & Libc.UF_HIDDEN) != 0;
}

/// <summary>
/// Byte-exact, directory-relative filesystem access for Linux and macOS (x64 and arm64).
/// Everything else, and any platform where the startup probe fails, uses the managed enumerator.
/// </summary>
internal static unsafe class NativeIo
{
    internal const int StatBufferSize = 256;

    private enum Platform
    {
        Unsupported,
        Linux,
        MacOS,
        MacOSInode64
    }

    private static readonly Platform s_platform = DetectPlatform();
    private static readonly Lazy<bool> s_supported = new(Probe);

    public static bool IsSupported => s_supported.Value;

    public static bool IsMacOS => s_platform is Platform.MacOS or Platform.MacOSInode64;

    // Linux dirent: d_ino u64, d_off i64, d_reclen u16, d_type u8, d_name.
    // macOS dirent (64-bit inode): d_ino u64, d_seekoff u64, d_reclen u16, d_namlen u16, d_type u8, d_name.
    internal static int DirentTypeOffset => IsMacOS ? 20 : 18;
    internal static int DirentNameOffset => IsMacOS ? 21 : 19;
    internal const int MacDirentNameLengthOffset = 18;

    private static int OpenDirectoryFlags => IsMacOS
        ? Libc.O_NONBLOCK_MACOS | Libc.O_CLOEXEC_MACOS
        : Libc.O_NONBLOCK_LINUX | Libc.O_CLOEXEC_LINUX;

    private static int AtFdCwd => IsMacOS ? Libc.AT_FDCWD_MACOS : Libc.AT_FDCWD_LINUX;

    private static Platform DetectPlatform()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (arch is not (Architecture.X64 or Architecture.Arm64)) return Platform.Unsupported;
        if (OperatingSystem.IsLinux()) return Platform.Linux;
        if (OperatingSystem.IsMacOS()) return arch == Architecture.X64 ? Platform.MacOSInode64 : Platform.MacOS;
        return Platform.Unsupported;
    }

    /// <summary>Opens a directory stream. Returns 0 on success, otherwise the errno.</summary>
    internal static int OpenDirectory(byte* path, out nint dir, out int fd)
    {
        fd = Libc.open(path, OpenDirectoryFlags);
        return WrapOpenDirectoryFd(fd, out dir);
    }

    /// <summary>Opens a directory stream relative to <paramref name="dirFd"/>. Returns 0 on success, otherwise the errno.</summary>
    internal static int OpenDirectoryRelative(int dirFd, byte* name, out nint dir, out int fd)
    {
        fd = Libc.openat(dirFd, name, OpenDirectoryFlags);
        return WrapOpenDirectoryFd(fd, out dir);
    }

    private static int WrapOpenDirectoryFd(int fd, out nint dir)
    {
        dir = 0;
        if (fd < 0) return Marshal.GetLastPInvokeError();

        dir = s_platform == Platform.MacOSInode64 ? Libc.fdopendir_inode64(fd) : Libc.fdopendir(fd);
        if (dir != 0) return 0;

        int error = Marshal.GetLastPInvokeError();
        _ = Libc.close(fd);
        return error;
    }

    internal static byte* ReadEntry(nint dir)
    {
        Marshal.SetLastPInvokeError(0);
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
                        Libc.STATX_INO | Libc.STATX_SIZE | Libc.STATX_BLOCKS | Libc.STATX_MTIME;
            if (Libc.statx(dirFd, path, flags, mask, buffer) != 0)
            {
                errno = Marshal.GetLastPInvokeError();
                stat = default;
                return false;
            }

            // struct statx: nlink u32 @16, mode u16 @28, ino u64 @32, size u64 @40,
            // blocks u64 @48, mtime.tv_sec i64 @112, mtime.tv_nsec u32 @120, dev_major u32 @136, dev_minor u32 @140.
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
                mtimeNsec: *(uint*)(buffer + 120));
            errno = 0;
            return true;
        }

        int atFlags = followSymlinks ? 0 : Libc.AT_SYMLINK_NOFOLLOW_MACOS;
        int result = s_platform == Platform.MacOSInode64
            ? Libc.fstatat_inode64(dirFd, path, buffer, atFlags)
            : Libc.fstatat(dirFd, path, buffer, atFlags);
        if (result != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            stat = default;
            return false;
        }

        // 64-bit inode struct stat: dev u32 @0, mode u16 @4, nlink u16 @6, ino u64 @8,
        // mtime.tv_sec i64 @48, mtime.tv_nsec i64 @56, size i64 @96, blocks i64 @104, flags u32 @116.
        stat = new NativeStat(
            mode: *(ushort*)(buffer + 4),
            size: *(long*)(buffer + 96),
            blocks: *(long*)(buffer + 104),
            flags: *(uint*)(buffer + 116),
            device: *(uint*)(buffer + 0),
            inode: *(ulong*)(buffer + 8),
            hardLinks: *(ushort*)(buffer + 6),
            mtimeSec: *(long*)(buffer + 48),
            mtimeNsec: (uint)*(long*)(buffer + 56));
        errno = 0;
        return true;
    }

    /// <summary>Stats a null-terminated path relative to the current working directory.</summary>
    internal static bool TryStatPath(ReadOnlySpan<byte> nullTerminatedPath, bool followSymlinks, Span<byte> buffer, out NativeStat stat, out int errno)
    {
        if (nullTerminatedPath.IsEmpty || nullTerminatedPath[^1] != 0)
            throw new ArgumentException("Path must be null-terminated.", nameof(nullTerminatedPath));
        if (buffer.Length < StatBufferSize)
            throw new ArgumentException("Buffer is smaller than StatBufferSize.", nameof(buffer));

        fixed (byte* p = nullTerminatedPath)
        fixed (byte* b = buffer)
        {
            return TryStatAt(AtFdCwd, p, followSymlinks, b, out stat, out errno);
        }
    }

    /// <summary>Maps an errno to the same messages the managed scanner reports.</summary>
    internal static string DescribeError(int errno) => errno switch
    {
        Libc.EACCES or Libc.EPERM => "Access Denied",
        Libc.ENOENT or Libc.ENOTDIR => "Directory Not Found",
        _ => Marshal.GetPInvokeErrorMessage(errno)
    };

    /// <summary>Returns the exact POSIX strerror message matching libc/GNU tools.</summary>
    internal static string DescribePosixError(int errno) => Marshal.GetPInvokeErrorMessage(errno);

    /// <summary>Restores default SIGPIPE termination so downstream pipe closes yield exit code 141 without .NET exceptions.</summary>
    internal static void RestoreDefaultSigpipe()
    {
        if (s_platform != Platform.Unsupported)
            _ = Libc.signal(Libc.SIGPIPE, 0);
    }

    /// <summary>
    /// Binds every entry point once and checks the struct layouts against "/", so a platform
    /// with unexpected symbols or layouts falls back to the managed scanner instead of misreading data.
    /// </summary>
    private static bool Probe()
    {
        if (s_platform == Platform.Unsupported) return false;

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
