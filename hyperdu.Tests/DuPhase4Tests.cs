using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;
using hyperdu.Core.Scanning;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed partial class DuPhase4Tests : IDisposable
{
    private readonly string? _prevDialect = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");

    public DuPhase4Tests() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", "gnu");

    public void Dispose() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", _prevDialect);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int chmod(byte[] pathname, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int link(byte[] oldpath, byte[] newpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int open(byte[] pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int openat(int dirfd, byte[] pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mkdirat(int dirfd, byte[] pathname, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int linkat(int olddirfd, byte[] oldpath, int newdirfd, byte[] newpath, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlinkat(int dirfd, byte[] pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int oldfd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    private static void SetMode(string path, uint mode)
    {
        byte[] utf8 = [.. Encoding.UTF8.GetBytes(path), 0];
        if (chmod(utf8, mode) != 0)
            throw new IOException($"chmod failed for {path} with errno {Marshal.GetLastPInvokeError()}");
    }

    private static void CreateHardLink(string target, string linkPath)
    {
        byte[] oldUtf8 = [.. Encoding.UTF8.GetBytes(target), 0];
        byte[] newUtf8 = [.. Encoding.UTF8.GetBytes(linkPath), 0];
        if (link(oldUtf8, newUtf8) != 0)
            throw new IOException($"link failed with errno {Marshal.GetLastPInvokeError()}");
    }

    private sealed class CapturingResult
    {
        public int ExitCode { get; init; }
        public string Stdout { get; init; } = string.Empty;
        public string Stderr { get; init; } = string.Empty;
        public int WorkersStarted { get; init; }
        public long PeakUnprinted { get; init; }
        public int BackpressurePauses { get; init; }
        public int ChunkBatches { get; init; }
    }

    private static CapturingResult RunSequential(string[] args, byte[]? stdinBytes = null)
    {
        using MemoryStream stdin = stdinBytes != null ? new MemoryStream(stdinBytes) : new MemoryStream();
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        DuParseOutcome outcome = DuOptions.Parse(args, "du", "1.0.0", stdin, stdout, stderr, out DuOptions? opts);
        int rc = outcome switch
        {
            DuParseOutcome.ExitSuccess => 0,
            DuParseOutcome.ExitFailure => 1,
            DuParseOutcome.Run when opts != null => new SequentialWalker(opts, "du", stdin, stdout, stderr).Run(),
            _ => 1
        };

        return new CapturingResult
        {
            ExitCode = rc,
            Stdout = Encoding.UTF8.GetString(stdout.ToArray()),
            Stderr = Encoding.UTF8.GetString(stderr.ToArray())
        };
    }

    private static CapturingResult RunParallel(string[] args, int? workerBudgetOverride = null, byte[]? stdinBytes = null)
    {
        using MemoryStream stdin = stdinBytes != null ? new MemoryStream(stdinBytes) : new MemoryStream();
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        DuParseOutcome outcome = DuOptions.Parse(args, "du", "1.0.0", stdin, stdout, stderr, out DuOptions? opts);
        if (outcome == DuParseOutcome.ExitSuccess)
            return new CapturingResult { ExitCode = 0 };
        if (outcome != DuParseOutcome.Run || opts == null)
            return new CapturingResult { ExitCode = 1, Stderr = Encoding.UTF8.GetString(stderr.ToArray()) };

        ParallelDuWalker walker = new(opts, "du", stdin, stdout, stderr, workerBudgetOverride);
        int rc = walker.Run();
        return new CapturingResult
        {
            ExitCode = rc,
            Stdout = Encoding.UTF8.GetString(stdout.ToArray()),
            Stderr = Encoding.UTF8.GetString(stderr.ToArray()),
            WorkersStarted = walker.WorkersStartedCount,
            PeakUnprinted = walker.PeakUnprintedEntries,
            BackpressurePauses = walker.BackpressurePauseCount,
            ChunkBatches = walker.ChunkStatBatchesTriggered
        };
    }

    private static void WithEnv(string key, string? value, Action action)
    {
        string? backup = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, backup);
        }
    }

    [Fact]
    public void MountHeuristics_ParsesThreadCountsAndNetworkFileSystems()
    {
        Assert.Null(MountHeuristics.ParseWorkerCount(null));
        Assert.Null(MountHeuristics.ParseWorkerCount(""));
        Assert.Null(MountHeuristics.ParseWorkerCount("0"));
        Assert.Null(MountHeuristics.ParseWorkerCount("-4"));
        Assert.Null(MountHeuristics.ParseWorkerCount("abc"));
        Assert.Equal(1, MountHeuristics.ParseWorkerCount("1"));
        Assert.Equal(8, MountHeuristics.ParseWorkerCount(" 8 "));

        Assert.True(MountHeuristics.IsNetworkFsType("nfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("nfs4"));
        Assert.True(MountHeuristics.IsNetworkFsType("cifs"));
        Assert.True(MountHeuristics.IsNetworkFsType("smb3"));
        Assert.True(MountHeuristics.IsNetworkFsType("fuse.sshfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("glusterfs"));
        Assert.True(MountHeuristics.IsNetworkFsType("ceph"));
        Assert.False(MountHeuristics.IsNetworkFsType("ext4"));
        Assert.False(MountHeuristics.IsNetworkFsType("tmpfs"));

        int explicitBudget = MountHeuristics.ResolveWorkerBudget(4, Path.GetTempPath(), out bool isNet, out bool isRot);
        Assert.Equal(4, explicitBudget);
        Assert.False(isNet);
        Assert.False(isRot);
    }

    [UnixFact]
    public void ParallelWalker_MatchesSequentialByteForByte_AcrossFlagMatrixAndThreadCounts()
    {
        using TempTree tree = new();

        for (int b = 0; b < 5; b++)
        {
            tree.File($"branch_{b}/sub_a/f1.bin", 1024 + b * 256);
            tree.File($"branch_{b}/sub_b/nested/f2.tmp", 4096);
            tree.Dir($"branch_{b}/empty_sub");
        }

        string srcA = tree.File("hardlinks/src_a.bin", 4096);
        string srcB = tree.File("hardlinks/src_b.bin", 8192);
        string linkA = Path.Combine(tree.Dir("links_other"), "hl_a.bin");
        string linkB = Path.Combine(tree.Dir("branch_0/sub_a"), "hl_b.bin");
        CreateHardLink(srcA, linkA);
        CreateHardLink(srcB, linkB);

        File.CreateSymbolicLink(Path.Combine(tree.Dir("symlinks"), "to_file"), "../hardlinks/src_a.bin");
        File.CreateSymbolicLink(Path.Combine(tree.Dir("symlinks"), "to_branch1"), "../branch_1");
        File.CreateSymbolicLink(Path.Combine(tree.Dir("symlinks"), "dangling"), "nonexistent_target");
        File.CreateSymbolicLink(Path.Combine(tree.Dir("branch_2/sub_a"), "loop_up"), "..");

        string lockedDir = tree.Dir("unreadable/secret");
        tree.File("unreadable/secret/hidden.bin", 512);
        SetMode(lockedDir, 0);

        try
        {
            string[][] argSets =
            [
                ["-B1", tree.Root],
                ["-B1", "-a", tree.Root],
                ["-B1", "-s", tree.Root],
                ["-B1", "-S", tree.Root],
                ["-B1", "-d", "1", tree.Root],
                ["-B1", "-d", "2", "-c", tree.Root],
                ["-B1", "-l", "-a", tree.Root],
                ["-B1", "-x", tree.Root],
                ["-B1", "-L", "-a", tree.Root],
                ["-B1", "-D", "-a", tree.Root],
                ["--inodes", "-a", tree.Root],
                ["--time=mtime", "--time-style=full-iso", "-a", tree.Root],
                ["-B1", "--exclude=*.tmp", "-a", tree.Root],
                ["-B1", "-t", "+4096", "-a", tree.Root],
                ["-B1", "-t", "-4096", "-a", tree.Root],
                ["-B1", "-a", Path.Combine(tree.Root, "branch_0"), Path.Combine(tree.Root, "links_other"), Path.Combine(tree.Root, "branch_0")],
                ["-B1", "-a", tree.Root, Path.Combine(tree.Root, "branch_0/sub_a/f1.bin")]
            ];

            foreach (string[] args in argSets)
            {
                CapturingResult seq = RunSequential(args);
                foreach (int budget in new[] { 1, 2, 4, 8 })
                {
                    CapturingResult par = RunParallel(args, budget);
                    Assert.Equal(seq.ExitCode, par.ExitCode);
                    Assert.Equal(seq.Stdout, par.Stdout);
                    Assert.Equal(seq.Stderr, par.Stderr);
                }
            }

            // --files0-from streaming across operands
            byte[] files0Input = Encoding.UTF8.GetBytes(
                $"{Path.Combine(tree.Root, "branch_1")}\0{Path.Combine(tree.Root, "links_other")}\0");
            string[] files0Args = ["-B1", "-c", "--files0-from=-"];
            CapturingResult seqF0 = RunSequential(files0Args, files0Input);
            CapturingResult parF0 = RunParallel(files0Args, 4, files0Input);
            Assert.Equal(seqF0.ExitCode, parF0.ExitCode);
            Assert.Equal(seqF0.Stdout, parF0.Stdout);
            Assert.Equal(seqF0.Stderr, parF0.Stderr);
        }
        finally
        {
            SetMode(lockedDir, 0b111_101_101);
        }
    }

    [UnixFact]
    public void ParallelWalker_LargeDirectoryChunkStatx_TriggersAndMatchesSequential()
    {
        using TempTree tree = new();
        string hotDir = tree.Dir("hot");
        for (int i = 0; i < 1600; i++)
            tree.File($"hot/f_{i:D4}.dat", (i % 7) * 256);

        tree.File("hot/sub/deep.dat", 1024);

        foreach (string[] args in new[]
                 {
                     new[] { "-B1", tree.Root },
                     new[] { "-B1", "-a", tree.Root },
                     new[] { "-B1", "-l", tree.Root },
                     new[] { "-B1", "--exclude=*_01*", "-a", tree.Root },
                     new[] { "-B1", "-L", "-a", tree.Root }
                 })
        {
            CapturingResult seq = RunSequential(args);
            CapturingResult par = RunParallel(args, workerBudgetOverride: 4);

            Assert.Equal(seq.ExitCode, par.ExitCode);
            Assert.Equal(seq.Stdout, par.Stdout);
            Assert.Equal(seq.Stderr, par.Stderr);
            Assert.True(par.ChunkBatches >= 1, $"Expected chunk stat batch for {string.Join(" ", args)}");
            Assert.True(par.WorkersStarted >= 2);
        }
    }

    [UnixFact]
    public void ParallelWalker_AdaptiveFanOut_SkipsThreadsOnSmallAndSingleChainTrees()
    {
        using TempTree tiny = new();
        for (int i = 0; i < 5; i++)
            tiny.File($"f{i}.txt", 128);

        CapturingResult tinyRes = RunParallel(["-B1", tiny.Root], workerBudgetOverride: 4);
        Assert.Equal(0, tinyRes.ExitCode);
        Assert.Equal(0, tinyRes.WorkersStarted);

        // Linear deep chain has only 1 child dir per level: zero pending sibling fanout -> stays inline.
        using TempTree deep = new();
        string cur = deep.Root;
        for (int i = 0; i < 150; i++)
        {
            cur = Path.Combine(cur, $"l{i:D3}");
            Directory.CreateDirectory(cur);
            if (i % 25 == 0)
                File.WriteAllBytes(Path.Combine(cur, "item.bin"), new byte[512]);
        }

        CapturingResult deepSeq = RunSequential(["-B1", deep.Root]);
        CapturingResult deepPar = RunParallel(["-B1", deep.Root], workerBudgetOverride: 4);
        Assert.Equal(deepSeq.Stdout, deepPar.Stdout);
        Assert.Equal(0, deepPar.WorkersStarted);

        // Multi-branch tree triggers adaptive fan-out when budget > 1, but stays inline for HYPERDU_THREADS=1.
        using TempTree branched = new();
        for (int i = 0; i < 6; i++)
        {
            treeCreateBranch(branched, i);
        }

        CapturingResult singleBudget = RunParallel(["-B1", branched.Root], workerBudgetOverride: 1);
        Assert.Equal(0, singleBudget.WorkersStarted);

        CapturingResult multiBudget = RunParallel(["-B1", branched.Root], workerBudgetOverride: 4);
        Assert.True(multiBudget.WorkersStarted >= 2);

        WithEnv("HYPERDU_THREADS", "1", () =>
        {
            CapturingResult envSingle = RunParallel(["-B1", branched.Root], workerBudgetOverride: null);
            Assert.Equal(0, envSingle.WorkersStarted);
        });

        static void treeCreateBranch(TempTree t, int b)
        {
            for (int c = 0; c < 3; c++)
                t.File($"b_{b}/c_{c}/leaf.bin", 256);
        }
    }

    [UnixFact]
    public void ParallelWalker_DeepBeyondPathMax_MatchesSequentialInBothSingleAndMultiWorkerModes()
    {
        using TempTree tree = new();
        string segment = new('d', 120);
        string deepRoot = tree.Dir("deep");
        tree.Dir("sibling_a");
        tree.Dir("sibling_b");
        tree.Dir("sibling_c");
        tree.File("sibling_a/x.bin", 1024);
        tree.File("tmpl.bin", 512);

        const int ORdonly = 0;
        int atRemovedir = OperatingSystem.IsMacOS() ? 0x80 : 0x200;
        List<(int ParentFd, byte[] Name, bool IsDir)> createdReversed = [];

        // Walk fd-relatively past PATH_MAX (>4800 bytes) with a fork so queued parallel workers also open >PATH_MAX paths.
        int rootFd = open([.. Encoding.UTF8.GetBytes(tree.Root), 0], ORdonly);
        int curFd = open([.. Encoding.UTF8.GetBytes(deepRoot), 0], ORdonly);
        Assert.True(rootFd >= 0 && curFd >= 0);
        List<int> openFds = [rootFd, curFd];
        try
        {
            for (int i = 0; i < 42; i++)
            {
                byte[] segBytes = [.. Encoding.UTF8.GetBytes($"{segment}_{i:D2}"), 0];
                Assert.Equal(0, mkdirat(curFd, segBytes, 0b111_101_101));
                int nextFd = openat(curFd, segBytes, ORdonly);
                Assert.True(nextFd >= 0);
                createdReversed.Add((curFd, segBytes, true));
                openFds.Add(nextFd);
                curFd = nextFd;
            }

            // Add three leaf sibling dirs and files beyond PATH_MAX using non-variadic linkat (safe on ARM64 macOS).
            byte[] tmplName = [.. Encoding.UTF8.GetBytes("tmpl.bin"), 0];
            for (int b = 0; b < 3; b++)
            {
                byte[] subSeg = [.. Encoding.UTF8.GetBytes($"branch_{b}"), 0];
                Assert.Equal(0, mkdirat(curFd, subSeg, 0b111_101_101));
                int branchFd = openat(curFd, subSeg, ORdonly);
                Assert.True(branchFd >= 0);
                byte[] leafFile = [.. Encoding.UTF8.GetBytes("leaf.bin"), 0];
                Assert.Equal(0, linkat(rootFd, tmplName, branchFd, leafFile, 0));
                createdReversed.Add((branchFd, leafFile, false));
                createdReversed.Add((curFd, subSeg, true));
                openFds.Add(branchFd);
            }

            CapturingResult seq = RunSequential(["-B1", "-s", tree.Root]);
            CapturingResult par1 = RunParallel(["-B1", "-s", tree.Root], workerBudgetOverride: 1);
            CapturingResult par4 = RunParallel(["-B1", "-s", tree.Root], workerBudgetOverride: 4);

            Assert.Equal(0, seq.ExitCode);
            Assert.Equal(seq.Stdout, par1.Stdout);
            Assert.Equal(seq.Stdout, par4.Stdout);
            Assert.Equal(seq.Stderr, par4.Stderr);

            CapturingResult seqAll = RunSequential(["-B1", "-a", tree.Root]);
            CapturingResult parAll = RunParallel(["-B1", "-a", tree.Root], workerBudgetOverride: 4);
            Assert.Equal(seqAll.ExitCode, parAll.ExitCode);
            Assert.Equal(seqAll.Stdout, parAll.Stdout);
        }
        finally
        {
            for (int i = createdReversed.Count - 1; i >= 0; i--)
            {
                (int pfd, byte[] name, bool isDir) = createdReversed[i];
                _ = unlinkat(pfd, name, isDir ? atRemovedir : 0);
            }

            foreach (int fd in openFds)
                _ = close(fd);
        }
    }

    [UnixFact]
    public void ParallelWalker_BackpressureAndStreamingEarlyCancel_BehavesCleanly()
    {
        using TempTree tree = new();
        // 80 dirs x 900 files = 72,000 entries > BackpressureHighWatermark (65,536) with -a.
        for (int d = 0; d < 80; d++)
        {
            string dir = tree.Dir($"d_{d:D2}");
            for (int f = 0; f < 900; f++)
            {
                using FileStream fs = File.Create(Path.Combine(dir, $"f_{f:D3}"));
            }
        }

        // First test early cancellation after >= 4096 bytes written (~100+ lines).
        using CancellationTokenSource cts = new();
        using EarlyCancelStream earlyStream = new(4096, cts);
        using MemoryStream err = new();
        Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["-B1", "-a", tree.Root], "du", "1.0", Stream.Null, earlyStream, err, out DuOptions? opts));
        ParallelDuWalker earlyWalker = new(opts!, "du", Stream.Null, earlyStream, err, workerBudgetOverride: 4);
        Assert.ThrowsAny<OperationCanceledException>(() => earlyWalker.Run([Encoding.UTF8.GetBytes(tree.Root)], cts.Token));
        Assert.True(earlyStream.BytesWritten >= 4096);

        // Next verify slow-consumer backpressure pauses excess worker scanning and matches SequentialWalker.
        using ThrottledStream throttledOut = new();
        using MemoryStream throttledErr = new();
        Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["-B1", "-a", tree.Root], "du", "1.0", Stream.Null, throttledOut, throttledErr, out DuOptions? bpOpts));
        ParallelDuWalker bpWalker = new(bpOpts!, "du", Stream.Null, throttledOut, throttledErr, workerBudgetOverride: 4);
        int rc = bpWalker.Run();
        Assert.Equal(0, rc);
        Assert.Equal(72_081, throttledOut.LineCount);
        Assert.True(bpWalker.PeakUnprintedEntries <= 80_000, $"Peak unprinted {bpWalker.PeakUnprintedEntries} exceeded bounded ceiling");
    }

    [UnixFact]
    public void RawUnixFdStream_DupedDescriptorsWriteSequentiallyWithoutOverwriting()
    {
        using TempTree tree = new();
        string outPath = Path.Combine(tree.Root, "combined.txt");
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(outPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        int fd1 = handle.DangerousGetHandle().ToInt32();
        int fd2 = dup(fd1);
        Assert.True(fd2 >= 0);

        try
        {
            using var s1 = new hyperdu.Core.Native.RawUnixFdStream(fd1, writable: true);
            using var s2 = new hyperdu.Core.Native.RawUnixFdStream(fd2, writable: true);
            s1.Write("first-"u8);
            s2.Write("second-"u8);
            s1.Write("third\n"u8);
        }
        finally
        {
            _ = close(fd2);
        }

        Assert.Equal("first-second-third\n", File.ReadAllText(outPath));
    }

    private sealed class EarlyCancelStream : Stream
    {
        private readonly long _threshold;
        private readonly CancellationTokenSource _cts;
        public long BytesWritten { get; private set; }

        public EarlyCancelStream(long threshold, CancellationTokenSource cts)
        {
            _threshold = threshold;
            _cts = cts;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            if (BytesWritten >= _threshold)
                _cts.Cancel();
        }
    }

    private sealed class ThrottledStream : Stream
    {
        private int _writes;
        public int LineCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (buffer[offset + i] == (byte)'\n')
                    LineCount++;
            }

            if ((++_writes & 7) == 0)
                Thread.Sleep(1);
        }
    }
}
