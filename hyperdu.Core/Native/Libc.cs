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
    internal const int EACCES = 13;
    internal const int ENOTDIR = 20;

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
    internal const uint STATX_SIZE = 0x200;
    internal const uint STATX_BLOCKS = 0x400;

    internal const uint UF_HIDDEN = 0x8000;

    // open(2) is variadic; declaring only the two fixed arguments is ABI-safe because O_CREAT is never passed.
    [LibraryImport(Lib, SetLastError = true)]
    internal static partial int open(byte* path, int flags);

    [LibraryImport(Lib)]
    internal static partial int close(int fd);

    [LibraryImport(Lib)]
    internal static partial int closedir(nint dir);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial nint fdopendir(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    internal static partial byte* readdir(nint dir);

    [LibraryImport(Lib)]
    internal static partial int statx(int dirFd, byte* path, int flags, uint mask, byte* statxBuffer);

    [LibraryImport(Lib)]
    internal static partial int fstatat(int dirFd, byte* path, byte* statBuffer, int flags);

    // On Intel macOS the plain symbols use the legacy 32-bit inode layout; these are the 64-bit inode variants.
    [LibraryImport(Lib, EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    internal static partial nint fdopendir_inode64(int fd);

    [LibraryImport(Lib, EntryPoint = "readdir$INODE64", SetLastError = true)]
    internal static partial byte* readdir_inode64(nint dir);

    [LibraryImport(Lib, EntryPoint = "fstatat$INODE64")]
    internal static partial int fstatat_inode64(int dirFd, byte* path, byte* statBuffer, int flags);
}
