using System.Runtime.Versioning;
using System.Text;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Tests;

[UnsupportedOSPlatform("windows")]
public class NativeScanTests
{
    private static readonly byte[] InvalidUtf8Name = [(byte)'d', (byte)'i', (byte)'r', 0xFE];

    [UnixFact]
    public async Task NativeAndManagedScans_ProduceTheSameTree()
    {
        using TempTree tree = BuildTree();

        (ParallelScanner native, DirectoryNode nativeRoot) = await ScanAsync(tree.Root, useNativeIo: true);
        (ParallelScanner managed, DirectoryNode managedRoot) = await ScanAsync(tree.Root, useNativeIo: false);

        Assert.True(native.UsesNativeIo);
        Assert.False(managed.UsesNativeIo);
        Assert.Equal(Flatten(managedRoot), Flatten(nativeRoot));
        Assert.Equal(managed.FilesScanned, native.FilesScanned);
        Assert.Equal(managed.DirectoriesScanned, native.DirectoriesScanned);
        Assert.Equal(1_000 + 2_000 + 3_000 + 40 + 7, nativeRoot.TotalSize);
    }

    [UnixFact]
    public async Task SkipHidden_MatchesManagedScan()
    {
        using TempTree tree = BuildTree();

        (_, DirectoryNode nativeRoot) = await ScanAsync(tree.Root, useNativeIo: true, skipHidden: true);
        (_, DirectoryNode managedRoot) = await ScanAsync(tree.Root, useNativeIo: false, skipHidden: true);

        Assert.Equal(Flatten(managedRoot), Flatten(nativeRoot));
        Assert.Equal(1_000 + 2_000 + 3_000, nativeRoot.TotalSize);
    }

    [UnixFact]
    public async Task DirectorySymlink_IsListedButNotTraversed()
    {
        using TempTree tree = new TempTree();
        tree.File("real/payload", 500);
        File.CreateSymbolicLink(Path.Combine(tree.Root, "link"), "real");

        (_, DirectoryNode root) = await ScanAsync(tree.Root, useNativeIo: true);

        DirectoryNode link = root.Subdirectories.Single(d => d.Name == "link");
        Assert.True(link.IsScanned);
        Assert.Empty(link.Subdirectories);
        Assert.Equal("real".Length, link.TotalSize);
        Assert.Equal(500 + "real".Length, root.TotalSize);
    }

    [UnixFact]
    public async Task NonUtf8DirectoryName_IsTraversed()
    {
        using TempTree tree = new TempTree();
        byte[] invalidPath = CreateInvalidUtf8Directory(tree);

        try
        {
            (_, DirectoryNode root) = await ScanAsync(tree.Root, useNativeIo: true);

            DirectoryNode invalid = root.Subdirectories.Single();
            Assert.Null(invalid.ErrorMessage);
            Assert.Equal(100, invalid.TotalSize);
            Assert.Equal(1, invalid.FileCount);
            Assert.Equal("inner", invalid.Subdirectories.Single().Name);
            Assert.Equal(invalidPath, invalid.GetNativePath(nullTerminated: false));
        }
        finally
        {
            TestLibc.Rename(invalidPath, Encoding.UTF8.GetBytes(Path.Combine(tree.Root, "plain")));
        }
    }

    [UnixFact]
    public async Task RescanOfNonUtf8Node_UsesTheExactPath()
    {
        using TempTree tree = new TempTree();
        byte[] invalidPath = CreateInvalidUtf8Directory(tree);

        try
        {
            (_, DirectoryNode root) = await ScanAsync(tree.Root, useNativeIo: true);
            DirectoryNode invalid = root.Subdirectories.Single();

            ParallelScanner rescanner = new ParallelScanner(new ScanOptions { WorkerCount = 2 });
            DirectoryNode rescanned = await rescanner.ScanAsync(invalid);

            Assert.Null(rescanned.ErrorMessage);
            Assert.Equal(100, rescanned.TotalSize);
            Assert.Null(rescanned.Subdirectories.Single().ErrorMessage);
        }
        finally
        {
            TestLibc.Rename(invalidPath, Encoding.UTF8.GetBytes(Path.Combine(tree.Root, "plain")));
        }
    }

    [UnixFact]
    public async Task FileListing_WorksInNonUtf8Directory()
    {
        using TempTree tree = new TempTree();
        byte[] invalidPath = CreateInvalidUtf8Directory(tree);

        try
        {
            (_, DirectoryNode root) = await ScanAsync(tree.Root, useNativeIo: true);

            List<(string Name, long Size)> files = DirectoryListing.ListFiles(root.Subdirectories.Single());

            Assert.Equal(new List<(string Name, long Size)> { ("payload.txt", 100L) }, files);
        }
        finally
        {
            TestLibc.Rename(invalidPath, Encoding.UTF8.GetBytes(Path.Combine(tree.Root, "plain")));
        }
    }

    [UnixFact]
    public async Task UnreadableDirectory_IsReportedAsAccessDenied()
    {
        using TempTree tree = new TempTree();
        string locked = tree.Dir("locked");
        tree.File("locked/secret", 10);
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            if (Directory.EnumerateFileSystemEntries(locked).Any())
                return; // Running as root: permissions are not enforced.
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            (_, DirectoryNode root) = await ScanAsync(tree.Root, useNativeIo: true);

            Assert.Equal("Access Denied", root.Subdirectories.Single().ErrorMessage);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static TempTree BuildTree()
    {
        TempTree tree = new TempTree();
        tree.File("a.bin", 1_000);
        tree.File("docs/b.bin", 2_000);
        tree.File("docs/deep/nested/c.bin", 3_000);
        tree.File(".config/settings", 40);
        tree.File("docs/.notes", 7);
        tree.Dir("empty");
        return tree;
    }

    private static byte[] CreateInvalidUtf8Directory(TempTree tree)
    {
        tree.File("plain/payload.txt", 100);
        tree.Dir("plain/inner");
        byte[] invalidPath = [.. Encoding.UTF8.GetBytes(tree.Root + "/"), .. InvalidUtf8Name];
        TestLibc.Rename(Encoding.UTF8.GetBytes(Path.Combine(tree.Root, "plain")), invalidPath);
        return invalidPath;
    }

    private static async Task<(ParallelScanner Scanner, DirectoryNode Root)> ScanAsync(string path, bool useNativeIo,
        bool skipHidden = false)
    {
        ParallelScanner scanner = new ParallelScanner(new ScanOptions
        {
            WorkerCount = 4,
            UseNativeIo = useNativeIo,
            SkipHidden = skipHidden
        });
        DirectoryNode root = await scanner.ScanAsync(path);
        return (scanner, root);
    }

    private static SortedDictionary<string, (long Total, int Files, int Subdirs, string? Error)> Flatten(DirectoryNode root)
    {
        SortedDictionary<string, (long, int, int, string?)> result = new(StringComparer.Ordinal);
        Stack<DirectoryNode> pending = new Stack<DirectoryNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DirectoryNode node = pending.Pop();
            result[Path.GetRelativePath(root.Path, node.Path)] =
                (node.TotalSize, node.FileCount, node.SubdirectoryCount, node.ErrorMessage);
            foreach (DirectoryNode child in node.Subdirectories) pending.Push(child);
        }

        return result;
    }
}
