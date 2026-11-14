using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Tests;

public class NativeDirectoryTests
{
    [UnixFact]
    public void Probe_SucceedsOnSupportedPlatforms()
    {
        bool expected = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        Assert.Equal(expected, NativeIo.IsSupported);
    }

    [UnixFact]
    public void Reader_ReportsKindsAndSizes()
    {
        using TempTree tree = new TempTree();
        tree.File("data.bin", 10);
        tree.File(".hidden", 5);
        tree.Dir("sub");
        File.CreateSymbolicLink(Path.Combine(tree.Root, "file-link"), "data.bin");
        File.CreateSymbolicLink(Path.Combine(tree.Root, "dir-link"), "sub");
        File.CreateSymbolicLink(Path.Combine(tree.Root, "dangling"), "missing");

        Dictionary<string, (NativeEntryKind Kind, long Size)> entries = ReadAll(tree.Root);

        Assert.Equal(6, entries.Count);
        Assert.Equal((NativeEntryKind.File, 10L), entries["data.bin"]);
        Assert.Equal((NativeEntryKind.File, 5L), entries[".hidden"]);
        Assert.Equal(NativeEntryKind.Directory, entries["sub"].Kind);
        Assert.Equal((NativeEntryKind.Symlink, (long)"data.bin".Length), entries["file-link"]);
        Assert.Equal((NativeEntryKind.Symlink, (long)"sub".Length), entries["dir-link"]);
        Assert.Equal((NativeEntryKind.Symlink, (long)"missing".Length), entries["dangling"]);
    }

    [UnixFact]
    public unsafe void Open_ReportsErrnoForMissingDirectory()
    {
        byte[] path = Encoding.UTF8.GetBytes("/nonexistent-hyperdu-test-path\0");
        NativeDirectory? directory;
        int error;
        fixed (byte* p = path)
        {
            directory = NativeDirectory.Open(p, out error);
        }

        Assert.Null(directory);
        Assert.Equal("Directory Not Found", NativeIo.DescribeError(error));
    }

    private static unsafe Dictionary<string, (NativeEntryKind Kind, long Size)> ReadAll(string dir)
    {
        Dictionary<string, (NativeEntryKind Kind, long Size)> result = new();
        byte* buffer = stackalloc byte[NativeIo.StatBufferSize];
        byte[] path = Encoding.UTF8.GetBytes(dir + "\0");

        NativeDirectory? directory;
        fixed (byte* p = path)
        {
            directory = NativeDirectory.Open(p, out _);
        }

        Assert.NotNull(directory);
        using (directory)
        {
            while (directory.MoveNext())
            {
                directory.TryStatCurrent(false, buffer, out NativeStat stat);
                result[Encoding.UTF8.GetString(directory.CurrentName)] = (directory.CurrentKind, stat.Size);
            }

            Assert.Equal(0, directory.Error);
        }

        return result;
    }
}
