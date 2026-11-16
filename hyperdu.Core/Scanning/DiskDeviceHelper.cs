using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace hyperdu.Core.Scanning;

public static partial class DiskDeviceHelper
{
    // Windows API Constants
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint DRIVE_REMOTE = 4;

    // Structs for Windows P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        ref DeviceSeekPenaltyDescriptor lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string lpRootPathName);

    public static bool IsRotationalDrive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsRotationalDriveWindows(path);
        }
        if (OperatingSystem.IsLinux())
        {
            return IsRotationalDriveLinux(path);
        }
        if (OperatingSystem.IsMacOS())
        {
            return IsRotationalDriveMacOs(path);
        }
        return false; // Default fallback to non-rotational (safe for multi-threading)
    }

    internal static bool IsNetworkDriveWindows(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (IsUncLikePath(path))
                return true;

            string fullPath = NormalizeWindowsDevicePath(Path.GetFullPath(path));
            if (IsUncLikePath(fullPath))
                return true;

            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;

            if (!root.EndsWith('\\'))
                root += "\\";

            return GetDriveTypeW(root) == DRIVE_REMOTE;
        }
        catch
        {
            return IsUncLikePath(path);
        }
    }

    private static bool IsUncLikePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            path.Length >= 6 &&
            path[5] == ':')
        {
            return path.Substring(4);
        }

        return path;
    }

    private static bool IsRotationalDriveMacOs(string path)
    {
        if (!NativeIo.TryGetMacOsMountInfo(path, out _, out string mountFromName))
            return false;

        return IsRotationalMacOsMount(mountFromName);
    }

    internal static unsafe bool IsRotationalMacOsMount(string mountFromName)
    {
        if (!OperatingSystem.IsMacOS() ||
            string.IsNullOrEmpty(mountFromName) ||
            !mountFromName.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return false;
        }

        return MacOsIoKit.IsBsdDeviceRotational(mountFromName.Substring(5));
    }

    private static unsafe partial class MacOsIoKit
    {
        private const string IOKitFramework = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint kIORegistryIterateRecursively = 0x00000001;
        private const uint kIORegistryIterateParents = 0x00000002;
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [LibraryImport(IOKitFramework)]
        private static partial nint IOBSDNameMatching(uint mainPort, uint options, byte* bsdName);

        [LibraryImport(IOKitFramework)]
        private static partial uint IOServiceGetMatchingService(uint mainPort, nint matching);

        [LibraryImport(IOKitFramework)]
        private static partial nint IORegistryEntrySearchCFProperty(
            uint entry,
            byte* plane,
            nint key,
            nint allocator,
            uint options);

        [LibraryImport(IOKitFramework)]
        private static partial int IOObjectRelease(uint obj);

        [LibraryImport(CoreFoundationFramework)]
        private static partial nint CFStringCreateWithCString(nint alloc, byte* cStr, uint encoding);

        [LibraryImport(CoreFoundationFramework)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool CFStringGetCString(nint theString, byte* buffer, nint bufferSize, uint encoding);

        [LibraryImport(CoreFoundationFramework)]
        private static partial nint CFDictionaryGetValue(nint theDict, nint key);

        [LibraryImport(CoreFoundationFramework)]
        private static partial nuint CFGetTypeID(nint cf);

        [LibraryImport(CoreFoundationFramework)]
        private static partial nuint CFDictionaryGetTypeID();

        [LibraryImport(CoreFoundationFramework)]
        private static partial nuint CFStringGetTypeID();

        [LibraryImport(CoreFoundationFramework)]
        private static partial void CFRelease(nint cf);

        internal static bool IsBsdDeviceRotational(string bsdName)
        {
            if (string.IsNullOrEmpty(bsdName))
                return false;

            try
            {
                uint service = FindMediaService(bsdName);
                if (service == 0)
                    return false;

                nint devCharsKey = 0;
                nint mediumTypeKey = 0;
                nint devCharsDict = 0;
                try
                {
                    ReadOnlySpan<byte> devCharsKeyBytes = "Device Characteristics\0"u8;
                    ReadOnlySpan<byte> mediumTypeKeyBytes = "Medium Type\0"u8;
                    fixed (byte* pDevChars = devCharsKeyBytes)
                    fixed (byte* pMediumType = mediumTypeKeyBytes)
                    {
                        devCharsKey = CFStringCreateWithCString(0, pDevChars, kCFStringEncodingUTF8);
                        mediumTypeKey = CFStringCreateWithCString(0, pMediumType, kCFStringEncodingUTF8);
                    }

                    if (devCharsKey == 0 || mediumTypeKey == 0)
                        return false;

                    ReadOnlySpan<byte> servicePlane = "IOService\0"u8;
                    fixed (byte* pPlane = servicePlane)
                    {
                        devCharsDict = IORegistryEntrySearchCFProperty(
                            service,
                            pPlane,
                            devCharsKey,
                            0,
                            kIORegistryIterateRecursively | kIORegistryIterateParents);
                    }

                    if (devCharsDict == 0 || CFGetTypeID(devCharsDict) != CFDictionaryGetTypeID())
                        return false;

                    nint mediumTypeVal = CFDictionaryGetValue(devCharsDict, mediumTypeKey);
                    if (mediumTypeVal == 0 || CFGetTypeID(mediumTypeVal) != CFStringGetTypeID())
                        return false;

                    byte* strBuf = stackalloc byte[64];
                    if (!CFStringGetCString(mediumTypeVal, strBuf, 64, kCFStringEncodingUTF8))
                        return false;

                    ReadOnlySpan<byte> mediumSpan = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(strBuf);
                    return Encoding.UTF8.GetString(mediumSpan).Equals("Rotational", StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    if (devCharsDict != 0) CFRelease(devCharsDict);
                    if (mediumTypeKey != 0) CFRelease(mediumTypeKey);
                    if (devCharsKey != 0) CFRelease(devCharsKey);
                    _ = IOObjectRelease(service);
                }
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

        private static uint FindMediaService(string bsdName)
        {
            string candidate = bsdName;
            while (!string.IsNullOrEmpty(candidate))
            {
                int byteLen = Encoding.UTF8.GetByteCount(candidate);
                byte[] buf = new byte[byteLen + 1];
                Encoding.UTF8.GetBytes(candidate, buf);
                fixed (byte* pName = buf)
                {
                    nint matching = IOBSDNameMatching(0, 0, pName);
                    if (matching != 0)
                    {
                        uint service = IOServiceGetMatchingService(0, matching);
                        if (service != 0)
                            return service;
                    }
                }

                // Step back from snapshot/partition slice (e.g. disk1s5s1 -> disk1s5 -> disk1).
                int lastSlice = candidate.LastIndexOf('s');
                if (lastSlice <= 4)
                    break;

                bool allDigits = true;
                for (int i = lastSlice + 1; i < candidate.Length; i++)
                {
                    if (!char.IsAsciiDigit(candidate[i]))
                    {
                        allDigits = false;
                        break;
                    }
                }

                if (!allDigits || lastSlice + 1 == candidate.Length)
                    break;

                candidate = candidate.Substring(0, lastSlice);
            }

            return 0;
        }
    }

    private static bool IsRotationalDriveWindows(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                return false;

            string volumePath = string.Concat(@"\\.\", root.AsSpan(0, 2));

            using SafeFileHandle handle = CreateFile(
                volumePath,
                0, // No access requested, we only want metadata/attributes query
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            var query = new StoragePropertyQuery
            {
                PropertyId = 7, // StorageDeviceSeekPenaltyProperty
                QueryType = 0,  // PropertyStandardQuery
                AdditionalParameters = 0
            };

            var descriptor = new DeviceSeekPenaltyDescriptor();
            uint bytesReturned = 0;

            bool success = DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                ref query,
                (uint)Marshal.SizeOf(query),
                ref descriptor,
                (uint)Marshal.SizeOf(descriptor),
                out bytesReturned,
                IntPtr.Zero);

            if (success)
            {
                return descriptor.IncursSeekPenalty != 0;
            }
        }
        catch
        {
            // Fallback
        }
        return false;
    }

    private static bool IsRotationalDriveLinux(string path)
    {
        if (!MountHeuristics.TryGetLinuxMountInfo(path, out string bestMatchDevice, out _) ||
            string.IsNullOrEmpty(bestMatchDevice))
        {
            return false;
        }

        return IsRotationalBlockDevice(bestMatchDevice);
    }

    internal static bool IsRotationalBlockDevice(string devicePath)
    {
        try
        {
            if (string.IsNullOrEmpty(devicePath))
                return false;

            string baseDevice = GetBaseBlockDevice(devicePath);
            if (string.IsNullOrEmpty(baseDevice))
                return false;

            string sysfsPath = $"/sys/block/{baseDevice}/queue/rotational";
            if (File.Exists(sysfsPath))
            {
                string text = File.ReadAllText(sysfsPath).Trim();
                return text == "1";
            }
        }
        catch
        {
            // Fallback
        }
        return false;
    }

    private static string GetBaseBlockDevice(string devicePath)
    {
        if (!devicePath.StartsWith("/dev/", StringComparison.Ordinal))
            return string.Empty;

        string name = devicePath.Substring(5); // strip "/dev/"

        if (name.StartsWith("mapper/", StringComparison.Ordinal) || name.StartsWith("dm-", StringComparison.Ordinal))
        {
            return ResolveMapperDevice(devicePath, name);
        }

        if (name.StartsWith("nvme", StringComparison.Ordinal) || name.StartsWith("mmcblk", StringComparison.Ordinal))
        {
            return ResolvePartitionedStorageDevice(name);
        }

        return ResolveStandardDevice(name);
    }

    private static string ResolveMapperDevice(string devicePath, string name)
    {
        if (name.StartsWith("mapper/", StringComparison.Ordinal))
        {
            try
            {
                var resolvedInfo = File.ResolveLinkTarget(devicePath, true);
                string resolved = resolvedInfo != null ? resolvedInfo.FullName : devicePath;
                if (resolved.StartsWith("/dev/", StringComparison.Ordinal))
                {
                    return resolved.Substring(5);
                }
            }
            catch
            {
                // Ignore symlink resolution failure
            }
        }
        return name; // e.g. "dm-0"
    }

    private static string ResolvePartitionedStorageDevice(string name)
    {
        int pIndex = name.LastIndexOf('p');
        int minIndex = name.StartsWith("nvme", StringComparison.Ordinal) ? 4 : 6;
        if (pIndex > minIndex && char.IsDigit(name[pIndex + 1]))
        {
            return name.Substring(0, pIndex);
        }
        return name;
    }

    private static string ResolveStandardDevice(string name)
    {
        int lastCharIndex = name.Length - 1;
        while (lastCharIndex >= 0 && char.IsDigit(name[lastCharIndex]))
        {
            lastCharIndex--;
        }
        if (lastCharIndex >= 0)
        {
            return name.Substring(0, lastCharIndex + 1);
        }
        return name;
    }
}
