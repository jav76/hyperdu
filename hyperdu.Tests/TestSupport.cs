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

internal static unsafe partial class TestLibc
{
    [LibraryImport("libc", SetLastError = true)]
    private static partial int rename(byte* from, byte* to);

    public static void Rename(byte[] from, byte[] to)
    {
        fixed (byte* f = NullTerminated(from))
        fixed (byte* t = NullTerminated(to))
        {
            if (rename(f, t) != 0)
                throw new IOException($"rename failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }

    private static byte[] NullTerminated(byte[] bytes) => [.. bytes, 0];
}
