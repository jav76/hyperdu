using System.Runtime.InteropServices;

namespace hyperdu.Core.Native;

/// <summary>
/// Raw Windows kernel32 / ntdll bindings. Callers must go through <see cref="WindowsIo"/>
/// and <see cref="NativeIo"/>; nothing outside <c>hyperdu.Core.Native</c> imports these directly.
/// </summary>
internal static unsafe partial class Win32
{
    internal const nint INVALID_HANDLE_VALUE = -1;

    // Desired access flags (FILE_READ_ATTRIBUTES works even on exclusive-locked files).
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_OPEN = 0x00000001;

    // CreateFileW dwFlagsAndAttributes
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // NtOpenFile OpenOptions
    internal const uint FILE_DIRECTORY_FILE = 0x00000001;
    internal const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;
    internal const uint FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000;
    internal const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    internal const uint OBJ_CASE_INSENSITIVE = 0x00000040;

    // FILE_INFORMATION_CLASS for NtQueryDirectoryFile
    internal const int FileIdFullDirectoryInformation = 38;

    // FILE_INFO_BY_HANDLE_CLASS for GetFileInformationByHandleEx
    internal const int FileBasicInfo = 0;
    internal const int FileStandardInfo = 1;
    internal const int FileAttributeTagInfo = 9;

    internal const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // IsReparseTagNameSurrogate(tag) == ((tag & 0x20000000) != 0): covers symlinks (0xA000000C),
    // mount points / junctions (0xA0000003), and LX symlinks (0xA000001D), while excluding WOF/Cloud tags.
    internal const uint IO_REPARSE_TAG_SURROGATE_BIT = 0x20000000;

    internal const int STATUS_SUCCESS = 0;
    internal const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    internal const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    internal const int STATUS_NO_SUCH_FILE = unchecked((int)0xC000000F);

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_DIRECTORY = 267;
    internal const int ERROR_CANT_RESOLVE_FILENAME = 1921;

    // FILE_ID_FULL_DIR_INFORMATION field offsets (identical on x86, x64, and arm64).
    internal const int FdiOffsetNextEntryOffset = 0;
    internal const int FdiOffsetLastAccessTime = 16;
    internal const int FdiOffsetLastWriteTime = 24;
    internal const int FdiOffsetChangeTime = 32;
    internal const int FdiOffsetEndOfFile = 40;
    internal const int FdiOffsetAllocationSize = 48;
    internal const int FdiOffsetFileAttributes = 56;
    internal const int FdiOffsetFileNameLength = 60;
    internal const int FdiOffsetEaSize = 64;
    internal const int FdiOffsetFileId = 72;
    internal const int FdiOffsetFileName = 80;

    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public nint RootDirectory;
        public UNICODE_STRING* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_STATUS_BLOCK
    {
        public nint StatusOrPointer;
        public nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME_U32
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME_U32 ftCreationTime;
        public FILETIME_U32 ftLastAccessTime;
        public FILETIME_U32 ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        private uint _reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
        private ushort _reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    internal static bool IsNameSurrogateReparseTag(uint tag) =>
        (tag & IO_REPARSE_TAG_SURROGATE_BIT) != 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateFileW(
        char* lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        nint hFile,
        BY_HANDLE_FILE_INFORMATION* lpFileInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        nint hFile,
        int fileInformationClass,
        void* lpFileInformation,
        uint dwBufferSize);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtOpenFile(
        nint* fileHandle,
        uint desiredAccess,
        OBJECT_ATTRIBUTES* objectAttributes,
        IO_STATUS_BLOCK* ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryDirectoryFile(
        nint fileHandle,
        nint @event,
        nint apcRoutine,
        nint apcContext,
        IO_STATUS_BLOCK* ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass,
        byte returnSingleEntry,
        UNICODE_STRING* fileName,
        byte restartScan);

    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);
}
