using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace hyperdu.Core.Scanning;

public static partial class DiskDeviceHelper
{
    // Windows API Constants
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

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
        return false; // Default fallback to non-rotational (safe for multi-threading)
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
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists("/proc/mounts")) return false;

            string[] lines = File.ReadAllLines("/proc/mounts");
            string bestMatchMount = "";
            string bestMatchDevice = "";

            foreach (string line in lines)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                string device = parts[0];
                string mountPoint = parts[1];

                if (fullPath.StartsWith(mountPoint, StringComparison.Ordinal) && mountPoint.Length > bestMatchMount.Length)
                {
                    bestMatchMount = mountPoint;
                    bestMatchDevice = device;
                }
            }

            if (string.IsNullOrEmpty(bestMatchDevice))
                return false;

            string baseDevice = GetBaseBlockDevice(bestMatchDevice);
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
