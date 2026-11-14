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
    {
        Mode = mode;
        Size = size;
        Blocks = blocks;
        Flags = flags;
    }

    public int Mode { get; }
    public long Size { get; }

    /// <summary>Allocated size in 512-byte units.</summary>
    public long Blocks { get; }

    public uint Flags { get; }

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
        dir = 0;
        fd = Libc.open(path, OpenDirectoryFlags);
        if (fd < 0) return Marshal.GetLastPInvokeError();

        dir = s_platform == Platform.MacOSInode64 ? Libc.fdopendir_inode64(fd) : Libc.fdopendir(fd);
        if (dir != 0) return 0;

        int error = Marshal.GetLastPInvokeError();
        Libc.close(fd);
        fd = -1;
        return error;
    }

    internal static byte* ReadEntry(nint dir) =>
        s_platform == Platform.MacOSInode64 ? Libc.readdir_inode64(dir) : Libc.readdir(dir);

    internal static NativeEntryKind KindFromDirentType(byte type) => type switch
    {
        Libc.DT_DIR => NativeEntryKind.Directory,
        Libc.DT_REG => NativeEntryKind.File,
        Libc.DT_LNK => NativeEntryKind.Symlink,
        Libc.DT_UNKNOWN => NativeEntryKind.Unknown,
        _ => NativeEntryKind.Other
    };

    /// <summary>Stats <paramref name="path"/> relative to <paramref name="dirFd"/>. <paramref name="buffer"/> must hold <see cref="StatBufferSize"/> bytes.</summary>
    internal static bool TryStatAt(int dirFd, byte* path, bool followSymlinks, byte* buffer, out NativeStat stat)
    {
        if (s_platform == Platform.Linux)
        {
            int flags = followSymlinks ? 0 : Libc.AT_SYMLINK_NOFOLLOW_LINUX;
            uint mask = Libc.STATX_TYPE | Libc.STATX_MODE | Libc.STATX_SIZE | Libc.STATX_BLOCKS;
            if (Libc.statx(dirFd, path, flags, mask, buffer) != 0)
            {
                stat = default;
                return false;
            }

            // struct statx: stx_mode u16 @28, stx_size u64 @40, stx_blocks u64 @48
            stat = new NativeStat(*(ushort*)(buffer + 28), *(long*)(buffer + 40), *(long*)(buffer + 48), 0);
            return true;
        }

        int atFlags = followSymlinks ? 0 : Libc.AT_SYMLINK_NOFOLLOW_MACOS;
        int result = s_platform == Platform.MacOSInode64
            ? Libc.fstatat_inode64(dirFd, path, buffer, atFlags)
            : Libc.fstatat(dirFd, path, buffer, atFlags);
        if (result != 0)
        {
            stat = default;
            return false;
        }

        // 64-bit inode struct stat: st_mode u16 @4, st_size i64 @96, st_blocks i64 @104, st_flags u32 @116
        stat = new NativeStat(*(ushort*)(buffer + 4), *(long*)(buffer + 96), *(long*)(buffer + 104), *(uint*)(buffer + 116));
        return true;
    }

    /// <summary>Maps an errno to the same messages the managed scanner reports.</summary>
    internal static string DescribeError(int errno) => errno switch
    {
        Libc.EACCES or Libc.EPERM => "Access Denied",
        Libc.ENOENT or Libc.ENOTDIR => "Directory Not Found",
        _ => Marshal.GetPInvokeErrorMessage(errno)
    };

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
