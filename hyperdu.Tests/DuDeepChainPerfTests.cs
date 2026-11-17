using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed partial class DuDeepChainPerfTests : IDisposable
{
    private readonly string? _prevDialect = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");

    public DuDeepChainPerfTests() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", "gnu");

    public void Dispose() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", _prevDialect);

    private const int O_RDONLY = 0;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int openat(int dirfd, byte[] pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mkdirat(int dirfd, byte[] pathname, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int linkat(int olddirfd, byte[] oldpath, int newdirfd, byte[] newpath, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int symlinkat(byte[] target, int newdirfd, byte[] linkpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlinkat(int dirfd, byte[] pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fchmodat(int dirfd, byte[] pathname, uint mode, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    private static int AtRemovedir => OperatingSystem.IsMacOS() ? 0x80 : 0x200;

    private static byte[] Nul(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    private sealed class DeepTempTree : IDisposable
    {
        public string Root { get; }
        private readonly List<int> _heldFds = [];

        public DeepTempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "hyperdu-deep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string CreateTemplateFile(string name, int size)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        public int OpenDir(string path)
        {
            int fd = openat(OperatingSystem.IsMacOS() ? -2 : -100, Nul(path), O_RDONLY);
            if (fd < 0)
                throw new IOException($"openat failed for {path}: errno={Marshal.GetLastPInvokeError()}");
            _heldFds.Add(fd);
            return fd;
        }

        public void BuildChain(
            int levels,
            Func<int, string> segmentName,
            Action<int, int, string>? onLevel = null)
        {
            int curFd = OpenDir(Root);
            try
            {
                for (int level = 1; level <= levels; level++)
                {
                    string seg = segmentName(level);
                    byte[] segBytes = Nul(seg);
                    if (mkdirat(curFd, segBytes, 0b111_101_101) != 0)
                        throw new IOException($"mkdirat failed at {level}: errno={Marshal.GetLastPInvokeError()}");

                    int nextFd = openat(curFd, segBytes, O_RDONLY);
                    if (nextFd < 0)
                        throw new IOException($"openat failed at {level}: errno={Marshal.GetLastPInvokeError()}");

                    onLevel?.Invoke(level, nextFd, seg);

                    if (curFd >= 0)
                        _ = close(curFd);
                    _heldFds.Remove(curFd);
                    _heldFds.Add(nextFd);
                    curFd = nextFd;
                }
            }
            finally
            {
                if (curFd >= 0)
                {
                    _ = close(curFd);
                    _heldFds.Remove(curFd);
                }
            }
        }

        public static void LinkFile(int rootFd, string templateName, int targetDirFd, string destName)
        {
            if (linkat(rootFd, Nul(templateName), targetDirFd, Nul(destName), 0) != 0)
                throw new IOException($"linkat failed for {destName}: errno={Marshal.GetLastPInvokeError()}");
        }

        public static int MkdirAndOpen(int parentFd, string name)
        {
            byte[] seg = Nul(name);
            if (mkdirat(parentFd, seg, 0b111_101_101) != 0)
                throw new IOException($"mkdirat failed for {name}: errno={Marshal.GetLastPInvokeError()}");
            int fd = openat(parentFd, seg, O_RDONLY);
            if (fd < 0)
                throw new IOException($"openat failed for {name}: errno={Marshal.GetLastPInvokeError()}");
            return fd;
        }

        public static void Symlink(int parentFd, string linkName, string target)
        {
            if (symlinkat(Nul(target), parentFd, Nul(linkName)) != 0)
                throw new IOException($"symlinkat failed for {linkName}: errno={Marshal.GetLastPInvokeError()}");
        }

        public static void Chmod(int parentFd, string name, uint mode)
        {
            int flags = OperatingSystem.IsMacOS() ? 0x0020 : 0;
            if (fchmodat(parentFd, Nul(name), mode, flags) != 0)
                throw new IOException($"fchmodat failed for {name}: errno={Marshal.GetLastPInvokeError()}");
        }

        public void Dispose()
        {
            foreach (int fd in _heldFds)
                _ = close(fd);
            _heldFds.Clear();

            try
            {
                DeleteDeepIterative(Root);
            }
            catch
            {
                // Best-effort cleanup of temp tree.
            }
        }

        private static void DeleteDeepIterative(string rootPath)
        {
            if (!Directory.Exists(rootPath))
                return;

            // Walk down chains storing only names (not open FDs) to avoid RLIMIT_NOFILE on 1000+ levels.
            int atFdCwd = OperatingSystem.IsMacOS() ? -2 : -100;
            List<byte[]> pathSegments = [];

            while (true)
            {
                int curFd = openat(atFdCwd, Nul(rootPath), O_RDONLY);
                if (curFd < 0)
                    break;

                bool descended = false;
                try
                {
                    foreach (byte[] seg in pathSegments)
                    {
                        int nextFd = openat(curFd, seg, O_RDONLY);
                        _ = close(curFd);
                        curFd = nextFd;
                        if (curFd < 0)
                            break;
                    }

                    if (curFd < 0)
                    {
                        pathSegments.RemoveAt(pathSegments.Count - 1);
                        continue;
                    }

                    using hyperdu.Core.Native.NativeDirectory? dir = hyperdu.Core.Native.NativeDirectory.WrapFd(curFd, out _);
                    if (dir == null)
                    {
                        _ = close(curFd);
                        break;
                    }

                    // WrapFd takes ownership of curFd via fdopendir on macOS or raw fd on Linux; dispose closes it.
                    curFd = -1;
                    byte[]? firstSubdir = null;
                    List<byte[]> nonDirs = [];
                    while (dir.MoveNext())
                    {
                        ReadOnlySpan<byte> name = dir.CurrentName;
                        byte[] nameNul = new byte[name.Length + 1];
                        name.CopyTo(nameNul);
                        if (dir.CurrentKind == hyperdu.Core.Native.NativeEntryKind.Directory)
                        {
                            firstSubdir ??= nameNul;
                        }
                        else
                        {
                            nonDirs.Add(nameNul);
                        }
                    }

                    foreach (byte[] fileNul in nonDirs)
                    {
                        if (unlinkat(dir.Fd, fileNul, 0) != 0)
                            _ = unlinkat(dir.Fd, fileNul, AtRemovedir);
                    }

                    if (firstSubdir != null)
                    {
                        pathSegments.Add(firstSubdir);
                        descended = true;
                    }
                    else
                    {
                        // Leaf directory cleared; if we are at root (pathSegments empty), we're done.
                        if (pathSegments.Count == 0)
                            break;
                    }
                }
                finally
                {
                    if (curFd >= 0)
                        _ = close(curFd);
                }

                if (!descended && pathSegments.Count > 0)
                {
                    byte[] leafSeg = pathSegments[^1];
                    pathSegments.RemoveAt(pathSegments.Count - 1);

                    int parentFd = openat(atFdCwd, Nul(rootPath), O_RDONLY);
                    if (parentFd >= 0)
                    {
                        foreach (byte[] seg in pathSegments)
                        {
                            int nextFd = openat(parentFd, seg, O_RDONLY);
                            _ = close(parentFd);
                            parentFd = nextFd;
                            if (parentFd < 0)
                                break;
                        }

                        if (parentFd >= 0)
                        {
                            _ = unlinkat(parentFd, leafSeg, AtRemovedir);
                            _ = close(parentFd);
                        }
                    }
                }
            }

            try
            {
                Directory.Delete(rootPath, false);
            }
            catch
            {
                // Ignore if already removed.
            }
        }
    }

    private sealed record RunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        int WorkersStarted,
        int ChunkBatches);

    private static RunResult RunSequential(string[] args, string dialect = "gnu")
    {
        string? prev = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");
        try
        {
            Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", dialect);
            using MemoryStream stdout = new();
            using MemoryStream stderr = new();
            DuParseOutcome outcome = DuOptions.Parse(args, "du", "1.0.0", Stream.Null, stdout, stderr, out DuOptions? opts);
            if (outcome != DuParseOutcome.Run || opts == null)
                return new RunResult(1, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), 0, 0);

            int rc = new SequentialWalker(opts, "du", Stream.Null, stdout, stderr).Run();
            return new RunResult(rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), 0, 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", prev);
        }
    }

    private static RunResult RunParallel(string[] args, int? workerBudget = null, string dialect = "gnu")
    {
        string? prev = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");
        try
        {
            Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", dialect);
            using MemoryStream stdout = new();
            using MemoryStream stderr = new();
            DuParseOutcome outcome = DuOptions.Parse(args, "du", "1.0.0", Stream.Null, stdout, stderr, out DuOptions? opts);
            if (outcome != DuParseOutcome.Run || opts == null)
                return new RunResult(1, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), 0, 0);

            ParallelDuWalker walker = new(opts, "du", Stream.Null, stdout, stderr, workerBudget);
            int rc = walker.Run();
            return new RunResult(
                rc,
                Encoding.UTF8.GetString(stdout.ToArray()),
                Encoding.UTF8.GetString(stderr.ToArray()),
                walker.WorkersStartedCount,
                walker.ChunkStatBatchesTriggered);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", prev);
        }
    }

    [UnixFact]
    public void DeepChainBeyondPathMax_MatchesSequentialByteForByte_AllCoreFlagsAndStaysInline()
    {
        using DeepTempTree tree = new();
        tree.CreateTemplateFile("tmpl.bin", 512);
        tree.CreateTemplateFile("tmpl.tmp", 256);
        int rootFd = tree.OpenDir(tree.Root);

        // 350 levels x 16 bytes = ~5,600 bytes (> 4096 Linux PATH_MAX and > 1024 macOS PATH_MAX).
        tree.BuildChain(350, level => $"d_{level:D3}_seg12345", (level, dirFd, _) =>
        {
            if (level % 25 == 0)
                DeepTempTree.LinkFile(rootFd, "tmpl.bin", dirFd, $"leaf_{level:D3}.bin");
            if (level % 50 == 0)
                DeepTempTree.LinkFile(rootFd, "tmpl.tmp", dirFd, $"skip_{level:D3}.tmp");
        });

        string[][] argMatrix =
        [
            ["-B1", "-s", tree.Root],
            ["-B1", "-a", tree.Root],
            ["-B1", "-d", "1", tree.Root],
            ["-B1", "-d", "2", tree.Root],
            ["-B1", "-S", "-c", tree.Root],
            ["-B1", "--time", "--time-style=full-iso", "-a", tree.Root],
            ["-B1", "--exclude=*.tmp", "-a", tree.Root],
            ["-B1", "--exclude=*/d_150_*", "-a", tree.Root],
            ["-B1", "--exclude=*/d_300_*", "-s", tree.Root],
            ["-B1", "-a", tree.Root + "/"]
        ];

        foreach (string[] args in argMatrix)
        {
            RunResult seq = RunSequential(args);
            RunResult par = RunParallel(args, workerBudget: 4);
            Assert.True(seq.ExitCode == 0, $"args=[{string.Join(' ', args)}] seqErr={seq.Stderr}");
            Assert.Equal(seq.ExitCode, par.ExitCode);
            Assert.Equal(seq.Stdout, par.Stdout);
            Assert.Equal(seq.Stderr, par.Stderr);
            Assert.Equal(0, par.WorkersStarted);
        }
    }

    [UnixFact]
    public void DeepChain_WithFollowSymlinks_MatchesSequentialUnderAndBeyondPathMax()
    {
        // Part A: ~120 levels under PATH_MAX with symlinks to files and dirs.
        using DeepTempTree underTree = new();
        underTree.CreateTemplateFile("payload.bin", 1024);
        int underRootFd = underTree.OpenDir(underTree.Root);
        underTree.BuildChain(120, level => $"u_{level:D3}", (level, dirFd, _) =>
        {
            if (level == 60)
                DeepTempTree.LinkFile(underRootFd, "payload.bin", dirFd, "target.bin");
            if (level == 120)
            {
                DeepTempTree.Symlink(dirFd, "link_to_file", "../../../" + string.Join("/", Enumerable.Repeat("..", 57)) + "/target.bin");
                DeepTempTree.LinkFile(underRootFd, "payload.bin", dirFd, "leaf.bin");
            }
        });

        foreach (string[] args in new[]
                 {
                     new[] { "-B1", "-L", "-s", underTree.Root },
                     new[] { "-B1", "-L", "-a", underTree.Root }
                 })
        {
            RunResult seq = RunSequential(args);
            RunResult par = RunParallel(args, workerBudget: 4);
            Assert.Equal(seq.ExitCode, par.ExitCode);
            Assert.Equal(seq.Stdout, par.Stdout);
            Assert.Equal(seq.Stderr, par.Stderr);
            Assert.Equal(0, par.WorkersStarted);
        }

        // Part B: > PATH_MAX (~5,760 bytes) under -L reproduces exact ENAMETOOLONG parity with SequentialWalker.
        using DeepTempTree overTree = new();
        overTree.BuildChain(320, level => $"o_{level:D3}_segment1234");
        foreach (string[] args in new[]
                 {
                     new[] { "-B1", "-L", "-s", overTree.Root },
                     new[] { "-B1", "-L", "-a", overTree.Root }
                 })
        {
            RunResult seq = RunSequential(args);
            RunResult par = RunParallel(args, workerBudget: 4);
            Assert.Equal(seq.ExitCode, par.ExitCode);
            Assert.Equal(seq.Stdout, par.Stdout);
            Assert.Equal(seq.Stderr, par.Stderr);
        }
    }

    [UnixFact]
    public void DeepChainBeyondPathMax_UnreadableDirectoryPast4500Bytes_ReconstructsPathWithoutOsPathLimit()
    {
        if (Environment.IsPrivilegedProcess)
            return;

        using DeepTempTree tree = new();
        tree.CreateTemplateFile("tmpl.bin", 512);
        int rootFd = tree.OpenDir(tree.Root);
        int curFd = rootFd;
        int lockParentFd = -1;
        string lockName = "x_310_segment1234";

        for (int level = 1; level <= 315; level++)
        {
            string seg = $"x_{level:D3}_segment1234";
            int nextFd = DeepTempTree.MkdirAndOpen(curFd, seg);
            if (curFd != rootFd && curFd != lockParentFd)
                _ = close(curFd);

            if (level == 309)
                lockParentFd = nextFd;
            if (level == 310)
                DeepTempTree.LinkFile(rootFd, "tmpl.bin", nextFd, "hidden.bin");

            curFd = nextFd;
        }

        if (curFd != lockParentFd)
            _ = close(curFd);

        DeepTempTree.Chmod(lockParentFd, lockName, 0);
        try
        {
            foreach (string[] args in new[]
                     {
                         new[] { "-B1", "-s", tree.Root },
                         new[] { "-B1", "-a", tree.Root },
                         new[] { "-B1", "-d", "1", tree.Root }
                     })
            {
                RunResult seqGnu = RunSequential(args, dialect: "gnu");
                RunResult parGnu = RunParallel(args, workerBudget: 4, dialect: "gnu");
                Assert.Equal(1, seqGnu.ExitCode);
                Assert.Equal(seqGnu.Stdout, parGnu.Stdout);
                Assert.Equal(seqGnu.Stderr, parGnu.Stderr);
                Assert.Contains(lockName, parGnu.Stderr, StringComparison.Ordinal);
                Assert.True(parGnu.Stderr.Length > 5000);
            }

            RunResult seqBsd = RunSequential(["-A", "-B", "1", "-s", tree.Root], dialect: "bsd");
            RunResult parBsd = RunParallel(["-A", "-B", "1", "-s", tree.Root], workerBudget: 4, dialect: "bsd");
            Assert.Equal(1, seqBsd.ExitCode);
            Assert.Equal(seqBsd.Stdout, parBsd.Stdout);
            Assert.Equal(seqBsd.Stderr, parBsd.Stderr);
        }
        finally
        {
            DeepTempTree.Chmod(lockParentFd, lockName, 0b111_101_101);
            _ = close(lockParentFd);
        }
    }

    [UnixFact]
    public void DeepChain_SymlinkCycles_InlineAndConcurrentDepthAbove32_MatchGnuAndBsd()
    {
        // Part A: single linear 60-level chain (stays inline, !_workersStarted).
        using DeepTempTree inlineTree = new();
        inlineTree.CreateTemplateFile("item.bin", 512);
        int inlineRootFd = inlineTree.OpenDir(inlineTree.Root);
        inlineTree.BuildChain(60, level => $"c_{level:D2}", (level, dirFd, _) =>
        {
            if (level == 55)
            {
                DeepTempTree.LinkFile(inlineRootFd, "item.bin", dirFd, "item.bin");
                string upTo10 = string.Join("/", Enumerable.Repeat("..", 45));
                DeepTempTree.Symlink(dirFd, "back_to_10", upTo10);
                DeepTempTree.Symlink(dirFd, "self", ".");
            }
        });

        RunResult seqGnuInline = RunSequential(["-B1", "-L", "-a", inlineTree.Root], dialect: "gnu");
        RunResult parGnuInline = RunParallel(["-B1", "-L", "-a", inlineTree.Root], workerBudget: 4, dialect: "gnu");
        Assert.Equal(0, seqGnuInline.ExitCode);
        Assert.Equal(seqGnuInline.Stdout, parGnuInline.Stdout);
        Assert.Equal(string.Empty, parGnuInline.Stderr);
        Assert.Equal(0, parGnuInline.WorkersStarted);

        RunResult seqBsdInline = RunSequential(["-A", "-B", "1", "-L", inlineTree.Root], dialect: "bsd");
        RunResult parBsdInline = RunParallel(["-A", "-B", "1", "-L", inlineTree.Root], workerBudget: 4, dialect: "bsd");
        Assert.Equal(1, seqBsdInline.ExitCode);
        Assert.Equal(seqBsdInline.ExitCode, parBsdInline.ExitCode);
        Assert.Equal(seqBsdInline.Stderr, parBsdInline.Stderr);
        Assert.Contains("Can't follow symlink cycle from ", parBsdInline.Stderr, StringComparison.Ordinal);

        // Part B: branched tree forcing workers (_workersStarted == true) with a 60-level branch (Depth > 32)
        // containing ancestor cycles, exercising WorkerScratch AncestorSet + LCA alignment.
        using DeepTempTree branchedTree = new();
        branchedTree.CreateTemplateFile("item.bin", 512);
        int brRootFd = branchedTree.OpenDir(branchedTree.Root);
        for (int b = 0; b < 4; b++)
        {
            int bFd = DeepTempTree.MkdirAndOpen(brRootFd, $"br_{b}");
            DeepTempTree.LinkFile(brRootFd, "item.bin", bFd, "bfile.bin");
            int curFd = bFd;
            for (int level = 1; level <= 50; level++)
            {
                int nextFd = DeepTempTree.MkdirAndOpen(curFd, $"d_{level:D2}");
                if (curFd != bFd)
                    _ = close(curFd);
                curFd = nextFd;
                if (level == 45)
                {
                    DeepTempTree.LinkFile(brRootFd, "item.bin", curFd, "deep.bin");
                    string upTo10 = string.Join("/", Enumerable.Repeat("..", 35));
                    DeepTempTree.Symlink(curFd, "cycle_up", upTo10);
                    DeepTempTree.Symlink(curFd, "cycle_self", ".");
                }
            }

            _ = close(curFd);
            _ = close(bFd);
        }

        RunResult seqGnuBr = RunSequential(["-B1", "-L", "-l", "-a", branchedTree.Root], dialect: "gnu");
        RunResult parGnuBr = RunParallel(["-B1", "-L", "-l", "-a", branchedTree.Root], workerBudget: 4, dialect: "gnu");
        Assert.Equal(0, seqGnuBr.ExitCode);
        Assert.Equal(seqGnuBr.Stdout, parGnuBr.Stdout);
        Assert.Equal(string.Empty, parGnuBr.Stderr);
        Assert.True(parGnuBr.WorkersStarted >= 2);

        RunResult seqBsdBr = RunSequential(["-A", "-B", "1", "-L", branchedTree.Root], dialect: "bsd");
        RunResult parBsdBr = RunParallel(["-A", "-B", "1", "-L", branchedTree.Root], workerBudget: 4, dialect: "bsd");
        Assert.Equal(1, seqBsdBr.ExitCode);
        Assert.Equal(seqBsdBr.ExitCode, parBsdBr.ExitCode);
        Assert.Contains("Can't follow symlink cycle from ", parBsdBr.Stderr, StringComparison.Ordinal);
    }

    [UnixFact]
    public void BranchedDeepChainBeyondPathMax_ExercisesWorkerFallbackOpenExcludesAndLargeDirChunks()
    {
        using DeepTempTree tree = new();
        tree.CreateTemplateFile("tmpl.bin", 512);
        int rootFd = tree.OpenDir(tree.Root);

        // Descend 280 levels (~4,700 bytes > PATH_MAX), then fork into 4 branches including a >512-file dir.
        int forkFd = -1;
        int curFd = rootFd;
        for (int level = 1; level <= 280; level++)
        {
            int nextFd = DeepTempTree.MkdirAndOpen(curFd, $"p_{level:D3}_segment12");
            if (curFd != rootFd)
                _ = close(curFd);
            curFd = nextFd;
            if (level == 280)
                forkFd = nextFd;
        }

        for (int b = 0; b < 4; b++)
        {
            int bFd = DeepTempTree.MkdirAndOpen(forkFd, $"branch_{b}");
            if (b == 0)
            {
                for (int f = 0; f < 600; f++)
                    DeepTempTree.LinkFile(rootFd, "tmpl.bin", bFd, $"f_{f:D4}.dat");
            }
            else
            {
                int subFd = DeepTempTree.MkdirAndOpen(bFd, "nested");
                DeepTempTree.LinkFile(rootFd, "tmpl.bin", subFd, "leaf.dat");
                _ = close(subFd);
            }

            _ = close(bFd);
        }

        _ = close(forkFd);

        foreach (string[] args in new[]
                 {
                     new[] { "-B1", "-s", tree.Root },
                     new[] { "-B1", "-a", tree.Root },
                     new[] { "-B1", "--exclude=*/branch_1/*", "-a", tree.Root },
                     new[] { "-B1", "--exclude=*/f_025*", "-a", tree.Root }
                 })
        {
            RunResult seq = RunSequential(args);
            RunResult par = RunParallel(args, workerBudget: 4);
            Assert.Equal(0, seq.ExitCode);
            Assert.Equal(seq.Stdout, par.Stdout);
            Assert.Equal(seq.Stderr, par.Stderr);
            Assert.True(par.WorkersStarted >= 2, $"args=[{string.Join(' ', args)}] workers={par.WorkersStarted}");
            Assert.True(par.ChunkBatches >= 1, $"args=[{string.Join(' ', args)}] chunks={par.ChunkBatches}");
        }

        // Excluding an ancestor directory at level 25 short-circuits before the fork, staying single-threaded.
        string[] earlyExclude = ["-B1", "--exclude=*/p_025_*", "-a", tree.Root];
        RunResult seqEarly = RunSequential(earlyExclude);
        RunResult parEarly = RunParallel(earlyExclude, workerBudget: 4);
        Assert.Equal(0, seqEarly.ExitCode);
        Assert.Equal(seqEarly.Stdout, parEarly.Stdout);
        Assert.Equal(0, parEarly.WorkersStarted);
    }

    [UnixFact]
    public void DeepChain_1100Levels_CompletesAndMatchesGnuDuAndExpectedStructure()
    {
        using DeepTempTree tree = new();
        tree.CreateTemplateFile("item.bin", 256);
        int rootFd = tree.OpenDir(tree.Root);

        tree.BuildChain(1100, level => $"l_{level:D4}_seg", (level, dirFd, _) =>
        {
            if (level % 100 == 0)
                DeepTempTree.LinkFile(rootFd, "item.bin", dirFd, $"f_{level:D4}.bin");
        });

        // Note: SequentialWalker hits its OpenFdSafetyCap=850 and falls back to OpenRootDirectory (ENAMETOOLONG > PATH_MAX),
        // while ParallelDuWalker traverses 1100+ levels with constant O(1) open FDs.
        RunResult sumPar = RunParallel(["-b", "-s", tree.Root], workerBudget: 4);
        Assert.Equal(0, sumPar.ExitCode);
        Assert.Equal(string.Empty, sumPar.Stderr);
        Assert.Equal(0, sumPar.WorkersStarted);
        Assert.Equal($"256\t{tree.Root}\n", sumPar.Stdout);

        RunResult allPar = RunParallel(["-b", "-l", "-a", tree.Root], workerBudget: 4);
        Assert.Equal(0, allPar.ExitCode);
        Assert.Equal(string.Empty, allPar.Stderr);
        Assert.Equal(0, allPar.WorkersStarted);
        string[] lines = allPar.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 1 root + 1100 dirs + 1 template file + 11 linked files = 1113 lines.
        Assert.Equal(1113, lines.Length);

        RunResult d1Par = RunParallel(["-B1", "-d", "1", tree.Root], workerBudget: 4);
        Assert.Equal(0, d1Par.ExitCode);
        Assert.Equal(string.Empty, d1Par.Stderr);
        Assert.Equal(2, d1Par.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // If GNU du is installed on PATH (Linux), verify exact byte-for-byte parity at 1100 levels.
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/du"))
        {
            using System.Diagnostics.Process proc = new();
            proc.StartInfo.FileName = "/usr/bin/du";
            proc.StartInfo.ArgumentList.Add("-B1");
            proc.StartInfo.ArgumentList.Add("-l");
            proc.StartInfo.ArgumentList.Add("-a");
            proc.StartInfo.ArgumentList.Add(tree.Root);
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.UseShellExecute = false;
            proc.Start();
            string sysOut = proc.StandardOutput.ReadToEnd();
            string sysErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && sysErr.Length == 0)
            {
                RunResult parB1 = RunParallel(["-B1", "-l", "-a", tree.Root], workerBudget: 4);
                Assert.Equal(sysOut, parB1.Stdout);
            }
        }
    }

    [LinuxFact("Reads /proc/self/fd to verify zero file descriptor leaks across completion, errors, and cancellation.")]
    public void DeepChainAndCancellation_DoNotLeakFileDescriptors()
    {
        using DeepTempTree deepTree = new();
        deepTree.CreateTemplateFile("item.bin", 256);
        int deepRootFd = deepTree.OpenDir(deepTree.Root);
        deepTree.BuildChain(400, level => $"fd_{level:D3}_segment12", (level, dirFd, _) =>
        {
            if (level % 40 == 0)
                DeepTempTree.LinkFile(deepRootFd, "item.bin", dirFd, $"f_{level:D3}.bin");
        });

        // Branched tree with large dirs for multi-worker cancellation check.
        using DeepTempTree wideBranched = new();
        wideBranched.CreateTemplateFile("item.bin", 256);
        int wbRootFd = wideBranched.OpenDir(wideBranched.Root);
        for (int b = 0; b < 6; b++)
        {
            int bFd = DeepTempTree.MkdirAndOpen(wbRootFd, $"b_{b}");
            for (int f = 0; f < 600; f++)
                DeepTempTree.LinkFile(wbRootFd, "item.bin", bFd, $"f_{f:D4}.bin");
            _ = close(bFd);
        }

        // Cycle tree for BSD abort check.
        using DeepTempTree bsdCycleTree = new();
        int cycRootFd = bsdCycleTree.OpenDir(bsdCycleTree.Root);
        int cycSubFd = DeepTempTree.MkdirAndOpen(cycRootFd, "sub");
        DeepTempTree.Symlink(cycSubFd, "up", bsdCycleTree.Root);
        _ = close(cycSubFd);

        // Warmup once so runtime/Static initializers don't skew FD baseline.
        _ = RunParallel(["-B1", "-s", deepTree.Root], workerBudget: 4);
        int baselineFds = CountOpenFds();

        // 1. Normal runs on 400-level deep chain.
        _ = RunParallel(["-B1", "-s", deepTree.Root], workerBudget: 4);
        _ = RunParallel(["-B1", "-a", deepTree.Root], workerBudget: 4);
        _ = RunParallel(["-B1", "--exclude=*/fd_200_*", "-a", deepTree.Root], workerBudget: 4);
        Assert.Equal(baselineFds, CountOpenFds());

        // 2. Early cancellation during inline deep chain -a emission.
        using (CancellationTokenSource ctsInline = new())
        using (CancelAfterBytesStream cancelOut = new(1024, ctsInline))
        using (MemoryStream err = new())
        {
            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["-B1", "-a", deepTree.Root], "du", "1.0", Stream.Null, cancelOut, err, out DuOptions? opts));
            ParallelDuWalker walker = new(opts!, "du", Stream.Null, cancelOut, err, workerBudgetOverride: 4);
            Assert.ThrowsAny<OperationCanceledException>(() => walker.Run([Encoding.UTF8.GetBytes(deepTree.Root)], ctsInline.Token));
        }

        Assert.Equal(baselineFds, CountOpenFds());

        // 3. Early cancellation during multi-worker large-dir scan (use -l so all 3600 hardlinked files emit >200KB).
        using (CancellationTokenSource ctsWorkers = new())
        using (CancelAfterBytesStream cancelOut = new(512, ctsWorkers))
        using (MemoryStream err = new())
        {
            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["-B1", "-l", "-a", wideBranched.Root], "du", "1.0", Stream.Null, cancelOut, err, out DuOptions? opts));
            ParallelDuWalker walker = new(opts!, "du", Stream.Null, cancelOut, err, workerBudgetOverride: 4);
            Assert.ThrowsAny<OperationCanceledException>(() => walker.Run([Encoding.UTF8.GetBytes(wideBranched.Root)], ctsWorkers.Token));
        }

        Assert.Equal(baselineFds, CountOpenFds());

        // 4. BSD symlink cycle abort.
        RunResult bsdAbort = RunParallel(["-L", bsdCycleTree.Root], workerBudget: 4, dialect: "bsd");
        Assert.Equal(1, bsdAbort.ExitCode);
        Assert.Equal(baselineFds, CountOpenFds());
    }

    private static int CountOpenFds()
    {
        return Directory.GetFileSystemEntries("/proc/self/fd").Length;
    }

    private sealed class CancelAfterBytesStream : Stream
    {
        private readonly long _threshold;
        private readonly CancellationTokenSource _cts;
        private long _written;

        public CancelAfterBytesStream(long threshold, CancellationTokenSource cts)
        {
            _threshold = threshold;
            _cts = cts;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written >= _threshold)
                _cts.Cancel();
        }
    }
}
