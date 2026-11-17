using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Models;
using hyperdu.Core.Native;
using hyperdu.Core.Scanning;

namespace hyperdu.Tests;

public sealed partial class TuiPhase6Tests
{
    [UnixFact]
    public async Task HeadlessTui_MatchesHyperduDu_OnSparseFilesHardLinksAndBlocks()
    {
        using TempTree tree = new();
        WriteNonZeroFile(Path.Combine(tree.Root, "regular.bin"), 8_192);
        WriteNonZeroFile(Path.Combine(tree.Root, "sub", "inner.bin"), 4_096);

        string sparsePath = Path.Combine(tree.Root, "sparse.bin");
        using (FileStream fs = File.Create(sparsePath))
        {
            fs.SetLength(16L * 1024 * 1024);
            fs.Flush(flushToDisk: true);
        }

        string hardA = Path.Combine(tree.Root, "hard_a.bin");
        string hardB = Path.Combine(tree.Root, "hard_b.bin");
        WriteNonZeroFile(hardA, 65_536);
        Assert.Equal(0, Link(Encoding.UTF8.GetBytes(hardA + "\0"), Encoding.UTF8.GetBytes(hardB + "\0")));

        // Allocated blocks (default) vs -l (count hard links) vs --apparent-size
        long duDefault = RunDuTotalBytes(["-B1", "-s"], tree.Root);
        long tuiDefault = RunHeadlessTuiTotalBytes([], tree.Root);
        Assert.Equal(duDefault, tuiDefault);
        Assert.True(tuiDefault < 16L * 1024 * 1024, $"Expected sparse file not to allocate 16 MiB, got {tuiDefault}.");

        long duCountLinks = RunDuTotalBytes(["-B1", "-s", "-l"], tree.Root);
        long tuiCountLinks = RunHeadlessTuiTotalBytes(["-l"], tree.Root);
        Assert.Equal(duCountLinks, tuiCountLinks);
        Assert.True(tuiCountLinks > tuiDefault, "Expected -l to count the hard-linked file twice.");

        long duApparent = RunDuTotalBytes(["-B1", "--apparent-size", "-s"], tree.Root);
        long tuiApparent = RunHeadlessTuiTotalBytes(["--apparent-size"], tree.Root);
        Assert.Equal(duApparent, tuiApparent);
        Assert.Equal(16L * 1024 * 1024 + 65_536 + 8_192 + 4_096, tuiApparent);

        long duApparentCountLinks = RunDuTotalBytes(["-B1", "--apparent-size", "-s", "-l"], tree.Root);
        long tuiApparentCountLinks = RunHeadlessTuiTotalBytes(["--apparent-size", "-l"], tree.Root);
        Assert.Equal(duApparentCountLinks, tuiApparentCountLinks);
        Assert.Equal(16L * 1024 * 1024 + 65_536 * 2 + 8_192 + 4_096, tuiApparentCountLinks);

        // Verify DirectoryListing honors allocated vs apparent size on the sparse file.
        ParallelScanner scanner = new(new ScanOptions { WorkerCount = 2, ApparentSize = false });
        DirectoryNode rootNode = await scanner.ScanAsync(tree.Root);
        List<(string Name, long Size)> allocatedFiles = DirectoryListing.ListFiles(rootNode, useNativeIo: true, apparentSize: false);
        List<(string Name, long Size)> apparentFiles = DirectoryListing.ListFiles(rootNode, useNativeIo: true, apparentSize: true);

        long sparseAllocated = allocatedFiles.Single(f => f.Name == "sparse.bin").Size;
        long sparseApparent = apparentFiles.Single(f => f.Name == "sparse.bin").Size;
        Assert.True(sparseAllocated < 16L * 1024 * 1024);
        Assert.Equal(16L * 1024 * 1024, sparseApparent);
    }

    [UnixFact]
    public async Task ParallelScanner_FollowSymlinks_PreventsAncestorCycleAndDeduplicates()
    {
        using TempTree tree = new();
        string sub = tree.Dir("sub");
        WriteNonZeroFile(Path.Combine(sub, "payload.bin"), 16_384);
        File.CreateSymbolicLink(Path.Combine(sub, "loop_parent"), "..");
        File.CreateSymbolicLink(Path.Combine(sub, "loop_root"), tree.Root);
        File.CreateSymbolicLink(Path.Combine(tree.Root, "peer_sub"), "sub");
        File.CreateSymbolicLink(Path.Combine(sub, "payload_link"), "payload.bin");

        long duFollow = RunDuTotalBytes(["-B1", "-s", "-L"], tree.Root);
        long tuiFollow = RunHeadlessTuiTotalBytes(["-L"], tree.Root);
        Assert.Equal(duFollow, tuiFollow);

        long duFollowApparent = RunDuTotalBytes(["-B1", "--apparent-size", "-s", "-L"], tree.Root);
        long tuiFollowApparent = RunHeadlessTuiTotalBytes(["--apparent-size", "-L"], tree.Root);
        Assert.Equal(duFollowApparent, tuiFollowApparent);
        Assert.Equal(16_384, tuiFollowApparent);

        // Even with CountLinks enabled (-L -l), ancestor cycle protection must terminate without looping.
        ParallelScanner countLinksScanner = new(new ScanOptions
        {
            WorkerCount = 4,
            UseNativeIo = true,
            FollowSymlinks = true,
            CountLinks = true,
            ApparentSize = true
        });
        DirectoryNode countLinksRoot = await countLinksScanner.ScanAsync(tree.Root);
        Assert.Null(countLinksRoot.ErrorMessage);
        Assert.True(countLinksRoot.TotalSize >= 16_384);
    }

    [Fact]
    public async Task ParallelScanner_ManagedFallback_PreventsSymlinkCycle()
    {
        using TempTree tree = new();
        string sub = tree.Dir("sub");
        WriteNonZeroFile(Path.Combine(sub, "data.bin"), 256);

        try
        {
            File.CreateSymbolicLink(Path.Combine(sub, "loop"), tree.Root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Unprivileged Windows environments without Developer Mode cannot create symlinks.
            return;
        }

        ParallelScanner scanner = new(new ScanOptions
        {
            WorkerCount = 2,
            UseNativeIo = false,
            FollowSymlinks = true
        });
        DirectoryNode root = await scanner.ScanAsync(tree.Root);

        Assert.Null(root.ErrorMessage);
        Assert.Equal(256, root.TotalSize);
    }

    [UnixFact]
    public void HeadlessTui_OneFileSystem_SkipsForeignMountTarget()
    {
        using TempTree tree = new();
        string d1 = tree.Dir("d1");
        WriteNonZeroFile(Path.Combine(d1, "local.bin"), 16_384);

        Span<byte> statBuf = new byte[256];
        if (!NativeIo.TryStatPath(Encoding.UTF8.GetBytes(d1 + "\0"), true, statBuf, out NativeStat stTmp, out _))
            return;

        const string shmBase = "/dev/shm";
        if (!Directory.Exists(shmBase) ||
            !NativeIo.TryStatPath(Encoding.UTF8.GetBytes(shmBase + "\0"), true, statBuf, out NativeStat stShm, out _) ||
            stTmp.Device == stShm.Device)
        {
            return;
        }

        string foreignDir = Path.Combine(shmBase, "hyperdu-tui-xdev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreignDir);
        try
        {
            WriteNonZeroFile(Path.Combine(foreignDir, "remote.bin"), 65_536);
            File.CreateSymbolicLink(Path.Combine(d1, "cross"), foreignDir);

            long duWithCross = RunDuTotalBytes(["-B1", "--apparent-size", "-s", "-L"], d1);
            long tuiWithCross = RunHeadlessTuiTotalBytes(["--apparent-size", "-L"], d1);
            Assert.Equal(duWithCross, tuiWithCross);
            Assert.Equal(16_384 + 65_536, tuiWithCross);

            long duOneFs = RunDuTotalBytes(["-B1", "--apparent-size", "-s", "-x", "-L"], d1);
            long tuiOneFs = RunHeadlessTuiTotalBytes(["--apparent-size", "-x", "-L"], d1);
            Assert.Equal(duOneFs, tuiOneFs);
            Assert.Equal(16_384, tuiOneFs);
        }
        finally
        {
            try
            {
                Directory.Delete(foreignDir, true);
            }
            catch
            {
                // Best-effort cleanup of temp dir in /dev/shm.
            }
        }
    }

    [UnixFact]
    public async Task ParallelScanner_BeyondPathMax_MatchesDuAndWorksInHeadlessTui()
    {
        using TempTree tree = new();
        string segment = new('d', 120);
        string deepRoot = tree.Dir("deep");
        WriteNonZeroFile(Path.Combine(tree.Root, "tmpl.bin"), 8_192);

        const int ORdonly = 0;
        int atRemovedir = OperatingSystem.IsMacOS() ? 0x80 : 0x200;
        List<(int ParentFd, byte[] Name, bool IsDir)> createdReversed = [];

        int rootFd = Open(Encoding.UTF8.GetBytes(tree.Root + "\0"), ORdonly);
        int curFd = Open(Encoding.UTF8.GetBytes(deepRoot + "\0"), ORdonly);
        Assert.True(rootFd >= 0 && curFd >= 0);
        List<int> openFds = [rootFd, curFd];

        try
        {
            for (int i = 0; i < 42; i++)
            {
                byte[] segBytes = [.. Encoding.UTF8.GetBytes($"{segment}_{i:D2}"), 0];
                Assert.Equal(0, MkdirAt(curFd, segBytes, 0b111_101_101));
                int nextFd = OpenAt(curFd, segBytes, ORdonly);
                Assert.True(nextFd >= 0);
                createdReversed.Add((curFd, segBytes, true));
                openFds.Add(nextFd);
                curFd = nextFd;
            }

            byte[] tmplName = [.. Encoding.UTF8.GetBytes("tmpl.bin"), 0];
            byte[] leafDirName = [.. Encoding.UTF8.GetBytes("leaf_dir"), 0];
            Assert.Equal(0, MkdirAt(curFd, leafDirName, 0b111_101_101));
            int leafFd = OpenAt(curFd, leafDirName, ORdonly);
            Assert.True(leafFd >= 0);
            openFds.Add(leafFd);
            createdReversed.Add((curFd, leafDirName, true));

            byte[] leafFile = [.. Encoding.UTF8.GetBytes("leaf.bin"), 0];
            Assert.Equal(0, LinkAt(rootFd, tmplName, leafFd, leafFile, 0));
            Assert.Equal(0, UnlinkAt(rootFd, tmplName, 0));
            createdReversed.Add((leafFd, leafFile, false));

            long duDefault = RunDuTotalBytes(["-B1", "-s"], tree.Root);
            long tuiDefault = RunHeadlessTuiTotalBytes([], tree.Root);
            Assert.Equal(duDefault, tuiDefault);

            // Unlike POSIX stat()-based du -L (which hits ENAMETOOLONG past PATH_MAX), TUI -L uses fd-relative openat/fstatat.
            long tuiFollow = RunHeadlessTuiTotalBytes(["-L"], tree.Root);
            Assert.Equal(duDefault, tuiFollow);

            ParallelScanner directScanner = new(new ScanOptions
            {
                WorkerCount = 4,
                UseNativeIo = true,
                ApparentSize = false,
                CountLinks = false
            });
            DirectoryNode rootNode = await directScanner.ScanAsync(tree.Root);
            Assert.Equal(duDefault, rootNode.TotalSize);

            DirectoryNode cursor = rootNode.Subdirectories.Single(d => d.Name == "deep");
            int depth = 0;
            while (cursor.Subdirectories.Count > 0)
            {
                Assert.Null(cursor.ErrorMessage);
                cursor = cursor.Subdirectories.Single();
                depth++;
            }

            Assert.Equal(43, depth);
            Assert.Null(cursor.ErrorMessage);
            Assert.True(cursor.Path.Length > 4096, $"Expected path > 4096 bytes, got {cursor.Path.Length}.");
            Assert.Equal(1, cursor.SelfFileCount);
            List<(string Name, long Size)> leafFiles = DirectoryListing.ListFiles(cursor, useNativeIo: true, apparentSize: true);
            Assert.Equal(new List<(string Name, long Size)> { ("leaf.bin", 8_192L) }, leafFiles);

            // Verify rescanning and CLI headless root operands deeper than PATH_MAX both succeed cleanly via OpenPathSafe.
            DirectoryNode rescannedLeaf = await directScanner.ScanAsync(cursor);
            Assert.Null(rescannedLeaf.ErrorMessage);
            Assert.Equal(1, rescannedLeaf.SelfFileCount);
            Assert.Equal(8_192L, RunHeadlessTuiTotalBytes(["--apparent-size"], cursor.Path));
        }
        finally
        {
            for (int i = createdReversed.Count - 1; i >= 0; i--)
            {
                (int parentFd, byte[] name, bool isDir) = createdReversed[i];
                _ = UnlinkAt(parentFd, name, isDir ? atRemovedir : 0);
            }

            foreach (int fd in openFds)
                _ = Close(fd);
        }
    }

    [Fact]
    public void MountHeuristics_ClassifiesPlatformMountsAndFsTypes()
    {
        Assert.True(MountHeuristics.IsNetworkFsType("nfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("smbfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("afpfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("webdav"));
        Assert.True(MountHeuristics.IsNetworkFsType("cifs"));
        Assert.True(MountHeuristics.IsNetworkFsType("ftp"));
        Assert.False(MountHeuristics.IsNetworkFsType("apfs"));
        Assert.False(MountHeuristics.IsNetworkFsType("ext4"));
        Assert.False(MountHeuristics.IsNetworkFsType("tmpfs"));

        string tempPath = Path.GetTempPath();
        MountHeuristics.DetectMountCharacteristics(tempPath, out bool isNetwork, out _);
        Assert.False(isNetwork);
        Assert.False(MountHeuristics.IsNetworkMount(tempPath));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(MountHeuristics.IsNetworkMount(@"\\hyperdu-test-server\share\dir"));
            Assert.True(MountHeuristics.IsNetworkMount(@"\\?\UNC\hyperdu-test-server\share\dir"));
            Assert.False(MountHeuristics.IsNetworkMount(@"\\?\" + Path.GetFullPath(tempPath)));
        }
    }

    [MacFact("Verifies macOS statfs/IOKit mount and rotational detection.")]
    public void MacOsMountInfo_ReadsFsTypeAndDeviceWithoutError()
    {
        string tempPath = Path.GetTempPath();
        Assert.True(NativeIo.TryGetMacOsMountInfo(tempPath, out string fsType, out string mountFrom));
        Assert.False(string.IsNullOrWhiteSpace(fsType));
        Assert.False(string.IsNullOrWhiteSpace(mountFrom));
        _ = DiskDeviceHelper.IsRotationalDrive(tempPath);
    }

    private static void WriteNonZeroFile(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[length];
        data.AsSpan().Fill(0xA5);
        using FileStream fs = File.Create(path);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }

    private static long RunDuTotalBytes(IReadOnlyList<string> flags, string targetPath)
    {
        List<string> args = ["du", .. flags, targetPath];
        return RunCliAndParseTotalBytes(args, headlessTui: false);
    }

    private static long RunHeadlessTuiTotalBytes(IReadOnlyList<string> flags, string targetPath)
    {
        List<string> args = [.. flags, targetPath];
        return RunCliAndParseTotalBytes(args, headlessTui: true);
    }

    private static long RunCliAndParseTotalBytes(IReadOnlyList<string> cliArgs, bool headlessTui)
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "hyperdu.dll");
        Assert.True(File.Exists(cliDll), $"Expected CLI assembly at {cliDll}");

        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(cliDll);
        foreach (string arg in cliArgs)
            psi.ArgumentList.Add(arg);

        if (headlessTui)
            psi.Environment["HYPERDU_NO_UI"] = "1";
        else
            psi.Environment.Remove("HYPERDU_NO_UI");

        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(15000), "Timed out running CLI process.");
        Assert.True(proc.ExitCode == 0, $"CLI failed ({proc.ExitCode}):\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Equal(string.Empty, stderr);

        string line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        int tab = line.IndexOf('\t');
        Assert.True(tab > 0, $"Unexpected CLI output line: '{line}'");
        return long.Parse(line[..tab]);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(byte[] path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAt(int dirFd, byte[] name, int flags);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static partial int MkdirAt(int dirFd, byte[] name, uint mode);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true)]
    private static partial int Link(byte[] oldPath, byte[] newPath);

    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static partial int LinkAt(int oldDirFd, byte[] oldPath, int newDirFd, byte[] newPath, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAt(int dirFd, byte[] name, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
