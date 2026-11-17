using System.Runtime.InteropServices;

namespace hyperdu.Tests;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Requires Linux or macOS.";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows.";
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute(string reason)
    {
        if (!OperatingSystem.IsLinux())
            Skip = reason;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute(string reason)
    {
        if (!OperatingSystem.IsMacOS())
            Skip = reason;
    }
}

internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "hyperdu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, int size)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // Best-effort cleanup of the temp tree.
        }
    }
}

internal static partial class TestLibc
{
    [LibraryImport("libc", SetLastError = true)]
    private static partial int rename(byte[] from, byte[] to);

    public static void Rename(byte[] from, byte[] to)
    {
        if (rename(NullTerminated(from), NullTerminated(to)) != 0)
            throw new IOException($"rename failed with errno {Marshal.GetLastPInvokeError()}");
    }

    private const uint UF_HIDDEN = 0x8000;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int chflags(byte[] path, uint flags);

    public static void SetHiddenFlag(string path)
    {
        if (chflags(NullTerminated(System.Text.Encoding.UTF8.GetBytes(path)), UF_HIDDEN) != 0)
            throw new IOException($"chflags failed with errno {Marshal.GetLastPInvokeError()}");
    }

    private static byte[] NullTerminated(byte[] bytes) => [.. bytes, 0];
}
