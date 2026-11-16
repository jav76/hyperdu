using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Unix-specific P/Invoke helpers for creating hard links and deep directory trees beyond PATH_MAX.
/// </summary>
internal static unsafe partial class UnixNativeFs
{
    private const string Lib = "libc";
    private const int O_RDONLY = 0x0;
    private static int AtRemovedir => OperatingSystem.IsMacOS() ? 0x80 : 0x200;

    private static readonly bool s_isMacOSInode64 =
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int link(byte[] existingPath, byte[] newPath);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int mkdirat(int dirFd, byte* pathname, uint mode);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int openat(int dirFd, byte* pathname, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int unlinkat(int dirFd, byte* pathname, int flags);

    /// <summary>Creates a hard link on Unix. Returns false on Windows or unsupported filesystems.</summary>
    public static bool TryCreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
            return false;

        byte[] existingBytes = NullTerminate(existingPath);
        byte[] newBytes = NullTerminate(newPath);
        return link(existingBytes, newBytes) == 0;
    }

    /// <summary>
    /// Creates a nested directory chain of <paramref name="depth"/> levels using fd-relative mkdirat/openat on Unix
    /// so depths beyond PATH_MAX succeed cleanly. Places a small file every <paramref name="fileInterval"/> levels
    /// while the full path stays under OS path limits.
    /// </summary>
    public static (int CreatedDirs, int CreatedFiles) CreateDeepChain(
        string rootPath,
        int depth,
        int fileInterval,
        int fileSize)
    {
        Directory.CreateDirectory(rootPath);
        int createdDirs = 1;
        int createdFiles = 0;

        if (OperatingSystem.IsWindows())
        {
            string current = rootPath;
            byte[] payload = new byte[fileSize];
            for (int level = 1; level <= depth; level++)
            {
                string next = Path.Combine(current, $"d_{level:D3}_segment12");
                if (next.Length >= 240)
                    break;
                Directory.CreateDirectory(next);
                createdDirs++;
                if (fileInterval > 0 && level % fileInterval == 0)
                {
                    File.WriteAllBytes(Path.Combine(next, $"leaf_{level:D3}.dat"), payload);
                    createdFiles++;
                }
                current = next;
            }

            return (createdDirs, createdFiles);
        }

        byte[] rootBytes = NullTerminate(rootPath);
        int currentFd;
        fixed (byte* rootPtr = rootBytes)
        {
            currentFd = openat(OperatingSystem.IsMacOS() ? Libc.AT_FDCWD_MACOS : Libc.AT_FDCWD_LINUX, rootPtr, O_RDONLY);
        }

        if (currentFd < 0)
            throw new IOException($"Failed to open deep-chain root '{rootPath}' (errno {Marshal.GetLastPInvokeError()}).");

        try
        {
            string trackedPath = rootPath;
            bool pathFitsOsLimit = true;
            int maxSafePath = OperatingSystem.IsMacOS() ? 950 : 3900;
            byte[] payload = new byte[fileSize];

            for (int level = 1; level <= depth; level++)
            {
                string segment = $"d_{level:D3}_segment12";
                byte[] segmentBytes = NullTerminate(segment);
                int nextFd;

                fixed (byte* segPtr = segmentBytes)
                {
                    if (mkdirat(currentFd, segPtr, 0x1ED) != 0) // 0755
                    {
                        throw new IOException($"mkdirat failed at level {level} (errno {Marshal.GetLastPInvokeError()}).");
                    }

                    nextFd = openat(currentFd, segPtr, O_RDONLY);
                    if (nextFd < 0)
                    {
                        throw new IOException($"openat failed at level {level} (errno {Marshal.GetLastPInvokeError()}).");
                    }
                }

                _ = Libc.close(currentFd);
                currentFd = nextFd;
                createdDirs++;

                if (pathFitsOsLimit)
                {
                    string candidate = Path.Combine(trackedPath, segment);
                    if (candidate.Length + 20 >= maxSafePath)
                    {
                        pathFitsOsLimit = false;
                    }
                    else
                    {
                        trackedPath = candidate;
                        if (fileInterval > 0 && level % fileInterval == 0)
                        {
                            File.WriteAllBytes(Path.Combine(trackedPath, $"leaf_{level:D3}.dat"), payload);
                            createdFiles++;
                        }
                    }
                }
            }
        }
        finally
        {
            if (currentFd >= 0)
                _ = Libc.close(currentFd);
        }

        return (createdDirs, createdFiles);
    }

    /// <summary>
    /// Recursively deletes a directory tree using fd-relative syscalls on Unix so deep chains beyond PATH_MAX are cleaned up.
    /// </summary>
    public static void DeleteDirectoryRecursive(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return;

        if (OperatingSystem.IsWindows() || !NativeIo.IsSupported)
        {
            Directory.Delete(rootPath, true);
            return;
        }

        byte[] rootBytes = NullTerminate(rootPath);
        int atFdCwd = OperatingSystem.IsMacOS() ? Libc.AT_FDCWD_MACOS : Libc.AT_FDCWD_LINUX;
        int rootFd;
        fixed (byte* rootPtr = rootBytes)
        {
            rootFd = openat(atFdCwd, rootPtr, O_RDONLY);
        }

        if (rootFd < 0)
        {
            Directory.Delete(rootPath, true);
            return;
        }

        DeleteChildrenByFd(rootFd);
        // rootFd is closed by fdopendir/closedir inside DeleteChildrenByFd.
        try
        {
            Directory.Delete(rootPath, false);
        }
        catch
        {
            // Best-effort cleanup of root directory itself.
        }
    }

    private static void DeleteChildrenByFd(int dirFd)
    {
        nint dir = s_isMacOSInode64 ? Libc.fdopendir_inode64(dirFd) : Libc.fdopendir(dirFd);
        if (dir == 0)
        {
            _ = Libc.close(dirFd);
            return;
        }

        try
        {
            List<(byte[] Name, bool IsDirectory)> entries = new();
            while (true)
            {
                byte* entry = NativeIo.ReadEntry(dir);
                if (entry == null)
                    break;

                byte* namePtr = entry + NativeIo.DirentNameOffset;
                if (namePtr[0] == '.' && (namePtr[1] == 0 || (namePtr[1] == '.' && namePtr[2] == 0)))
                    continue;

                int nameLength = NativeIo.IsMacOS
                    ? *(ushort*)(entry + NativeIo.MacDirentNameLengthOffset)
                    : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(namePtr).Length;

                byte[] nullTerminatedName = new byte[nameLength + 1];
                new ReadOnlySpan<byte>(namePtr, nameLength).CopyTo(nullTerminatedName);
                NativeEntryKind kind = NativeIo.KindFromDirentType(entry[NativeIo.DirentTypeOffset]);
                entries.Add((nullTerminatedName, kind == NativeEntryKind.Directory));
            }

            foreach ((byte[] nameBytes, bool isDirectory) in entries)
            {
                fixed (byte* namePtr = nameBytes)
                {
                    if (isDirectory)
                    {
                        int childFd = openat(dirFd, namePtr, O_RDONLY);
                        if (childFd >= 0)
                        {
                            DeleteChildrenByFd(childFd);
                        }

                        _ = unlinkat(dirFd, namePtr, AtRemovedir);
                    }
                    else
                    {
                        if (unlinkat(dirFd, namePtr, 0) != 0)
                        {
                            int childFd = openat(dirFd, namePtr, O_RDONLY);
                            if (childFd >= 0)
                            {
                                DeleteChildrenByFd(childFd);
                                _ = unlinkat(dirFd, namePtr, AtRemovedir);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            _ = Libc.closedir(dir);
        }
    }

    private static byte[] NullTerminate(string path)
    {
        int len = Encoding.UTF8.GetByteCount(path);
        byte[] bytes = new byte[len + 1];
        Encoding.UTF8.GetBytes(path, bytes);
        return bytes;
    }
}
