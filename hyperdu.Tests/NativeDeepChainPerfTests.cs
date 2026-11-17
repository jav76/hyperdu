using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Tests;

public sealed partial class NativeDeepChainPerfTests
{
    private const int PosixORdonly = 0;
    private static int AtRemovedir => OperatingSystem.IsMacOS() ? 0x80 : 0x200;

    [UnixFact]
    public async Task DeepChainBeyondPathMax_265Levels_MatchesDuInDirectScanAndCliNoUi()
    {
        using DeepChainFixture fixture = DeepChainFixture.Create(
            depth: 265,
            segmentPrefix: "chain_lvl",
            fileEveryNLevels: 25,
            fileSize: 4_096,
            includeLeafFile: true);

        Assert.True(fixture.LeafPathLength > 4096, $"Expected leaf path > 4096, got {fixture.LeafPathLength}.");

        long duApparent = RunCliAndParseTotal(["gdu", "--apparent-size", "-B1", "-s", fixture.Root], headlessTui: false);
        long duAlloc = RunCliAndParseTotal(["gdu", "-B1", "-s", fixture.Root], headlessTui: false);

        long cliNoUiApparent = RunCliAndParseTotal(["--no-ui", "--apparent-size", fixture.Root], headlessTui: false);
        long cliEnvApparent = RunCliAndParseTotal(["--apparent-size", fixture.Root], headlessTui: true);
        long cliNoUiAlloc = RunCliAndParseTotal(["--no-ui", fixture.Root], headlessTui: false);

        Assert.Equal(duApparent, cliNoUiApparent);
        Assert.Equal(duApparent, cliEnvApparent);
        Assert.Equal(duAlloc, cliNoUiAlloc);
        Assert.Equal(fixture.ExpectedApparentBytes, cliNoUiApparent);

        foreach (int workers in new[] { 1, 4 })
        {
            ParallelScanner scanner = new(new ScanOptions
            {
                WorkerCount = workers,
                UseNativeIo = true,
                ApparentSize = true
            });
            DirectoryNode root = await scanner.ScanAsync(fixture.Root);

            Assert.Null(root.ErrorMessage);
            Assert.Equal(fixture.ExpectedApparentBytes, root.TotalSize);
            Assert.Equal(fixture.ExpectedFileCount, root.FileCount);
            Assert.Equal(fixture.ExpectedFileCount, scanner.FilesScanned);
            Assert.Equal(265, root.SubdirectoryCount);
            Assert.Equal(266, scanner.DirectoriesScanned);

            DirectoryNode cursor = root;
            for (int level = 1; level <= 265; level++)
            {
                Assert.Null(cursor.ErrorMessage);
                cursor = Assert.Single(cursor.Subdirectories);
            }

            Assert.Empty(cursor.Subdirectories);
            Assert.True(cursor.Path.Length > 4096, $"Expected lazy materialized Path > 4096, got {cursor.Path.Length}.");
            Assert.Equal(cursor.Path, Encoding.UTF8.GetString(cursor.GetNativePath(nullTerminated: false)));
            Assert.Equal(1, cursor.SelfFileCount);

            List<(string Name, long Size)> leafFiles = DirectoryListing.ListFiles(cursor, useNativeIo: true, apparentSize: true);
            Assert.Equal(new List<(string Name, long Size)> { ("leaf_bottom.bin", 4_096L) }, leafFiles);

            root.Compact();
            Assert.Equal(fixture.ExpectedApparentBytes, ParallelScanner.ComputeTotalSizes(root));

            DirectoryNode rescannedLeaf = await scanner.ScanAsync(cursor);
            Assert.Null(rescannedLeaf.ErrorMessage);
            Assert.Equal(4_096L, rescannedLeaf.TotalSize);
            Assert.Equal(1, rescannedLeaf.SelfFileCount);
        }
    }

    [UnixFact]
    public async Task DeepChain500Levels_FollowSymlinksAndCountLinks_DetectsCyclesAndAvoidsStackOverflow()
    {
        using DeepChainFixture fixture = DeepChainFixture.Create(
            depth: 500,
            segmentPrefix: "deep_s",
            fileEveryNLevels: 100,
            fileSize: 2_048,
            includeLeafFile: true);

        Assert.True(fixture.LeafPathLength > 8000, $"Expected leaf path > 8000, got {fixture.LeafPathLength}.");

        // Add an ancestor cycle symlink at depth 495 pointing back to depth 200
        // (relative target length stays < 1024 macOS PATH_MAX so symlinkat succeeds on macOS),
        // a dangling symlink at depth 498, and a peer branch symlink at depth 10.
        int dir495 = fixture.OpenLevelFd(495);
        int dir498 = fixture.OpenLevelFd(498);
        int dir10 = fixture.OpenLevelFd(10);
        try
        {
            string relUpTo200 = string.Join("/", Enumerable.Repeat("..", 495 - 200));
            Assert.Equal(0, SymlinkAt(NullTerm(relUpTo200), dir495, NullTerm("cycle_to_200")));
            Assert.Equal(0, SymlinkAt(NullTerm("missing_target_abc"), dir498, NullTerm("dangling_sym")));
            Assert.Equal(0, SymlinkAt(NullTerm("deep_s_011_pad012345"), dir10, NullTerm("peer_to_11")));
        }
        finally
        {
            _ = Close(dir495);
            _ = Close(dir498);
            _ = Close(dir10);
        }

        // -L (CountLinks = false): deduplicates peer_to_11 and breaks cycle_to_50; counts dangling_sym length.
        long expectedFollowDedup = fixture.ExpectedApparentBytes + "missing_target_abc".Length;
        foreach (int workers in new[] { 1, 4 })
        {
            ParallelScanner followDedup = new(new ScanOptions
            {
                WorkerCount = workers,
                UseNativeIo = true,
                FollowSymlinks = true,
                CountLinks = false,
                ApparentSize = true
            });
            DirectoryNode dedupRoot = await followDedup.ScanAsync(fixture.Root);
            Assert.Null(dedupRoot.ErrorMessage);
            Assert.Equal(expectedFollowDedup, dedupRoot.TotalSize);
            dedupRoot.Compact();
            Assert.Equal(expectedFollowDedup, ParallelScanner.ComputeTotalSizes(dedupRoot));
        }

        long cliFollow = RunCliAndParseTotal(["--no-ui", "-L", "-b", fixture.Root], headlessTui: false);
        Assert.Equal(expectedFollowDedup, cliFollow);

        // -L -l (CountLinks = true): exercises O(1) inline ancestor cycle protection and multi-worker cycle guard.
        // Note: peer_to_11 is NOT an ancestor of depth 10, so -L -l traverses the subtree twice while breaking cycle_to_50.
        foreach (int workers in new[] { 1, 4 })
        {
            ParallelScanner followCountLinks = new(new ScanOptions
            {
                WorkerCount = workers,
                UseNativeIo = true,
                FollowSymlinks = true,
                CountLinks = true,
                ApparentSize = true
            });
            DirectoryNode countedRoot = await followCountLinks.ScanAsync(fixture.Root);
            Assert.Null(countedRoot.ErrorMessage);
            Assert.True(countedRoot.TotalSize > expectedFollowDedup,
                $"Expected -L -l to traverse non-ancestor peer symlink twice, got {countedRoot.TotalSize} <= {expectedFollowDedup}.");
        }
    }

    [UnixFact]
    public async Task DeepChain_ExcludesAndHardlinks_WorkPastPathMax()
    {
        using DeepChainFixture fixture = DeepChainFixture.Create(
            depth: 265,
            segmentPrefix: "hl_lvl",
            fileEveryNLevels: 0,
            fileSize: 0,
            includeLeafFile: false);

        int fd10 = fixture.OpenLevelFd(10);
        int fd260 = fixture.OpenLevelFd(260);
        int fd265 = fixture.OpenLevelFd(265);
        try
        {
            WriteFileAt(fixture.Root, fd10, "primary.bin", 16_384);
            Assert.Equal(0, LinkAt(fd10, NullTerm("primary.bin"), fd265, NullTerm("hardlink.bin"), 0));
            WriteFileAt(fixture.Root, fd260, "ignored.tmp", 32_768);
            WriteFileAt(fixture.Root, fd265, "keep.dat", 4_096);

            Assert.Equal(0, MkdirAt(fd260, NullTerm("skip_me"), 0b111_101_101));
            int skipFd = OpenAt(fd260, NullTerm("skip_me"), PosixORdonly);
            Assert.True(skipFd >= 0);
            try
            {
                WriteFileAt(fixture.Root, skipFd, "huge.bin", 65_536);
            }
            finally
            {
                _ = Close(skipFd);
            }

            // Deduped hardlinks + excludes -> 16,384 + 4,096 = 20,480 bytes.
            ScanOptions dedupExcludeOpts = new()
            {
                WorkerCount = 4,
                UseNativeIo = true,
                ApparentSize = true,
                CountLinks = false
            };
            dedupExcludeOpts.ExcludeMatcher.AddPattern("*.tmp");
            dedupExcludeOpts.ExcludeMatcher.AddPattern("skip_me");

            ParallelScanner scanner = new(dedupExcludeOpts);
            DirectoryNode root = await scanner.ScanAsync(fixture.Root);
            Assert.Equal(20_480L, root.TotalSize);
            Assert.Equal(2, root.FileCount);

            long cliDedupExcl = RunCliAndParseTotal(
                ["--no-ui", "-b", "--exclude=*.tmp", "--exclude=skip_me", fixture.Root],
                headlessTui: false);
            Assert.Equal(20_480L, cliDedupExcl);

            // CountLinks (-l) + excludes -> 16,384 * 2 + 4,096 = 36,864 bytes.
            long cliCountLinksExcl = RunCliAndParseTotal(
                ["--no-ui", "-b", "-l", "--exclude=*.tmp", "--exclude=skip_me", fixture.Root],
                headlessTui: false);
            Assert.Equal(36_864L, cliCountLinksExcl);

            // Excluding the root operand leaves LastScanExcludedRoot = true and empty CLI output.
            string rootName = Path.GetFileName(fixture.Root);
            ScanOptions rootExclOpts = new() { UseNativeIo = true };
            rootExclOpts.ExcludeMatcher.AddPattern(rootName);
            ParallelScanner rootExclScanner = new(rootExclOpts);
            DirectoryNode excludedRoot = await rootExclScanner.ScanAsync(fixture.Root);
            Assert.True(rootExclScanner.LastScanExcludedRoot);
            Assert.Equal(0L, excludedRoot.TotalSize);

            (int rcExcl, string outExcl, string errExcl) = RunCli(["--no-ui", $"--exclude={rootName}", fixture.Root], headlessTui: false);
            Assert.Equal(0, rcExcl);
            Assert.Empty(outExcl);
            Assert.Empty(errExcl);
        }
        finally
        {
            _ = Close(fd10);
            _ = Close(fd260);
            _ = Close(fd265);
        }
    }

    [UnixFact]
    public async Task InlineToMultiWorkerFanOut_AndWideDirBeyondMaxPreopenedFds_FallbackAndCountAccurately()
    {
        using DeepChainFixture fixture = DeepChainFixture.Create(
            depth: 262,
            segmentPrefix: "fan_lvl",
            fileEveryNLevels: 0,
            fileSize: 0,
            includeLeafFile: false);

        const int wideSiblingCount = 160; // Exceeds MaxPreopenedFds (128 on Linux, 48 on macOS)
        const int fileSize = 1_024;

        int fd10 = fixture.OpenLevelFd(10);
        int fd262 = fixture.OpenLevelFd(262);
        try
        {
            // Place one file in inline ancestor depth 10 and a hardlink to it inside two wide branches.
            WriteFileAt(fixture.Root, fd10, "shared.bin", 8_192);

            for (int i = 0; i < wideSiblingCount; i++)
            {
                string subName = $"wide_{i:D3}";
                Assert.Equal(0, MkdirAt(fd262, NullTerm(subName), 0b111_101_101));
                int subFd = OpenAt(fd262, NullTerm(subName), PosixORdonly);
                Assert.True(subFd >= 0);
                try
                {
                    WriteFileAt(fixture.Root, subFd, "item.bin", fileSize);
                    if (i is 0 or 150)
                        Assert.Equal(0, LinkAt(fd10, NullTerm("shared.bin"), subFd, NullTerm("shared_link.bin"), 0));
                }
                finally
                {
                    _ = Close(subFd);
                }
            }

            long expectedTotal = 8_192L + (long)wideSiblingCount * fileSize;
            ParallelScanner scanner = new(new ScanOptions
            {
                WorkerCount = 4,
                UseNativeIo = true,
                ApparentSize = true,
                CountLinks = false,
                AutoDetectWorkerCount = true
            });

            DirectoryNode root = await scanner.ScanAsync(fixture.Root);
            Assert.Null(root.ErrorMessage);
            Assert.Equal(expectedTotal, root.TotalSize);
            Assert.Equal(1 + wideSiblingCount, root.FileCount);
            Assert.Equal(262 + wideSiblingCount, root.SubdirectoryCount);

            // Navigate to depth 262 and confirm all 160 subdirectories are present and scanned cleanly.
            DirectoryNode cursor = root;
            for (int level = 1; level <= 262; level++)
                cursor = Assert.Single(cursor.Subdirectories);

            Assert.Equal(wideSiblingCount, cursor.Subdirectories.Count);
            Assert.All(cursor.Subdirectories, sub =>
            {
                Assert.True(sub.IsScanned);
                Assert.Null(sub.ErrorMessage);
                Assert.True(sub.Path.Length > 4096);
            });
        }
        finally
        {
            _ = Close(fd10);
            _ = Close(fd262);
        }
    }

    [UnixFact]
    public async Task ProgressReporting_PauseResume_Cancellation_AndFdCleanup_LeakZeroFileDescriptors()
    {
        using DeepChainFixture fixture = DeepChainFixture.Create(
            depth: 300,
            segmentPrefix: "fd_lvl",
            fileEveryNLevels: 30,
            fileSize: 512,
            includeLeafFile: true);

        // Add an unreadable directory at depth 290 (when not running as root) to test error path FD cleanup.
        int fd290 = fixture.OpenLevelFd(290);
        bool lockedApplied = false;
        try
        {
            Assert.Equal(0, MkdirAt(fd290, NullTerm("locked_dir"), 0b000_000_000));
            int probeFd = OpenAt(fd290, NullTerm("locked_dir"), PosixORdonly);
            if (probeFd >= 0)
                _ = Close(probeFd); // Running as root; chmod 000 is bypassed.
            else
                lockedApplied = true;
        }
        finally
        {
            _ = Close(fd290);
        }

        // Also verify pre-canceled token throws OperationCanceledException without leaking FDs.
        using (CancellationTokenSource preCanceled = new())
        {
            preCanceled.Cancel();
            ParallelScanner preCancelScanner = new(new ScanOptions { WorkerCount = 4, UseNativeIo = true });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => preCancelScanner.ScanAsync(fixture.Root, null, preCanceled.Token));
        }

        // Progress + Pause/Resume test on a fresh scanner.
        List<ScanProgress> reports = [];
        IProgress<ScanProgress> progress = new SyncProgress<ScanProgress>(p => reports.Add(p));
        ParallelScanner progressScanner = new(new ScanOptions { WorkerCount = 2, UseNativeIo = true, ApparentSize = true });
        DirectoryNode progRoot = await progressScanner.ScanAsync(fixture.Root, progress);
        Assert.NotEmpty(reports);
        Assert.Equal(0, reports[^1].ActiveWorkers);
        Assert.Equal(fixture.ExpectedApparentBytes, progRoot.TotalSize);

        // Warm up thread pool / probe once before measuring baseline open FDs.
        int baselineFds = CountOpenFileDescriptors();

        for (int i = 0; i < 15; i++)
        {
            ParallelScanner scanner = new(new ScanOptions
            {
                WorkerCount = (i % 2 == 0) ? 1 : 4,
                UseNativeIo = true,
                ApparentSize = true
            });
            DirectoryNode root = await scanner.ScanAsync(fixture.Root);
            Assert.Equal(fixture.ExpectedApparentBytes, root.TotalSize);
            if (lockedApplied)
            {
                DirectoryNode node290 = root;
                for (int level = 1; level <= 290; level++)
                    node290 = node290.Subdirectories.Single(d => d.Name.StartsWith("fd_lvl_", StringComparison.Ordinal));
                DirectoryNode lockedNode = node290.Subdirectories.Single(d => d.Name == "locked_dir");
                Assert.Equal("Access Denied", lockedNode.ErrorMessage);
            }
        }

        // Mid-scan cancellation loop (canceling during scan must dispose all pre-opened and active FDs).
        for (int i = 0; i < 10; i++)
        {
            using CancellationTokenSource cts = new();
            ParallelScanner cancelScanner = new(new ScanOptions { WorkerCount = 4, UseNativeIo = true });
            IProgress<ScanProgress> cancelOnProgress = new SyncProgress<ScanProgress>(_ => cts.Cancel());
            cts.CancelAfter(TimeSpan.FromMilliseconds(1));
            DirectoryNode partial = await cancelScanner.ScanAsync(fixture.Root, cancelOnProgress, cts.Token);
            Assert.NotNull(partial);
            Assert.False(cancelScanner.IsScanning);
        }

        // Bounded cancellation check on a paused or active scan without racing fast completion.
        using (CancellationTokenSource activeCts = new())
        {
            ParallelScanner activeCancelScanner = new(new ScanOptions { WorkerCount = 2, UseNativeIo = true });
            Task<DirectoryNode> activeScanTask = activeCancelScanner.ScanAsync(fixture.Root, null, activeCts.Token);
            if (activeCancelScanner.IsScanning)
                activeCancelScanner.Pause();
            activeCts.Cancel();
            DirectoryNode canceledNode = await activeScanTask;
            Assert.NotNull(canceledNode);
            Assert.False(activeCancelScanner.IsScanning);
        }

        // Pause/Resume smoke check.
        ParallelScanner pauseScanner = new(new ScanOptions { WorkerCount = 2, UseNativeIo = true, ApparentSize = true });
        Task<DirectoryNode> scanTask = pauseScanner.ScanAsync(fixture.Root);
        if (pauseScanner.IsScanning)
        {
            pauseScanner.Pause();
            await Task.Delay(10);
            pauseScanner.Resume();
        }
        DirectoryNode pausedResult = await scanTask;
        Assert.Equal(fixture.ExpectedApparentBytes, pausedResult.TotalSize);

        int afterFds = CountOpenFileDescriptors();
        Assert.True(
            afterFds <= baselineFds + 2,
            $"Open file descriptors grew from {baselineFds} to {afterFds} after 25+ deep scans/cancellations.");
    }

    [Fact]
    public void HeadlessCli_TinyStartupAndMissingPath_BehavesCorrectly()
    {
        using TempTree tiny = new();
        for (int i = 0; i < 5; i++)
            tiny.File($"f_{i}.bin", 128 * (i + 1));

        long expectedApparent = 128 + 256 + 384 + 512 + 640;
        Assert.Equal(expectedApparent, RunCliAndParseTotal(["--no-ui", "--apparent-size", tiny.Root], headlessTui: false));
        Assert.Equal(expectedApparent, RunCliAndParseTotal(["-b", "-j", "1", tiny.Root], headlessTui: true));
        Assert.Equal(expectedApparent, RunCliAndParseTotal(["--no-ui", "-b", "-j", "4", tiny.Root], headlessTui: false));

        (int rcMissing, string outMissing, string errMissing) = RunCli(
            ["--no-ui", Path.Combine(tiny.Root, "does-not-exist")],
            headlessTui: false);
        Assert.Equal(1, rcMissing);
        Assert.Empty(outMissing);
        Assert.Contains("does not exist", errMissing);
    }

    private static int CountOpenFileDescriptors()
    {
        if (Directory.Exists("/proc/self/fd"))
            return Directory.GetFileSystemEntries("/proc/self/fd").Length;
        if (Directory.Exists("/dev/fd"))
            return Directory.GetFileSystemEntries("/dev/fd").Length;
        return 0;
    }

    private static byte[] NullTerm(string text)
    {
        int len = Encoding.UTF8.GetByteCount(text);
        byte[] buf = new byte[len + 1];
        Encoding.UTF8.GetBytes(text, buf);
        return buf;
    }

    private static unsafe void WriteFileAt(string stagingRoot, int dirFd, string name, int length)
    {
        string tmpName = ".stage_" + Guid.NewGuid().ToString("N");
        string tmpPath = Path.Combine(stagingRoot, tmpName);
        byte[] buf = new byte[length];
        if (length > 0)
            buf.AsSpan().Fill(0x3C);
        File.WriteAllBytes(tmpPath, buf);

        int rootFd = Open(NullTerm(stagingRoot), PosixORdonly);
        Assert.True(rootFd >= 0, $"open failed for stagingRoot (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            byte[] srcBytes = NullTerm(tmpName);
            byte[] dstBytes = NullTerm(name);
            fixed (byte* pSrc = srcBytes)
            fixed (byte* pDst = dstBytes)
            {
                int rc = RenameAt(rootFd, pSrc, dirFd, pDst);
                Assert.True(rc == 0, $"renameat failed for '{name}' (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = Close(rootFd);
        }
    }

    private static long RunCliAndParseTotal(IReadOnlyList<string> cliArgs, bool headlessTui)
    {
        (int exitCode, string stdout, string stderr) = RunCli(cliArgs, headlessTui);
        Assert.True(exitCode == 0, $"CLI failed ({exitCode}):\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Equal(string.Empty, stderr);

        string line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single();
        int tab = line.IndexOf('\t');
        Assert.True(tab > 0, $"Unexpected CLI output line: '{line}'");
        return long.Parse(line[..tab]);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(IReadOnlyList<string> cliArgs, bool headlessTui)
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
        return (proc.ExitCode, stdout, stderr);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed unsafe class DeepChainFixture : IDisposable
    {
        private readonly List<string> _segments;

        private DeepChainFixture(string root, List<string> segments, int leafPathLength, long expectedApparentBytes, int expectedFileCount)
        {
            Root = root;
            _segments = segments;
            LeafPathLength = leafPathLength;
            ExpectedApparentBytes = expectedApparentBytes;
            ExpectedFileCount = expectedFileCount;
        }

        public string Root { get; }
        public int LeafPathLength { get; }
        public long ExpectedApparentBytes { get; }
        public int ExpectedFileCount { get; }

        public static DeepChainFixture Create(int depth, string segmentPrefix, int fileEveryNLevels, int fileSize, bool includeLeafFile)
        {
            string root = Path.Combine(Path.GetTempPath(), "hyperdu-deep-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            List<string> segments = new(depth);
            long totalBytes = 0;
            int totalFiles = 0;
            int trackedLen = root.Length;

            int curFd = Open(NullTerm(root), PosixORdonly);
            Assert.True(curFd >= 0, $"Failed to open root '{root}'.");
            try
            {
                for (int level = 1; level <= depth; level++)
                {
                    string seg = $"{segmentPrefix}_{level:D3}_pad012345";
                    byte[] segBytes = NullTerm(seg);
                    fixed (byte* pSeg = segBytes)
                    {
                        Assert.Equal(0, MkdirAtPtr(curFd, pSeg, 0b111_101_101));
                        int nextFd = OpenAtPtr(curFd, pSeg, PosixORdonly);
                        Assert.True(nextFd >= 0, $"openat failed at level {level}");
                        _ = Close(curFd);
                        curFd = nextFd;
                    }

                    segments.Add(seg);
                    trackedLen += 1 + seg.Length;

                    if (fileEveryNLevels > 0 && level % fileEveryNLevels == 0)
                    {
                        WriteFileAt(root, curFd, $"file_{level:D3}.bin", fileSize);
                        totalBytes += fileSize;
                        totalFiles++;
                    }
                }

                if (includeLeafFile)
                {
                    WriteFileAt(root, curFd, "leaf_bottom.bin", fileSize);
                    totalBytes += fileSize;
                    totalFiles++;
                }
            }
            finally
            {
                _ = Close(curFd);
            }

            return new DeepChainFixture(root, segments, trackedLen, totalBytes, totalFiles);
        }

        public int OpenLevelFd(int level)
        {
            int fd = Open(NullTerm(Root), PosixORdonly);
            Assert.True(fd >= 0);
            for (int i = 0; i < level; i++)
            {
                int nextFd = OpenAt(fd, NullTerm(_segments[i]), PosixORdonly);
                _ = Close(fd);
                Assert.True(nextFd >= 0, $"OpenLevelFd failed at level {i + 1}");
                fd = nextFd;
            }

            return fd;
        }

        public void Dispose()
        {
            int rootFd = Open(NullTerm(Root), PosixORdonly);
            if (rootFd >= 0)
            {
                DeleteChildrenRecursive(rootFd);
            }

            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static void DeleteChildrenRecursive(int dirFd)
        {
            nint dir = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FdOpenDirInode64(dirFd)
                : FdOpenDir(dirFd);
            if (dir == 0)
            {
                _ = Close(dirFd);
                return;
            }

            List<(byte[] Name, bool IsDir)> children = [];
            try
            {
                int nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                int typeOffset = OperatingSystem.IsMacOS() ? 20 : 18;
                while (true)
                {
                    byte* entry = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                        ? ReadDirInode64(dir)
                        : ReadDir(dir);
                    if (entry == null)
                        break;

                    byte* pName = entry + nameOffset;
                    if (pName[0] == '.' && (pName[1] == 0 || (pName[1] == '.' && pName[2] == 0)))
                        continue;

                    int len = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(pName).Length;
                    byte[] name = new byte[len + 1];
                    new ReadOnlySpan<byte>(pName, len).CopyTo(name);
                    byte dtype = entry[typeOffset];
                    children.Add((name, dtype == 4));
                }

                foreach ((byte[] name, bool isDir) in children)
                {
                    fixed (byte* pName = name)
                    {
                        if (isDir)
                        {
                            _ = ChmodAt(dirFd, pName, 0b111_000_000, 0);
                            int childFd = OpenAtPtr(dirFd, pName, PosixORdonly);
                            if (childFd >= 0)
                                DeleteChildrenRecursive(childFd);
                            _ = UnlinkAtPtr(dirFd, pName, AtRemovedir);
                        }
                        else if (UnlinkAtPtr(dirFd, pName, 0) != 0)
                        {
                            _ = ChmodAt(dirFd, pName, 0b111_000_000, 0);
                            int childFd = OpenAtPtr(dirFd, pName, PosixORdonly);
                            if (childFd >= 0)
                            {
                                DeleteChildrenRecursive(childFd);
                                _ = UnlinkAtPtr(dirFd, pName, AtRemovedir);
                            }
                        }
                    }
                }
            }
            finally
            {
                _ = CloseDir(dir);
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(byte[] path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAt(int dirFd, byte[] name, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static unsafe partial int OpenAtPtr(int dirFd, byte* name, int flags);

    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static unsafe partial int RenameAt(int oldDirFd, byte* oldName, int newDirFd, byte* newName);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static partial int MkdirAt(int dirFd, byte[] name, uint mode);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static unsafe partial int MkdirAtPtr(int dirFd, byte* name, uint mode);

    [LibraryImport("libc", EntryPoint = "symlinkat", SetLastError = true)]
    private static partial int SymlinkAt(byte[] target, int newDirFd, byte[] linkPath);

    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static partial int LinkAt(int oldDirFd, byte[] oldPath, int newDirFd, byte[] newPath, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static unsafe partial int UnlinkAtPtr(int dirFd, byte* name, int flags);

    [LibraryImport("libc", EntryPoint = "fchmodat", SetLastError = true)]
    private static unsafe partial int ChmodAt(int dirFd, byte* path, uint mode, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "fdopendir")]
    private static partial nint FdOpenDir(int fd);

    [LibraryImport("libc", EntryPoint = "fdopendir$INODE64")]
    private static partial nint FdOpenDirInode64(int fd);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static unsafe partial byte* ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "readdir$INODE64")]
    private static unsafe partial byte* ReadDirInode64(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint dir);
}
