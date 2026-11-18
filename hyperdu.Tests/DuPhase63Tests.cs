using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Cli;
using hyperdu.Core.Du;
using hyperdu.Core.Native;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed partial class DuPhase63Tests
{
    private const uint BsdUfNodump = 0x00000001;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chflags(string path, uint flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string existingPath, string newPath);

    private static void WithEnv(Dictionary<string, string?> overrides, Action action)
    {
        Dictionary<string, string?> backup = new();
        foreach (string key in overrides.Keys)
            backup[key] = Environment.GetEnvironmentVariable(key);

        try
        {
            foreach ((string key, string? val) in overrides)
                Environment.SetEnvironmentVariable(key, val);
            action();
        }
        finally
        {
            foreach ((string key, string? val) in backup)
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static (DuParseOutcome Outcome, DuOptions? Options, string Stdout, string Stderr) ParseArgs(
        string[] args,
        string programName = "du",
        string? dialectOverride = "bsd")
    {
        DuParseOutcome outcome = DuParseOutcome.ExitFailure;
        DuOptions? options = null;
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();

        WithEnv(new() { ["HYPERDU_DU_DIALECT"] = dialectOverride }, () =>
        {
            outcome = DuOptions.Parse(args, programName, "1.0.0", stdout, stderr, out options);
        });

        return (
            outcome,
            options,
            Encoding.UTF8.GetString(stdout.ToArray()),
            Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunBsdWalker(
        string[] args,
        string programName = "du",
        bool parallel = true)
    {
        DuParseOutcome outcome = DuParseOutcome.ExitFailure;
        DuOptions? options = null;
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();

        WithEnv(new() { ["HYPERDU_DU_DIALECT"] = "bsd" }, () =>
        {
            outcome = DuOptions.Parse(args, programName, "1.0.0", Stream.Null, stdout, stderr, out options);
        });

        if (outcome == DuParseOutcome.ExitSuccess)
            return (0, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
        if (outcome == DuParseOutcome.ExitUsage)
            return (64, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
        if (outcome != DuParseOutcome.Run || options == null)
            return (1, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));

        int rc = parallel
            ? new ParallelDuWalker(options, programName, Stream.Null, stdout, stderr).Run()
            : new SequentialWalker(options, programName, Stream.Null, stdout, stderr).Run();

        return (rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public void ResolveDialect_HonorsEnvOverrideSymlinkAndPlatformDefault()
    {
        WithEnv(new() { ["HYPERDU_DU_DIALECT"] = null }, () =>
        {
            Assert.Equal(DuDialect.Gnu, DuOptions.ResolveDialect("gdu"));
            Assert.Equal(DuDialect.Gnu, DuOptions.ResolveDialect("/opt/homebrew/bin/gdu"));
            Assert.Equal(OperatingSystem.IsMacOS() ? DuDialect.Bsd : DuDialect.Gnu, DuOptions.ResolveDialect("du"));
            Assert.Equal(OperatingSystem.IsMacOS() ? DuDialect.Bsd : DuDialect.Gnu, DuOptions.ResolveDialect("/usr/local/bin/du"));
        });

        WithEnv(new() { ["HYPERDU_DU_DIALECT"] = "bsd" }, () =>
        {
            Assert.Equal(DuDialect.Bsd, DuOptions.ResolveDialect("gdu"));
            Assert.Equal(DuDialect.Bsd, DuOptions.ResolveDialect("du"));
        });

        WithEnv(new() { ["HYPERDU_DU_DIALECT"] = "gnu" }, () =>
        {
            Assert.Equal(DuDialect.Gnu, DuOptions.ResolveDialect("du"));
            Assert.Equal(DuDialect.Gnu, DuOptions.ResolveDialect("gdu"));
        });
    }

    [Fact]
    public void BsdGetopt_StopsAtFirstNonOptionAndRejectsGnuFlags()
    {
        (DuParseOutcome okOutcome, DuOptions? okOpts, _, string okErr) = ParseArgs(["targetDir", "-s"]);
        Assert.Equal(DuParseOutcome.Run, okOutcome);
        Assert.NotNull(okOpts);
        Assert.False(okOpts.Summarize);
        Assert.Equal(["targetDir", "-s"], okOpts.Operands);
        Assert.Equal(string.Empty, okErr);

        WithEnv(new() { ["POSIXLY_CORRECT"] = null }, () =>
        {
            (DuParseOutcome outcome, _, _, string err) = ParseArgs(["-Z"], "/usr/local/bin/du");
            Assert.Equal(DuParseOutcome.ExitUsage, outcome);
            Assert.StartsWith("du: invalid option -- Z\nusage: du ", err, StringComparison.Ordinal);
        });

        WithEnv(new() { ["POSIXLY_CORRECT"] = "1" }, () =>
        {
            (DuParseOutcome outcome, _, _, string err) = ParseArgs(["-Z"], "/usr/local/bin/du");
            Assert.Equal(DuParseOutcome.ExitUsage, outcome);
            Assert.StartsWith("du: illegal option -- Z\nusage: du ", err, StringComparison.Ordinal);
        });

        (DuParseOutcome longVerOutcome, _, _, string longVerErr) = ParseArgs(["--version"]);
        Assert.Equal(DuParseOutcome.ExitUsage, longVerOutcome);
        Assert.Contains("unrecognized option `--version'", longVerErr, StringComparison.Ordinal);
        Assert.Contains("usage: du ", longVerErr, StringComparison.Ordinal);

        (DuParseOutcome missingArgOutcome, _, _, string missingArgErr) = ParseArgs(["-d"]);
        Assert.Equal(DuParseOutcome.ExitUsage, missingArgOutcome);
        Assert.StartsWith("du: option requires an argument -- d\nusage: du ", missingArgErr, StringComparison.Ordinal);
    }

    [Fact]
    public void BsdOptions_ParsesFlagsAndRejectsConflicts()
    {
        (DuParseOutcome aPlusS, _, _, string aPlusSErr) = ParseArgs(["-a", "-s", "dir"]);
        Assert.Equal(DuParseOutcome.ExitUsage, aPlusS);
        Assert.Equal(DuOptions.BsdUsageText, aPlusSErr);

        (DuParseOutcome sPlusD0, _, _, string sPlusD0Err) = ParseArgs(["-s", "-d", "0", "dir"]);
        Assert.Equal(DuParseOutcome.ExitUsage, sPlusD0);
        Assert.Equal(DuOptions.BsdUsageText, sPlusD0Err);

        (DuParseOutcome badD, _, _, string badDErr) = ParseArgs(["-d", "-1", "dir"]);
        Assert.Equal(DuParseOutcome.ExitUsage, badD);
        Assert.Contains("du: invalid argument to option d: -1", badDErr, StringComparison.Ordinal);

        (DuParseOutcome badB, _, _, string badBErr) = ParseArgs(["-B", "0", "dir"]);
        Assert.Equal(DuParseOutcome.ExitUsage, badB);
        Assert.Contains("du: invalid argument to option B: 0", badBErr, StringComparison.Ordinal);

        (DuParseOutcome badT, _, _, string badTErr) = ParseArgs(["-t", "0", "dir"]);
        Assert.Equal(DuParseOutcome.ExitUsage, badT);
        Assert.Contains("du: invalid threshold: 0", badTErr, StringComparison.Ordinal);

        (DuParseOutcome gOutcome, DuOptions? gOpts, _, _) = ParseArgs(["-A", "-g", "-n", "-r", "-l", "-I", "*.tmp", "root"]);
        Assert.Equal(DuParseOutcome.Run, gOutcome);
        Assert.NotNull(gOpts);
        Assert.True(gOpts.ApparentSize);
        Assert.True(gOpts.IgnoreNoDump);
        Assert.True(gOpts.CountLinks);
        Assert.Equal(1073741824UL, gOpts.BlockSizeSpec.Scale);
        Assert.True(gOpts.Excludes.MatchBasenameOnly);
    }

    [Fact]
    public void BsdBlockSize_HonorsBlocksizeEnvWithClampingAndFlagPrecedence()
    {
        WithEnv(new() { ["BLOCKSIZE"] = "2K", ["DU_BLOCK_SIZE"] = "1M", ["BLOCK_SIZE"] = "1M" }, () =>
        {
            (DuParseOutcome outcome, DuOptions? opts, _, string err) = ParseArgs(["dir"]);
            Assert.Equal(DuParseOutcome.Run, outcome);
            Assert.Equal(2048UL, opts!.BlockSizeSpec.Scale);
            Assert.Equal(string.Empty, err);

            (DuParseOutcome kOutcome, DuOptions? kOpts, _, _) = ParseArgs(["-k", "dir"]);
            Assert.Equal(DuParseOutcome.Run, kOutcome);
            Assert.Equal(1024UL, kOpts!.BlockSizeSpec.Scale);
        });

        WithEnv(new() { ["BLOCKSIZE"] = "256" }, () =>
        {
            (DuParseOutcome outcome, DuOptions? opts, _, string err) = ParseArgs(["dir"]);
            Assert.Equal(DuParseOutcome.Run, outcome);
            Assert.Equal(512UL, opts!.BlockSizeSpec.Scale);
            Assert.Contains("du: minimum blocksize is 512", err, StringComparison.Ordinal);
        });

        WithEnv(new() { ["BLOCKSIZE"] = "   " }, () =>
        {
            (DuParseOutcome outcome, DuOptions? opts, _, string err) = ParseArgs(["dir"]);
            Assert.Equal(DuParseOutcome.Run, outcome);
            Assert.Equal(512UL, opts!.BlockSizeSpec.Scale);
            Assert.Contains("du: minimum blocksize is 512", err, StringComparison.Ordinal);
        });

        WithEnv(new() { ["BLOCKSIZE"] = "2G" }, () =>
        {
            (DuParseOutcome outcome, DuOptions? opts, _, string err) = ParseArgs(["dir"]);
            Assert.Equal(DuParseOutcome.Run, outcome);
            Assert.Equal(1073741824UL, opts!.BlockSizeSpec.Scale);
            Assert.Contains("du: maximum blocksize is 1G", err, StringComparison.Ordinal);
        });

        WithEnv(new() { ["BLOCKSIZE"] = "bogus" }, () =>
        {
            (DuParseOutcome outcome, DuOptions? opts, _, string err) = ParseArgs(["dir"]);
            Assert.Equal(DuParseOutcome.Run, outcome);
            Assert.Equal(512UL, opts!.BlockSizeSpec.Scale);
            Assert.Contains("du: bogus: unknown blocksize", err, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0UL, false, "  0B")]
    [InlineData(512UL, false, "512B")]
    [InlineData(999UL, false, "999B")]
    [InlineData(1000UL, false, "1.0K")]
    [InlineData(1024UL, false, "1.0K")]
    [InlineData(1536UL, false, "1.5K")]
    [InlineData(4096UL, false, "4.0K")]
    [InlineData(9984UL, false, "9.8K")]
    [InlineData(10200UL, false, " 10K")]
    [InlineData(1048576UL, false, "1.0M")]
    [InlineData(999UL, true, "999B")]
    [InlineData(1000UL, true, "1.0k")]
    [InlineData(9949UL, true, "9.9k")]
    [InlineData(9950UL, true, " 10k")]
    public void BsdHumanizeNumber_MatchesFreeBsdFormattingAndPadding(ulong bytes, bool si, string expected)
    {
        BlockSizeSpec spec = si ? BlockSizeSpec.BsdHumanSi : BlockSizeSpec.BsdHumanBinary;
        Assert.Equal(expected, HumanFormat.Format(bytes, spec));
    }

    [UnixFact]
    public void BsdWalker_UnitsApparentSizeIgnoreMaskDepthAndErrorsMatchBsdSemantics()
    {
        using TempTree tree = new();
        tree.File("sub/keep.bin", 513);
        tree.File("sub/drop.tmp", 4096);
        tree.File("skip_me/nested.bin", 8192);
        string emptyFile = tree.File("empty.bin", 0);

        foreach (bool parallel in new[] { false, true })
        {
            (int rcA, string outA, string errA) = RunBsdWalker(["-A", "-a", "-I", "*.tmp", "-I", "skip_*", tree.Root], parallel: parallel);
            Assert.Equal(0, rcA);
            Assert.Equal(string.Empty, errA);
            // -A counts 512-byte apparent blocks by default: 513 bytes -> 2 blocks, empty.bin -> 0 blocks.
            Assert.Contains($"2\t{Path.Combine(tree.Root, "sub", "keep.bin")}\n", outA, StringComparison.Ordinal);
            Assert.Contains($"0\t{emptyFile}\n", outA, StringComparison.Ordinal);
            Assert.DoesNotContain("drop.tmp", outA, StringComparison.Ordinal);
            Assert.DoesNotContain("skip_me", outA, StringComparison.Ordinal);

            (int rcAB1, string outAB1, _) = RunBsdWalker(["-A", "-B", "1", "-h", "-a", Path.Combine(tree.Root, "sub", "keep.bin"), emptyFile], parallel: parallel);
            Assert.Equal(0, rcAB1);
            Assert.Equal($"513B\t{Path.Combine(tree.Root, "sub", "keep.bin")}\n  0B\t{emptyFile}\n", outAB1);

            (int rcG, string outG, _) = RunBsdWalker(["-A", "-g", emptyFile, Path.Combine(tree.Root, "sub", "keep.bin")], parallel: parallel);
            Assert.Equal(0, rcG);
            Assert.Equal($"0\t{emptyFile}\n1\t{Path.Combine(tree.Root, "sub", "keep.bin")}\n", outG);

            // -c uses lowercase total, and duplicate directory operands are emitted twice in BSD mode.
            (int rcDup, string outDup, _) = RunBsdWalker(["-d", "0", "-c", tree.Dir("sub"), tree.Dir("sub")], parallel: parallel);
            Assert.Equal(0, rcDup);
            string[] dupLines = outDup.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, dupLines.Length);
            Assert.Equal(dupLines[0], dupLines[1]);
            Assert.EndsWith("\ttotal", dupLines[2], StringComparison.Ordinal);
        }

        string missing = Path.Combine(tree.Root, "does_not_exist");
        (int rcErr, string outErr, string errErr) = RunBsdWalker([missing], "/tmp/custom/du");
        Assert.Equal(1, rcErr);
        Assert.Equal(string.Empty, outErr);
        Assert.StartsWith($"du: {missing}: ", errErr, StringComparison.Ordinal);
    }

    [UnixFact]
    public void BsdWalker_UnreadableDirectoryOmitsSizeLineAndHardlinksDeduplicateOnlyNlinkGt1()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        using TempTree tree = new();
        string hl1 = tree.File("hl1.bin", 4096);
        string hl2 = Path.Combine(tree.Root, "hl2.bin");
        Assert.Equal(0, link(hl1, hl2));

        string locked = tree.Dir("locked");
        tree.File("locked/secret.bin", 4096);
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            (int rc, string stdout, string stderr) = RunBsdWalker(["-A", "-B", "1", "-k", tree.Root]);
            Assert.Equal(1, rc);
            Assert.DoesNotContain(locked, stdout, StringComparison.Ordinal);
            Assert.StartsWith($"du: {locked}: ", stderr, StringComparison.Ordinal);

            // -A without -k/-m/-g displays 512-byte blocks (4096 / 512 = 8); hardlink deduplication keeps count at 8.
            (int rcHlNoL, string outHlNoL, _) = RunBsdWalker(["-A", "-B", "1", "-c", hl1, hl2]);
            Assert.Equal(0, rcHlNoL);
            Assert.Equal($"8\t{hl1}\n8\ttotal\n", outHlNoL);

            (int rcHlWithL, string outHlWithL, _) = RunBsdWalker(["-A", "-B", "1", "-l", "-c", hl1, hl2]);
            Assert.Equal(0, rcHlWithL);
            Assert.Equal($"8\t{hl1}\n8\t{hl2}\n16\ttotal\n", outHlWithL);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [MacFact("UF_NODUMP flag filtering requires Darwin st_flags.")]
    public void MacOs_BsdNoDumpFlag_SkipsMarkedFilesAndDirectoriesAgainstSystemDu()
    {
        using TempTree tree = new();
        string normal = tree.File("keep.bin", 4096);
        string markedFile = tree.File("skip_file.bin", 4096);
        string markedDir = tree.Dir("skip_dir");
        tree.File("skip_dir/nested.bin", 8192);

        Assert.Equal(0, chflags(markedFile, BsdUfNodump));
        Assert.Equal(0, chflags(markedDir, BsdUfNodump));

        (int rc, string stdout, string stderr) = RunBsdWalker(["-n", "-a", "-A", tree.Root]);
        Assert.Equal(0, rc);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(normal, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("skip_file.bin", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("skip_dir", stdout, StringComparison.Ordinal);

        (int sysRc, string sysOut, string sysErr) = RunExternalProcess("/usr/bin/du", ["-n", "-a", "-A", tree.Root]);
        Assert.Equal(0, sysRc);
        Assert.Equal(string.Empty, sysErr);
        Assert.Equal(sysOut, stdout);
    }

    [MacFact("APFS firmlinks and /System/Volumes/Data device boundaries exist only on macOS.")]
    public void MacOs_ApfsFirmlink_CrossDeviceBoundaryAndDevFdMatchSystemDu()
    {
        Span<byte> rootStatBuf = stackalloc byte[NativeIo.StatBufferSize];
        Span<byte> devStatBuf = stackalloc byte[NativeIo.StatBufferSize];
        Span<byte> dataStatBuf = stackalloc byte[NativeIo.StatBufferSize];
        Span<byte> usersRootBuf = stackalloc byte[NativeIo.StatBufferSize];
        Span<byte> usersDataBuf = stackalloc byte[NativeIo.StatBufferSize];

        Assert.True(NativeIo.TryStatPath("/\0"u8, followSymlinks: false, rootStatBuf, out NativeStat rootStat, out _));
        Assert.True(NativeIo.TryStatPath("/dev\0"u8, followSymlinks: false, devStatBuf, out NativeStat devStat, out _));
        Assert.NotEqual(rootStat.Device, devStat.Device);

        // Split APFS volume-group hosts mount /System/Volumes/Data on a distinct st_dev and firmlink /Users there.
        if (NativeIo.TryStatPath("/System/Volumes/Data\0"u8, followSymlinks: false, dataStatBuf, out NativeStat dataStat, out _) &&
            dataStat.Device != rootStat.Device)
        {
            if (NativeIo.TryStatPath("/Users\0"u8, followSymlinks: false, usersRootBuf, out NativeStat usersRoot, out _) &&
                NativeIo.TryStatPath("/System/Volumes/Data/Users\0"u8, followSymlinks: false, usersDataBuf, out NativeStat usersData, out _))
            {
                Assert.Equal(dataStat.Device, usersRoot.Device);
                Assert.Equal((usersRoot.Device, usersRoot.Inode), (usersData.Device, usersData.Inode));
            }

            (int rcX, string outX, string errX) = RunBsdWalker(["-x", "-d", "1", "/System/Volumes"]);
            (int sysRcX, string sysOutX, string sysErrX) = RunExternalProcess("/usr/bin/du", ["-x", "-d", "1", "/System/Volumes"]);
            Assert.Contains("\t/System/Volumes/Data\n", outX, StringComparison.Ordinal);
            Assert.DoesNotContain("/System/Volumes/Data/Users", outX, StringComparison.Ordinal);
            if (sysRcX == 0)
            {
                Assert.Equal(0, rcX);
                Assert.Equal(sysErrX, errX);
                Assert.Equal(sysOutX, outX);
            }
        }

        // /dev/fd on macOS devfs is automatically skipped to avoid re-entering open fds.
        (int rcDevFd, string outDevFd, _) = RunBsdWalker(["-d", "1", "/dev"]);
        Assert.Equal(0, rcDevFd);
        Assert.DoesNotContain("/dev/fd", outDevFd, StringComparison.Ordinal);

        // Hardlinked files deduplicate via (dev, ino) by default in BSD mode.
        using TempTree tmpTree = new();
        string original = tmpTree.File("a.bin", 4096);
        string hardlink = Path.Combine(tmpTree.Root, "b.bin");
        Assert.Equal(0, link(original, hardlink));

        (int rcDedup, string outDedup, _) = RunBsdWalker(["-A", "-B", "1", "-c", original, hardlink]);
        Assert.Equal(0, rcDedup);
        Assert.Equal($"8\t{original}\n8\ttotal\n", outDedup);
    }

    [UnixFact]
    public void BsdWalker_HardlinkCountdown_DuplicateDirectoryOperandsAndExternalLink()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        string f1 = tree.File("d/a/file1.bin", 4096);
        string hlSub = Path.Combine(d, "a", "hl_sub.bin");
        string hlCross = Path.Combine(tree.Dir("d/c"), "hl_cross.bin");
        Assert.Equal(0, link(f1, hlSub));
        Assert.Equal(0, link(f1, hlCross));

        foreach (bool parallel in new[] { false, true })
        {
            // All 3 links are inside d: linkchk releases the inode after visiting the 3rd link,
            // so a second 'd' operand counts the file again and emits matching totals.
            (int rcSingle, string outSingle, string errSingle) = RunBsdWalker(["-A", "-B", "1", "-s", d], parallel: parallel);
            Assert.Equal(0, rcSingle);
            Assert.Equal(string.Empty, errSingle);
            ulong singleBlocks = ulong.Parse(outSingle.Split('\t')[0]);

            (int rc, string stdout, string stderr) = RunBsdWalker(["-A", "-B", "1", "-s", "-c", d, d], parallel: parallel);
            Assert.Equal(0, rc);
            Assert.Equal(string.Empty, stderr);
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal(lines[0], lines[1]);
            Assert.Equal($"{singleBlocks}\t{d}", lines[0]);
            ulong totalBlocks = ulong.Parse(lines[2].Split('\t')[0]);
            // howmany() ceiling across unrounded directory apparent sizes means total(2*bytes) is either 2*single or 2*single - 1.
            Assert.InRange(totalBlocks, singleBlocks * 2 - 1, singleBlocks * 2);
            Assert.EndsWith("\ttotal", lines[2], StringComparison.Ordinal);
        }

        // With st_nlink == 2 and one link outside d_ext, remaining == 1 after the first 'd_ext',
        // so the second 'd_ext' decrements remaining to 0 and skips the 4096-byte file (8 x 512-byte blocks).
        string dExt = tree.Dir("d_ext");
        string extInside = tree.File("d_ext/only_inside.bin", 4096);
        string extOutside = Path.Combine(tree.Root, "external_hl.bin");
        Assert.Equal(0, link(extInside, extOutside));
        foreach (bool parallel in new[] { false, true })
        {
            (int rcExt, string outExt, string errExt) = RunBsdWalker(["-A", "-B", "1", "-s", "-c", dExt, dExt], parallel: parallel);
            Assert.Equal(0, rcExt);
            Assert.Equal(string.Empty, errExt);
            string[] extLines = outExt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, extLines.Length);
            ulong firstBlocks = ulong.Parse(extLines[0].Split('\t')[0]);
            ulong secondBlocks = ulong.Parse(extLines[1].Split('\t')[0]);
            ulong totalBlocks = ulong.Parse(extLines[2].Split('\t')[0]);
            Assert.Equal(8UL, firstBlocks - secondBlocks);
            // ceiling(2 * dirBytes + fileBytes) / 512 is either firstBlocks + secondBlocks or 1 less.
            Assert.InRange(totalBlocks, firstBlocks + secondBlocks - 1, firstBlocks + secondBlocks);
        }
    }

    [UnixFact]
    public void BsdWalker_SymlinkLoopsAndDirectoryCyclesWithFollow_FailFastLikeAppleDu()
    {
        using TempTree loopTree = new();
        string loopA = Path.Combine(loopTree.Root, "loop_a");
        string loopB = Path.Combine(loopTree.Root, "loop_b");
        File.CreateSymbolicLink(loopA, "loop_b");
        File.CreateSymbolicLink(loopB, "loop_a");

        string d = loopTree.Dir("dir_loop");
        File.CreateSymbolicLink(Path.Combine(d, "la"), "lb");
        File.CreateSymbolicLink(Path.Combine(d, "lb"), "la");
        string okDir = loopTree.Dir("ok_dir");
        loopTree.File("ok_dir/keep.bin", 4096);

        using TempTree cycleTree = new();
        string sub = cycleTree.Dir("sub");
        cycleTree.File("sub/keep.bin", 4096);
        string cycleLink = Path.Combine(sub, "up");
        File.CreateSymbolicLink(cycleLink, cycleTree.Root);

        foreach (bool parallel in new[] { false, true })
        {
            // Operand ELOOP under -H / -L triggers fatal Apple unix2003 diagnostic.
            (int rcOp, string outOp, string errOp) = RunBsdWalker(["-H", "-c", loopA, okDir], "/usr/bin/du", parallel: parallel);
            Assert.Equal(1, rcOp);
            Assert.Equal(string.Empty, outOp);
            Assert.Equal($"du: Too many symlinks at {loopA}\n", errOp);

            // Child ELOOP under -L aborts before continuing to later operands or total.
            (int rcChild, _, string errChild) = RunBsdWalker(["-L", "-c", d, d], "/usr/bin/du", parallel: parallel);
            Assert.Equal(1, rcChild);
            Assert.StartsWith("du: Too many symlinks at ", errChild, StringComparison.Ordinal);

            // Ancestor directory symlink cycle under -L triggers FTS_DC diagnostic.
            (int rcCycle, _, string errCycle) = RunBsdWalker(["-L", cycleTree.Root], "/usr/bin/du", parallel: parallel);
            Assert.Equal(1, rcCycle);
            Assert.Equal($"du: Can't follow symlink cycle from {cycleLink} to {cycleTree.Root}\n", errCycle);
        }
    }

    [UnixFact]
    public void BsdWalker_DeepTreeBeyondPathMax_SucceedsWithoutTruncation()
    {
        // FixtureGenerator creates and deletes chains fd-relatively via openat/mkdirat/unlinkat,
        // avoiding .NET Directory.CreateDirectory's PATH_MAX (1024 on macOS) limit.
        using hyperdu.Benchmarks.Cli.FixtureGenerator generator = new();
        hyperdu.Benchmarks.Cli.FixtureSpec deep = generator.CreateDeepChain(depth: 100);

        foreach (bool parallel in new[] { false, true })
        {
            (int rc, string stdout, string stderr) = RunBsdWalker(["-A", "-B", "1", "-s", deep.RootPath], parallel: parallel);
            Assert.Equal(0, rc);
            Assert.Equal(string.Empty, stderr);
            string[] parts = stdout.TrimEnd('\n').Split('\t');
            Assert.Equal(2, parts.Length);
            Assert.True(ulong.Parse(parts[0]) >= 1UL);
            Assert.Equal(deep.RootPath, parts[1]);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunExternalProcess(string exe, string[] args)
    {
        using System.Diagnostics.Process proc = new();
        proc.StartInfo.FileName = exe;
        foreach (string arg in args)
            proc.StartInfo.ArgumentList.Add(arg);
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;
        proc.StartInfo.UseShellExecute = false;
        proc.Start();
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }
}
