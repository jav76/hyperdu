using System.Runtime.InteropServices;

namespace hyperdu.Core.Native;

/// <summary>
/// Raw libc bindings for Linux and macOS. Callers must go through <see cref="NativeIo"/>,
/// which picks the right entry point and struct layout for the current platform.
/// </summary>
internal static unsafe partial class Libc
{
    private const string Lib = "libc";

    internal const int EPERM = 1;
    internal const int ENOENT = 2;
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int EACCES = 13;
    internal const int ENOTDIR = 20;
    internal const int EPIPE = 32;

    internal const int SIGPIPE = 13;

    internal const byte DT_UNKNOWN = 0;
    internal const byte DT_DIR = 4;
    internal const byte DT_REG = 8;
    internal const byte DT_LNK = 10;

    internal const int S_IFMT = 0xF000;
    internal const int S_IFDIR = 0x4000;
    internal const int S_IFREG = 0x8000;
    internal const int S_IFLNK = 0xA000;

    internal const int O_NONBLOCK_LINUX = 0x800;
    internal const int O_CLOEXEC_LINUX = 0x80000;
    internal const int O_NONBLOCK_MACOS = 0x4;
    internal const int O_CLOEXEC_MACOS = 0x1000000;

    internal const int AT_FDCWD_LINUX = -100;
    internal const int AT_FDCWD_MACOS = -2;
    internal const int AT_SYMLINK_NOFOLLOW_LINUX = 0x100;
    internal const int AT_SYMLINK_NOFOLLOW_MACOS = 0x20;

    internal const uint STATX_TYPE = 0x1;
    internal const uint STATX_MODE = 0x2;
    internal const uint STATX_NLINK = 0x4;
    internal const uint STATX_ATIME = 0x20;
    internal const uint STATX_MTIME = 0x40;
    internal const uint STATX_CTIME = 0x80;
    internal const uint STATX_INO = 0x100;
    internal const uint STATX_SIZE = 0x200;
    internal const uint STATX_BLOCKS = 0x400;

    internal const uint UF_HIDDEN = 0x8000;

    internal const ulong TMPFS_MAGIC = 0x01021994;
    internal const ulong RAMFS_MAGIC = 0x858458F6;
    internal const ulong NFS_SUPER_MAGIC = 0x6969;

    internal const int LC_NUMERIC_MASK_LINUX = 1 << 1;
    internal const int LC_TIME_MASK_LINUX = 1 << 2;
    internal const int LC_ALL_MASK_MACOS = 0x3F;

    // open(2) and openat(2) are variadic; declaring only the fixed arguments is ABI-safe because O_CREAT is never passed.
    [LibraryImport(Lib)]
    internal static partial int open(byte* path, int flags);

    [LibraryImport(Lib)]
    internal static partial int openat(int dirFd, byte* path, int flags);

    [LibraryImport(Lib)]
    internal static partial nint read(int fd, byte* buf, nuint count);

    [LibraryImport(Lib)]
    internal static partial nint write(int fd, byte* buf, nuint count);

    [LibraryImport(Lib)]
    internal static partial int close(int fd);

    [LibraryImport(Lib)]
    internal static partial nint getdents64(int fd, byte* dirp, nuint count);

    [LibraryImport(Lib)]
    internal static partial int fstatfs(int fd, byte* buf);

    [LibraryImport(Lib)]
    internal static partial nint signal(int signum, nint handler);

    [LibraryImport(Lib)]
    internal static partial int closedir(nint dir);

    [LibraryImport(Lib)]
    internal static partial nint fdopendir(int fd);

    [LibraryImport(Lib)]
    internal static partial byte* readdir(nint dir);

    [LibraryImport(Lib)]
    internal static partial int statx(int dirFd, byte* path, int flags, uint mask, byte* statxBuffer);

    [LibraryImport(Lib)]
    internal static partial int fstatat(int dirFd, byte* path, byte* statBuffer, int flags);

    [LibraryImport(Lib, EntryPoint = "__errno_location")]
    [SuppressGCTransition]
    internal static partial int* errno_location_linux();

    [LibraryImport(Lib, EntryPoint = "__error")]
    [SuppressGCTransition]
    internal static partial int* errno_location_macos();

    // On Intel macOS the plain symbols use the legacy 32-bit inode layout; these are the 64-bit inode variants.
    [LibraryImport(Lib, EntryPoint = "fdopendir$INODE64")]
    internal static partial nint fdopendir_inode64(int fd);

    [LibraryImport(Lib, EntryPoint = "readdir$INODE64")]
    internal static partial byte* readdir_inode64(nint dir);

    [LibraryImport(Lib, EntryPoint = "fstatat$INODE64")]
    internal static partial int fstatat_inode64(int dirFd, byte* path, byte* statBuffer, int flags);

    [LibraryImport(Lib)]
    internal static partial void tzset();

    [LibraryImport(Lib)]
    internal static partial byte* localtime_r(long* timer, byte* result);

    [LibraryImport(Lib)]
    internal static partial nuint strftime(byte* s, nuint max, byte* format, byte* tm);

    [LibraryImport(Lib)]
    internal static partial byte* getenv(byte* name);

    [LibraryImport(Lib)]
    internal static partial int setenv(byte* name, byte* value, int overwrite);

    [LibraryImport(Lib)]
    internal static partial int unsetenv(byte* name);

    [LibraryImport(Lib)]
    internal static partial nint newlocale(int categoryMask, byte* locale, nint baseLocale);

    [LibraryImport(Lib)]
    internal static partial nint uselocale(nint newLoc);

    [LibraryImport(Lib)]
    internal static partial void freelocale(nint loc);

    [LibraryImport(Lib)]
    internal static partial byte** localeconv();
}
