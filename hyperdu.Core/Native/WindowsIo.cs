using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;

namespace hyperdu.Core.Native;

/// <summary>
/// Windows kernel directory enumeration and metadata access using CreateFileW / NtOpenFile
/// and NtQueryDirectoryFile (FileIdFullDirectoryInformation, class 38).
/// </summary>
internal static unsafe class WindowsIo
{
    private const int StackCharThreshold = 512;
    private const int CreateFileMaxPath = 32700;
    private const int NtStepChunkLimit = 16000;
    private const long WindowsToUnixEpochTicks = 116_444_736_000_000_000L;

    internal static bool IsNotFoundError(int win32Error) =>
        win32Error is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND;

    internal static string DescribeError(int win32Error) => win32Error switch
    {
        Win32.ERROR_ACCESS_DENIED => "Access Denied",
        Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND or Win32.ERROR_DIRECTORY => "Directory Not Found",
        _ => Marshal.GetPInvokeErrorMessage(win32Error)
    };

    internal static int ToWin32Error(int ntStatus)
    {
        uint err = Win32.RtlNtStatusToDosError(ntStatus);
        return err != 0 ? (int)err : 1;
    }

    internal static bool TryStatPath(ReadOnlySpan<byte> utf8Path, bool followSymlinks, out NativeStat stat, out int win32Error)
    {
        stat = default;
        if (!OpenHandlePathSafe(
                utf8Path,
                Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
                directoryOnly: false,
                followSymlinks: followSymlinks,
                out nint handle,
                out _,
                out win32Error))
        {
            return false;
        }

        try
        {
            return TryStatHandle(handle, followSymlinks, out stat, out win32Error);
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    internal static bool TryStatRelative(
        nint parentDirHandle,
        ReadOnlySpan<char> childName,
        bool followSymlinks,
        out NativeStat stat,
        out int win32Error)
    {
        stat = default;
        if (parentDirHandle == Win32.INVALID_HANDLE_VALUE || parentDirHandle == 0 || childName.IsEmpty)
        {
            win32Error = Win32.ERROR_INVALID_NAME;
            return false;
        }

        uint options = Win32.FILE_SYNCHRONOUS_IO_NONALERT |
                       Win32.FILE_OPEN_FOR_BACKUP_INTENT |
                       (followSymlinks ? 0u : Win32.FILE_OPEN_REPARSE_POINT);

        if (!TryNtOpenRelative(
                parentDirHandle,
                childName,
                Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
                options,
                out nint handle,
                out win32Error))
        {
            return false;
        }

        try
        {
            return TryStatHandle(handle, followSymlinks, out stat, out win32Error);
        }
        finally
        {
            _ = Win32.CloseHandle(handle);
        }
    }

    internal static bool OpenDirectory(
        ReadOnlySpan<byte> utf8Path,
        bool followSymlinks,
        out nint handle,
        out ulong volumeSerial,
        out int win32Error) =>
        OpenHandlePathSafe(
            utf8Path,
            Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
            directoryOnly: true,
            followSymlinks: followSymlinks,
            out handle,
            out volumeSerial,
            out win32Error);

    internal static bool OpenRelativeDirectory(
        nint parentHandle,
        ReadOnlySpan<byte> utf8ChildName,
        bool followSymlinks,
        ulong parentVolumeSerial,
        out nint childHandle,
        out ulong volumeSerial,
        out int win32Error)
    {
        childHandle = Win32.INVALID_HANDLE_VALUE;
        volumeSerial = parentVolumeSerial;
        if (utf8ChildName.Length > 0 && utf8ChildName[^1] == 0)
            utf8ChildName = utf8ChildName[..^1];

        if (parentHandle == Win32.INVALID_HANDLE_VALUE || parentHandle == 0 || utf8ChildName.IsEmpty)
        {
            win32Error = Win32.ERROR_INVALID_NAME;
            return false;
        }

        int maxChars = Encoding.UTF8.GetMaxCharCount(utf8ChildName.Length);
        char[]? rented = null;
        Span<char> nameChars = maxChars <= StackCharThreshold
            ? stackalloc char[StackCharThreshold]
            : (rented = ArrayPool<char>.Shared.Rent(maxChars));

        try
        {
            int charCount = Encoding.UTF8.GetChars(utf8ChildName, nameChars);
            uint options = Win32.FILE_DIRECTORY_FILE |
                           Win32.FILE_SYNCHRONOUS_IO_NONALERT |
                           Win32.FILE_OPEN_FOR_BACKUP_INTENT |
                           (followSymlinks ? 0u : Win32.FILE_OPEN_REPARSE_POINT);

            if (!TryNtOpenRelative(
                    parentHandle,
                    nameChars[..charCount],
                    Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
                    options,
                    out childHandle,
                    out win32Error))
            {
                return false;
            }

            if (followSymlinks)
            {
                Win32.BY_HANDLE_FILE_INFORMATION info;
                if (Win32.GetFileInformationByHandle(childHandle, &info))
                    volumeSerial = info.dwVolumeSerialNumber;
            }

            return true;
        }
        finally
        {
            if (rented != null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    internal static bool TryStatHandle(nint handle, bool followSymlinks, out NativeStat stat, out int win32Error)
    {
        stat = default;
        if (handle == Win32.INVALID_HANDLE_VALUE || handle == 0)
        {
            win32Error = Win32.ERROR_INVALID_NAME;
            return false;
        }

        Win32.BY_HANDLE_FILE_INFORMATION byHandle;
        if (!Win32.GetFileInformationByHandle(handle, &byHandle))
        {
            win32Error = Marshal.GetLastPInvokeError();
            if (win32Error == 0)
                win32Error = Win32.ERROR_ACCESS_DENIED;
            return false;
        }

        uint attrs = byHandle.dwFileAttributes;
        ulong device = byHandle.dwVolumeSerialNumber;
        ulong fileId = ((ulong)byHandle.nFileIndexHigh << 32) | byHandle.nFileIndexLow;
        uint hardLinks = byHandle.nNumberOfLinks;

        long allocationSize;
        long endOfFile;
        Win32.FILE_STANDARD_INFO stdInfo;
        if (Win32.GetFileInformationByHandleEx(handle, Win32.FileStandardInfo, &stdInfo, (uint)sizeof(Win32.FILE_STANDARD_INFO)))
        {
            allocationSize = stdInfo.AllocationSize;
            endOfFile = stdInfo.EndOfFile;
        }
        else
        {
            endOfFile = (long)(((ulong)byHandle.nFileSizeHigh << 32) | byHandle.nFileSizeLow);
            allocationSize = endOfFile;
        }

        long atimeTicks;
        long mtimeTicks;
        long ctimeTicks;
        Win32.FILE_BASIC_INFO basicInfo;
        if (Win32.GetFileInformationByHandleEx(handle, Win32.FileBasicInfo, &basicInfo, (uint)sizeof(Win32.FILE_BASIC_INFO)))
        {
            atimeTicks = basicInfo.LastAccessTime;
            mtimeTicks = basicInfo.LastWriteTime;
            ctimeTicks = basicInfo.ChangeTime;
            attrs = basicInfo.FileAttributes;
        }
        else
        {
            atimeTicks = CombineFileTime(byHandle.ftLastAccessTime);
            mtimeTicks = CombineFileTime(byHandle.ftLastWriteTime);
            ctimeTicks = mtimeTicks;
        }

        bool isSymlink = false;
        if (!followSymlinks && (attrs & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            Win32.FILE_ATTRIBUTE_TAG_INFO tagInfo;
            if (Win32.GetFileInformationByHandleEx(handle, Win32.FileAttributeTagInfo, &tagInfo, (uint)sizeof(Win32.FILE_ATTRIBUTE_TAG_INFO)))
                isSymlink = Win32.IsNameSurrogateReparseTag(tagInfo.ReparseTag);
        }

        int mode = isSymlink
            ? Libc.S_IFLNK
            : (attrs & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0 ? Libc.S_IFDIR : Libc.S_IFREG;
        long blocks = allocationSize > 0 ? (allocationSize + 511) >> 9 : 0;

        FileTimestamp atime = FileTimeToUnix(atimeTicks);
        FileTimestamp mtime = FileTimeToUnix(mtimeTicks);
        FileTimestamp ctime = FileTimeToUnix(ctimeTicks);

        stat = new NativeStat(
            mode: mode,
            size: endOfFile,
            blocks: blocks,
            flags: attrs,
            device: device,
            inode: fileId,
            hardLinks: hardLinks,
            mtimeSec: mtime.Seconds,
            mtimeNsec: mtime.Nanoseconds,
            atimeSec: atime.Seconds,
            atimeNsec: atime.Nanoseconds,
            ctimeSec: ctime.Seconds,
            ctimeNsec: ctime.Nanoseconds);
        win32Error = 0;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeStat BuildStatFromFileIdFullDirEntry(byte* entry, ulong volumeSerial)
    {
        uint attrs = *(uint*)(entry + Win32.FdiOffsetFileAttributes);
        uint reparseTag = *(uint*)(entry + Win32.FdiOffsetEaSize);
        bool isSymlink = (attrs & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
                         Win32.IsNameSurrogateReparseTag(reparseTag);

        int mode = isSymlink
            ? Libc.S_IFLNK
            : (attrs & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0 ? Libc.S_IFDIR : Libc.S_IFREG;

        long endOfFile = *(long*)(entry + Win32.FdiOffsetEndOfFile);
        long allocationSize = *(long*)(entry + Win32.FdiOffsetAllocationSize);
        long blocks = allocationSize > 0 ? (allocationSize + 511) >> 9 : 0;
        ulong fileId = (ulong)*(long*)(entry + Win32.FdiOffsetFileId);

        FileTimestamp atime = FileTimeToUnix(*(long*)(entry + Win32.FdiOffsetLastAccessTime));
        FileTimestamp mtime = FileTimeToUnix(*(long*)(entry + Win32.FdiOffsetLastWriteTime));
        FileTimestamp ctime = FileTimeToUnix(*(long*)(entry + Win32.FdiOffsetChangeTime));

        // FileIdFullDirectoryInformation omits nNumberOfLinks; hardLinks: 0 flags unknown link count so callers deduplicate on (volumeSerial, fileId).
        return new NativeStat(
            mode: mode,
            size: endOfFile,
            blocks: blocks,
            flags: attrs,
            device: volumeSerial,
            inode: fileId,
            hardLinks: 0,
            mtimeSec: mtime.Seconds,
            mtimeNsec: mtime.Nanoseconds,
            atimeSec: atime.Seconds,
            atimeNsec: atime.Nanoseconds,
            ctimeSec: ctime.Seconds,
            ctimeNsec: ctime.Nanoseconds);
    }

    internal static bool Probe()
    {
        try
        {
            string sysDir = Environment.SystemDirectory;
            if (string.IsNullOrEmpty(sysDir))
                sysDir = AppContext.BaseDirectory;

            byte[] pathBytes = Encoding.UTF8.GetBytes(sysDir + "\0");
            using NativeDirectory? dir = NativeDirectory.Open(pathBytes, out int error);
            if (dir == null || error != 0)
                return false;

            Span<byte> statBuf = stackalloc byte[NativeIo.StatBufferSize];
            if (!dir.TryStatSelf(statBuf, out NativeStat selfStat) || selfStat.Kind != NativeEntryKind.Directory)
                return false;

            while (dir.MoveNext())
            {
                if (dir.CurrentName.IsEmpty || !dir.TryGetInlineStat(out _))
                    return false;
            }

            return dir.Error == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool OpenHandlePathSafe(
        ReadOnlySpan<byte> utf8Path,
        uint desiredAccess,
        bool directoryOnly,
        bool followSymlinks,
        out nint handle,
        out ulong volumeSerial,
        out int win32Error)
    {
        handle = Win32.INVALID_HANDLE_VALUE;
        volumeSerial = 0;

        if (!TryToExtendedPath(utf8Path, out string extended, out win32Error))
            return false;

        uint flags = Win32.FILE_FLAG_BACKUP_SEMANTICS |
                     (followSymlinks ? 0u : Win32.FILE_FLAG_OPEN_REPARSE_POINT);

        if (extended.Length < CreateFileMaxPath)
        {
            fixed (char* pExt = extended)
            {
                handle = Win32.CreateFileW(
                    pExt,
                    desiredAccess,
                    Win32.FILE_SHARE_ALL,
                    0,
                    Win32.OPEN_EXISTING,
                    flags,
                    0);
            }

            if (handle == Win32.INVALID_HANDLE_VALUE)
            {
                win32Error = Marshal.GetLastPInvokeError();
                if (win32Error == 0)
                    win32Error = Win32.ERROR_FILE_NOT_FOUND;
                return false;
            }
        }
        else if (!TryOpenExtendedBeyond32K(extended, desiredAccess, directoryOnly, followSymlinks, out handle, out win32Error))
        {
            return false;
        }

        Win32.BY_HANDLE_FILE_INFORMATION byHandle;
        if (Win32.GetFileInformationByHandle(handle, &byHandle))
        {
            volumeSerial = byHandle.dwVolumeSerialNumber;
            if (directoryOnly && (byHandle.dwFileAttributes & Win32.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                _ = Win32.CloseHandle(handle);
                handle = Win32.INVALID_HANDLE_VALUE;
                win32Error = Win32.ERROR_DIRECTORY;
                return false;
            }

            if (directoryOnly && !followSymlinks && (byHandle.dwFileAttributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                Win32.FILE_ATTRIBUTE_TAG_INFO tagInfo;
                if (Win32.GetFileInformationByHandleEx(handle, Win32.FileAttributeTagInfo, &tagInfo, (uint)sizeof(Win32.FILE_ATTRIBUTE_TAG_INFO)) &&
                    Win32.IsNameSurrogateReparseTag(tagInfo.ReparseTag))
                {
                    _ = Win32.CloseHandle(handle);
                    handle = Win32.INVALID_HANDLE_VALUE;
                    win32Error = Win32.ERROR_DIRECTORY;
                    return false;
                }
            }
        }

        win32Error = 0;
        return true;
    }

    private static bool TryOpenExtendedBeyond32K(
        string extended,
        uint desiredAccess,
        bool directoryOnly,
        bool followSymlinks,
        out nint finalHandle,
        out int win32Error)
    {
        finalHandle = Win32.INVALID_HANDLE_VALUE;
        int minRootLen = FindExtendedRootLength(extended);
        int end = extended.Length;
        while (end > minRootLen && extended[end - 1] == '\\')
            end--;

        ReadOnlySpan<char> pathSpan = extended.AsSpan(0, end);
        int split = FindPrefixBoundary(pathSpan, minRootLen);

        int pos = split;
        while (pos < pathSpan.Length && pathSpan[pos] == '\\')
            pos++;

        bool rootIsFinal = pos >= pathSpan.Length;
        uint rootAccess = rootIsFinal
            ? desiredAccess
            : Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE;
        uint rootFlags = Win32.FILE_FLAG_BACKUP_SEMANTICS |
                         (rootIsFinal && !followSymlinks ? Win32.FILE_FLAG_OPEN_REPARSE_POINT : 0u);

        string rootPart = extended[..split];
        nint cur;
        fixed (char* pRoot = rootPart)
        {
            cur = Win32.CreateFileW(
                pRoot,
                rootAccess,
                Win32.FILE_SHARE_ALL,
                0,
                Win32.OPEN_EXISTING,
                rootFlags,
                0);
        }

        if (cur == Win32.INVALID_HANDLE_VALUE)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }

        while (pos < pathSpan.Length)
        {
            int maxEnd = Math.Min(pathSpan.Length, pos + NtStepChunkLimit);
            int chunkEnd;
            if (maxEnd == pathSpan.Length)
            {
                chunkEnd = maxEnd;
            }
            else
            {
                int sep = pathSpan[pos..maxEnd].LastIndexOf('\\');
                if (sep > 0)
                {
                    chunkEnd = pos + sep;
                }
                else
                {
                    int nextSep = pathSpan[pos..].IndexOf('\\');
                    chunkEnd = nextSep >= 0 ? pos + nextSep : pathSpan.Length;
                }
            }

            int nextPos = chunkEnd;
            while (nextPos < pathSpan.Length && pathSpan[nextPos] == '\\')
                nextPos++;

            bool isFinal = nextPos >= pathSpan.Length;
            bool stepFollow = !isFinal || followSymlinks;
            bool stepDirOnly = !isFinal || directoryOnly;
            uint stepAccess = isFinal
                ? desiredAccess
                : Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE;
            uint stepOptions = Win32.FILE_SYNCHRONOUS_IO_NONALERT |
                               Win32.FILE_OPEN_FOR_BACKUP_INTENT |
                               (stepDirOnly ? Win32.FILE_DIRECTORY_FILE : 0u) |
                               (stepFollow ? 0u : Win32.FILE_OPEN_REPARSE_POINT);

            bool ok = TryNtOpenRelative(cur, pathSpan[pos..chunkEnd], stepAccess, stepOptions, out nint next, out win32Error);
            _ = Win32.CloseHandle(cur);
            if (!ok)
                return false;

            cur = next;
            pos = nextPos;
        }

        finalHandle = cur;
        win32Error = 0;
        return true;
    }

    private static int FindExtendedRootLength(string extended)
    {
        if (extended.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            int serverEnd = extended.IndexOf('\\', 8);
            if (serverEnd >= 0)
            {
                int shareEnd = extended.IndexOf('\\', serverEnd + 1);
                return shareEnd >= 0 ? shareEnd + 1 : extended.Length;
            }

            return extended.Length;
        }

        if (extended.Length >= 7 &&
            extended.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
            extended[5] == ':' &&
            extended[6] == '\\')
        {
            return 7;
        }

        return Math.Min(extended.Length, 4);
    }

    private static int FindPrefixBoundary(ReadOnlySpan<char> path, int minRootLength)
    {
        if (path.Length <= CreateFileMaxPath)
            return path.Length;

        int sep = path[minRootLength..CreateFileMaxPath].LastIndexOf('\\');
        return sep > 0 ? minRootLength + sep : minRootLength;
    }

    private static bool TryNtOpenRelative(
        nint rootHandle,
        ReadOnlySpan<char> relativeName,
        uint desiredAccess,
        uint openOptions,
        out nint handle,
        out int win32Error)
    {
        handle = Win32.INVALID_HANDLE_VALUE;
        int byteLen = relativeName.Length * 2;
        if (byteLen > ushort.MaxValue)
        {
            win32Error = Win32.ERROR_INVALID_NAME;
            return false;
        }

        fixed (char* pName = relativeName)
        {
            Win32.UNICODE_STRING uName = new()
            {
                Length = (ushort)byteLen,
                MaximumLength = (ushort)byteLen,
                Buffer = (nint)pName
            };

            Win32.OBJECT_ATTRIBUTES oa = new()
            {
                Length = (uint)sizeof(Win32.OBJECT_ATTRIBUTES),
                RootDirectory = rootHandle,
                ObjectName = &uName,
                Attributes = Win32.OBJ_CASE_INSENSITIVE,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0
            };

            Win32.IO_STATUS_BLOCK iosb = default;
            nint opened = 0;
            int status = Win32.NtOpenFile(&opened, desiredAccess, &oa, &iosb, Win32.FILE_SHARE_ALL, openOptions);
            if (status < 0)
            {
                win32Error = ToWin32Error(status);
                return false;
            }

            handle = opened;
            win32Error = 0;
            return true;
        }
    }

    private static bool TryToExtendedPath(ReadOnlySpan<byte> utf8Path, out string extended, out int win32Error)
    {
        if (utf8Path.Length > 0 && utf8Path[^1] == 0)
            utf8Path = utf8Path[..^1];

        if (utf8Path.IsEmpty)
        {
            extended = string.Empty;
            win32Error = Win32.ERROR_PATH_NOT_FOUND;
            return false;
        }

        string raw = Encoding.UTF8.GetString(utf8Path);
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal) || raw.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            extended = raw;
            win32Error = 0;
            return true;
        }

        try
        {
            string full = Path.GetFullPath(raw);
            extended = full.StartsWith(@"\\", StringComparison.Ordinal)
                ? string.Concat(@"\\?\UNC\", full.AsSpan(2))
                : string.Concat(@"\\?\", full);
            win32Error = 0;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            extended = string.Empty;
            win32Error = Win32.ERROR_INVALID_NAME;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CombineFileTime(Win32.FILETIME_U32 ft) =>
        ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FileTimestamp FileTimeToUnix(long fileTimeTicks)
    {
        long unixTicks = fileTimeTicks - WindowsToUnixEpochTicks;
        long sec = Math.DivRem(unixTicks, 10_000_000L, out long remTicks);
        if (remTicks < 0)
        {
            sec -= 1;
            remTicks += 10_000_000L;
        }

        return new FileTimestamp(sec, (uint)(remTicks * 100L));
    }
}
