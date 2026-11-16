using System;
using System.IO;
using hyperdu.Core.Native;

namespace hyperdu.Core.Scanning;

/// <summary>
/// Resolves mount characteristics (network vs rotational HDD vs solid-state) and worker thread
/// budgets using a single pass over /proc/mounts on Linux.
/// </summary>
public static class MountHeuristics
{
    public static int? ParseWorkerCount(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        return int.TryParse(rawValue.Trim(), out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    public static bool IsNetworkMount(string path)
    {
        DetectMountCharacteristicsCore(path, checkRotational: false, out bool isNetwork, out _);
        return isNetwork;
    }

    public static int ResolveWorkerBudget(
        int? requestedThreads,
        string targetPath,
        out bool isNetwork,
        out bool isRotational)
    {
        if (requestedThreads.HasValue)
        {
            isNetwork = false;
            isRotational = false;
            return Math.Max(1, requestedThreads.Value);
        }

        DetectMountCharacteristics(targetPath, out isNetwork, out isRotational);

        if (isNetwork)
            return Math.Max(32, Environment.ProcessorCount * 2);

        if (isRotational)
            return 1;

        return Math.Max(1, Environment.ProcessorCount);
    }

    public static void DetectMountCharacteristics(string path, out bool isNetwork, out bool isRotational) =>
        DetectMountCharacteristicsCore(path, checkRotational: true, out isNetwork, out isRotational);

    private static void DetectMountCharacteristicsCore(
        string path,
        bool checkRotational,
        out bool isNetwork,
        out bool isRotational)
    {
        if (OperatingSystem.IsLinux())
        {
            if (TryGetLinuxMountInfo(path, out string device, out string fsType))
            {
                isNetwork = IsNetworkFsType(fsType);
                isRotational = checkRotational && !isNetwork && DiskDeviceHelper.IsRotationalBlockDevice(device);
                return;
            }

            isNetwork = false;
            isRotational = false;
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (NativeIo.TryGetMacOsMountInfo(path, out string fsType, out string mountFromName))
            {
                isNetwork = IsNetworkFsType(fsType);
                isRotational = checkRotational && !isNetwork && DiskDeviceHelper.IsRotationalMacOsMount(mountFromName);
                return;
            }

            isNetwork = false;
            isRotational = false;
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            isNetwork = DiskDeviceHelper.IsNetworkDriveWindows(path);
            isRotational = checkRotational && !isNetwork && DiskDeviceHelper.IsRotationalDrive(path);
            return;
        }

        isNetwork = false;
        isRotational = checkRotational && DiskDeviceHelper.IsRotationalDrive(path);
    }

    internal static bool IsNetworkFsType(string? fsType)
    {
        if (string.IsNullOrEmpty(fsType))
            return false;

        string fsLower = fsType.ToLowerInvariant();
        return fsLower.Contains("nfs", StringComparison.Ordinal) ||
               fsLower.Contains("cifs", StringComparison.Ordinal) ||
               fsLower.Contains("smb", StringComparison.Ordinal) ||
               fsLower.Contains("afp", StringComparison.Ordinal) ||
               fsLower.Contains("webdav", StringComparison.Ordinal) ||
               fsLower.Contains("sshfs", StringComparison.Ordinal) ||
               fsLower.Contains("davfs", StringComparison.Ordinal) ||
               fsLower.Contains("glusterfs", StringComparison.Ordinal) ||
               fsLower.Contains("ceph", StringComparison.Ordinal) ||
               fsLower == "ftp";
    }

    internal static bool TryGetLinuxMountInfo(string path, out string device, out string fsType)
    {
        device = string.Empty;
        fsType = string.Empty;

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists("/proc/mounts"))
                return false;

            string[] lines = File.ReadAllLines("/proc/mounts");
            string bestMount = string.Empty;

            foreach (string line in lines)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                string mountPoint = parts[1];
                if (!MatchesMountPrefix(fullPath, mountPoint))
                    continue;

                if (mountPoint.Length > bestMount.Length)
                {
                    bestMount = mountPoint;
                    device = parts[0];
                    fsType = parts[2];
                }
            }

            return bestMount.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesMountPrefix(string fullPath, string mountPoint)
    {
        if (!fullPath.StartsWith(mountPoint, StringComparison.Ordinal))
            return false;

        return mountPoint == "/" ||
               fullPath.Length == mountPoint.Length ||
               fullPath[mountPoint.Length] == '/';
    }
}
