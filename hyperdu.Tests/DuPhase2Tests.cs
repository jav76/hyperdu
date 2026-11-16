using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;
using hyperdu.Core.Native;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed partial class DuPhase2Tests
{
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

    private static (int ExitCode, string Stdout, string Stderr) RunWalker(string[] args, byte[]? stdinBytes = null)
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

        return (rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public void ExcludeMatcher_ImplementsGnuFnmatchAndSuffixRules()
    {
        static bool Match(string pat, string text) =>
            ExcludeMatcher.Fnmatch(Encoding.UTF8.GetBytes(pat), Encoding.UTF8.GetBytes(text));

        Assert.True(Match("*.tmp", "d/tmp_ignored.tmp"));
        Assert.True(Match("d?a/*", "d/a/file1.bin"));
        Assert.True(Match("*", ".hidden"));
        Assert.True(Match("[abc]file", "bfile"));
        Assert.True(Match("[!a-c]file", "dfile"));
        Assert.False(Match("[!a-c]file", "bfile"));
        Assert.True(Match("[^a-c]file", "zfile"));
        Assert.False(Match("[^a-c]file", "afile"));
        Assert.True(Match("[]a]x", "]x"));
        Assert.True(Match(@"a\*b", "a*b"));
        Assert.False(Match(@"a\*b", "axb"));

        ExcludeMatcher matcher = new();
        matcher.AddPattern("");
        matcher.AddPattern("*.tmp");
        matcher.AddPattern("target");
        Assert.False(matcher.IsEmpty);
        Assert.True(matcher.IsExcluded("target"u8));
        Assert.True(matcher.IsExcluded("sub/target"u8));
        Assert.False(matcher.IsExcluded("sub/target/"u8));
        Assert.True(matcher.IsExcluded("a/b/c.tmp"u8));
        Assert.False(matcher.IsExcluded(""u8));
    }

    [UnixFact]
    public void Excludes_FilterOperandsChildrenAndMissingFromFile()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        tree.File("d/keep.txt", 100);
        tree.File("d/skip.tmp", 200);
        string sub = tree.Dir("d/sub");
        tree.File("d/sub/a.log", 300);
        tree.File("d/sub/b.txt", 50);

        string patFile = tree.File("pats.txt", 0);
        File.WriteAllText(patFile, "*.tmp\n\n*.log\n");

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            // Operand itself excluded yields empty output.
            (int rcOp, string outOp, string errOp) = RunWalker(["--exclude=d", d]);
            Assert.Equal(0, rcOp);
            Assert.Empty(errOp);
            Assert.Empty(outOp);

            // Trailing slash on operand is not stripped when checking exclude patterns.
            (int rcSlash, string outSlash, string errSlash) = RunWalker(["--exclude=d", "-b", "-s", d + "/"]);
            Assert.Equal(0, rcSlash);
            Assert.Empty(errSlash);
            Assert.Contains("\t" + d + "/\n", outSlash);

            // -X reads patterns (ignoring blank lines) and sums only non-excluded files.
            (int rcX, string outX, string errX) = RunWalker(["-X", patFile, "-b", "-a", d]);
            Assert.Equal(0, rcX);
            Assert.Empty(errX);
            Assert.DoesNotContain("skip.tmp", outX);
            Assert.DoesNotContain("a.log", outX);
            Assert.Contains($"100\t{Path.Combine(d, "keep.txt")}\n", outX);
            Assert.Contains($"50\t{Path.Combine(sub, "b.txt")}\n", outX);
            Assert.Contains($"150\t{d}\n", outX);

            // -X - reads exclude patterns from stdin.
            (int rcStdinX, string outStdinX, string errStdinX) = RunWalker(
                ["-X", "-", "-b", "-s", d],
                Encoding.UTF8.GetBytes("*.tmp\n*.log\n"));
            Assert.Equal(0, rcStdinX);
            Assert.Empty(errStdinX);
            Assert.Equal($"150\t{d}\n", outStdinX);

            // Missing -X file produces exact POSIX error + Try help.
            string missingPat = Path.Combine(tree.Root, "no_such_pats.txt");
            (int rcMiss, _, string errMiss) = RunWalker(["-X", missingPat, d]);
            Assert.Equal(1, rcMiss);
            Assert.Contains($"du: {missingPat}: No such file or directory\n", errMiss);
            Assert.Contains("Try 'du --help' for more information.\n", errMiss);
        });
    }

    [Fact]
    public void Threshold_ParsesFormatsAndUsesAsciiQuotes()
    {
        Assert.True(BlockSize.TryParseThreshold("0", out ThresholdSpec t0, out _));
        Assert.Equal(new ThresholdSpec(false, 0), t0);

        Assert.True(BlockSize.TryParseThreshold("+4K", out ThresholdSpec tPos, out _));
        Assert.Equal(new ThresholdSpec(false, 4096), tPos);

        Assert.True(BlockSize.TryParseThreshold("-2K", out ThresholdSpec tNeg, out _));
        Assert.Equal(new ThresholdSpec(true, 2048), tNeg);

        Assert.False(BlockSize.TryParseThreshold("-0", out _, out BlockSizeParseStatus sNeg0));
        Assert.Equal(BlockSizeParseStatus.NegativeZeroThreshold, sNeg0);

        Assert.False(BlockSize.TryParseThreshold("-0K", out _, out BlockSizeParseStatus sNeg0K));
        Assert.Equal(BlockSizeParseStatus.NegativeZeroThreshold, sNeg0K);

        Assert.False(BlockSize.TryParseThreshold("1Y", out _, out BlockSizeParseStatus sOver));
        Assert.Equal(BlockSizeParseStatus.Overflow, sOver);

        WithEnv(new() { ["LC_ALL"] = "en_US.UTF-8", ["POSIXLY_CORRECT"] = null }, () =>
        {
            (int rcErr, _, string errText) = RunWalker(["-t", "-0", "."]);
            Assert.Equal(1, rcErr);
            Assert.Equal("du: invalid --threshold argument '-0'\n", errText);

            (int rcSize, _, string errSize) = RunWalker(["-t", "SIZE", "."]);
            Assert.Equal(1, rcSize);
            Assert.Equal("du: invalid -t argument 'SIZE'\n", errSize);

            (int rcOver, _, string errOver) = RunWalker(["--threshold=1Y", "."]);
            Assert.Equal(1, rcOver);
            Assert.Equal("du: --threshold argument '1Y' too large\n", errOver);
        });
    }

    [UnixFact]
    public void Threshold_FiltersOutputWithoutChangingSums()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        tree.File("d/small.bin", 100);
        tree.File("d/big.bin", 500);

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            (int rcPos, string outPos, string errPos) = RunWalker(["-b", "-a", "-c", "-t", "200", d]);
            Assert.Equal(0, rcPos);
            Assert.Empty(errPos);
            string[] linesPos = outPos.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal([
                $"500\t{Path.Combine(d, "big.bin")}",
                $"600\t{d}",
                "600\ttotal"
            ], linesPos);

            (int rcNeg, string outNeg, string errNeg) = RunWalker(["-b", "-a", "-c", "-t", "-200", d]);
            Assert.Equal(0, rcNeg);
            Assert.Empty(errNeg);
            string[] linesNeg = outNeg.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal([
                $"100\t{Path.Combine(d, "small.bin")}",
                "600\ttotal"
            ], linesNeg);
        });
    }

    [UnixFact]
    public void Inodes_CountsEntriesScalesHumanAndWarnsOnApparentSize()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        string f1 = tree.File("d/a.bin", 5000);
        tree.File("d/b.bin", 7000);
        Assert.Equal(0, link(f1, Path.Combine(d, "a_link.bin")));
        tree.Dir("d/sub");

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            // Deduplicated hardlink: d + sub + a.bin + b.bin = 4 inodes. -B 10 is ignored for --inodes.
            (int rc, string stdout, string stderr) = RunWalker(["--inodes", "-B", "10", "-a", d]);
            Assert.Equal(0, rc);
            Assert.Empty(stderr);
            Assert.Contains($"4\t{d}\n", stdout);
            Assert.Contains($"1\t{Path.Combine(d, "sub")}\n", stdout);

            // With -l: d + sub + a.bin + a_link.bin + b.bin = 5 inodes.
            (int rcL, string outL, string errL) = RunWalker(["--inodes", "-l", "-s", d]);
            Assert.Equal(0, rcL);
            Assert.Empty(errL);
            Assert.Equal($"5\t{d}\n", outL);

            // Warning when combined with --apparent-size or -b, still exits 0.
            (int rcWarn, string outWarn, string errWarn) = RunWalker(["--inodes", "-b", "-s", d]);
            Assert.Equal(0, rcWarn);
            Assert.Equal("du: warning: options --apparent-size and -b are ineffective with --inodes\n", errWarn);
            Assert.Equal($"4\t{d}\n", outWarn);

            // Human-readable scaling applies to large inode counts.
            string many = tree.Dir("many");
            for (int i = 0; i < 1024; i++)
                System.IO.File.WriteAllBytes(Path.Combine(many, $"f{i:D4}"), []);

            (int rcH, string outH, _) = RunWalker(["--inodes", "-h", "-s", many]);
            Assert.Equal(0, rcH);
            Assert.Equal($"1.1K\t{many}\n", outH); // 1025 inodes -> ceiling tenths in base 1024

            (int rcSi, string outSi, _) = RunWalker(["--inodes", "--si", "-s", many]);
            Assert.Equal(0, rcSi);
            Assert.Equal($"1.1k\t{many}\n", outSi);

            // --help documents --inodes.
            (int rcHelp, string outHelp, _) = RunWalker(["--help"]);
            Assert.Equal(0, rcHelp);
            Assert.Contains(" --inodes ", outHelp);
        });
    }

    [UnixFact]
    public void Files0From_MatchesGnuCasesAndErrors()
    {
        using TempTree tree = new();
        string root = tree.Dir("w");
        string f1 = tree.File("w/g", 10);
        string subDir = tree.Dir("w/sub");
        tree.File("w/sub/x", 20);
        string listFile = tree.File("w/list.bin", 0);
        string missingInput = Path.Combine(root, "does-not-exist");

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            // Extra operand rejected.
            (int rcExtra, _, string errExtra) = RunWalker(["--files0-from=-", "extra"], "a"u8.ToArray());
            Assert.Equal(1, rcExtra);
            Assert.Equal(
                "du: extra operand 'extra'\nfile operands cannot be combined with --files0-from\nTry 'du --help' for more information.\n",
                errExtra);

            // Missing --files0-from source file.
            (int rcMiss, _, string errMiss) = RunWalker([$"--files0-from={missingInput}"]);
            Assert.Equal(1, rcMiss);
            Assert.Equal($"du: cannot open '{missingInput}' for reading: No such file or directory\n", errMiss);

            // Directory passed as --files0-from yields read error: Is a directory.
            (int rcDir, _, string errDir) = RunWalker([$"--files0-from={subDir}"]);
            Assert.Equal(1, rcDir);
            Assert.Equal($"du: {subDir}: read error: Is a directory\n", errDir);

            // '-' inside stdin rejected.
            (int rcDash, _, string errDash) = RunWalker(["--files0-from=-"], "-"u8.ToArray());
            Assert.Equal(1, rcDash);
            Assert.Equal("du: when reading file names from stdin, no file name of '-' allowed\n", errDash);

            // '-' inside a named file IS allowed as an operand name (treated as file named "-").
            File.WriteAllBytes(listFile, Encoding.UTF8.GetBytes(missingInput + "\0"));
            (int rcList, _, string errList) = RunWalker([$"--files0-from={listFile}"]);
            Assert.Equal(1, rcList);
            Assert.Equal($"du: cannot access '{missingInput}': No such file or directory\n", errList);

            // Empty stdin produces nothing and exits 0.
            (int rcEmpty, string outEmpty, string errEmpty) = RunWalker(["--files0-from=-"], []);
            Assert.Equal(0, rcEmpty);
            Assert.Empty(outEmpty);
            Assert.Empty(errEmpty);

            // Two identical filenames with and without trailing NUL deduplicate.
            byte[] dupPayload = Encoding.UTF8.GetBytes($"{f1}\0{f1}");
            (int rcDup, string outDup, string errDup) = RunWalker(["-b", "--files0-from=-"], dupPayload);
            Assert.Equal(0, rcDup);
            Assert.Empty(errDup);
            Assert.Equal($"10\t{f1}\n", outDup);

            // Zero-length NUL followed by valid path reports -:1 error and still processes valid entry.
            byte[] nulValidPayload = Encoding.UTF8.GetBytes($"\0{f1}\0");
            (int rcZ, string outZ, string errZ) = RunWalker(["-b", "--files0-from=-"], nulValidPayload);
            Assert.Equal(1, rcZ);
            Assert.Equal("du: -:1: invalid zero-length file name\n", errZ);
            Assert.Equal($"10\t{f1}\n", outZ);

            // Multiple NULs report 1-based record numbers.
            (int rcZ2, string outZ2, string errZ2) = RunWalker(["--files0-from=-"], [0, 0]);
            Assert.Equal(1, rcZ2);
            Assert.Empty(outZ2);
            Assert.Equal("du: -:1: invalid zero-length file name\ndu: -:2: invalid zero-length file name\n", errZ2);
        });
    }

    [UnixFact]
    public void SymlinkPolicies_HandleP_H_L_Cycles_Dangling_And_Eloop()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        string sub = tree.Dir("d/real");
        string targetFile = tree.File("d/real/payload.bin", 1234);
        string dirLink = Path.Combine(d, "symdir");
        string fileLink = Path.Combine(d, "symfile");
        string danglingLink = Path.Combine(d, "dangling");
        File.CreateSymbolicLink(dirLink, "real");
        File.CreateSymbolicLink(fileLink, Path.Combine("real", "payload.bin"));
        File.CreateSymbolicLink(danglingLink, "missing_target");

        // Self-cycle under d/real
        string cycleA = tree.Dir("cycle/a");
        string cycleB = tree.Dir("cycle/b");
        File.CreateSymbolicLink(Path.Combine(cycleA, "to_b"), "../b");
        File.CreateSymbolicLink(Path.Combine(cycleB, "to_a"), "../a");

        // Deep chain of relative symlinks triggering kernel ELOOP under -L.
        string chainRoot = tree.Dir("chain");
        for (int i = 1; i <= 50; i++)
            tree.Dir($"chain/{i}");
        for (int i = 1; i < 50; i++)
            File.CreateSymbolicLink(Path.Combine(chainRoot, i.ToString(), "s"), $"../{i + 1}");
        tree.File("chain/50/leaf", 42);

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            // -P (default): symlink operand without trailing slash is not followed; with trailing slash it is.
            (int rcP, string outP, string errP) = RunWalker(["-P", "-b", dirLink]);
            Assert.Equal(0, rcP);
            Assert.Empty(errP);
            Assert.DoesNotContain("1234", outP);

            (int rcH, string outH, string errH) = RunWalker(["-H", "-b", "-a", dirLink]);
            Assert.Equal(0, rcH);
            Assert.Empty(errH);
            Assert.Contains($"1234\t{Path.Combine(dirLink, "payload.bin")}\n", outH);

            // -H on dangling operand emits cannot access without ': No such file or directory'.
            (int rcHd, _, string errHd) = RunWalker(["-H", danglingLink]);
            Assert.Equal(1, rcHd);
            Assert.Equal($"du: cannot access '{danglingLink}'\n", errHd);

            // -L dereferences child links, deduplicates target against real/payload.bin, reports dangling without strerror.
            (int rcL, string outL, string errL) = RunWalker(["-L", "-b", "-a", d]);
            Assert.Equal(1, rcL);
            Assert.Equal($"du: cannot access '{danglingLink}'\n", errL);
            string[] outLinesL = outL.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal($"1234\t{d}", outLinesL[^1]);
            int fileCounted = outLinesL.Count(l => l.StartsWith("1234\t", StringComparison.Ordinal) && l != $"1234\t{d}" && l != $"1234\t{sub}" && l != $"1234\t{dirLink}");
            Assert.Equal(1, fileCounted);

            // Symlink cycle under -L and -lL terminates cleanly with exit 0 and empty stderr.
            (int rcCyc, string outCyc, string errCyc) = RunWalker(["-alL", cycleA]);
            Assert.Equal(0, rcCyc);
            Assert.Empty(errCyc);
            Assert.Contains($"\t{Path.Combine(cycleA, "to_b")}\n", outCyc);
            Assert.Contains($"\t{cycleA}\n", outCyc);

            // Long symlink chain triggers ELOOP just like gnulib fts FTS_LOGICAL.
            string entry1 = Path.Combine(chainRoot, "1");
            (int rcEloop, _, string errEloop) = RunWalker(["-L", entry1]);
            Assert.Equal(1, rcEloop);
            Assert.Contains("Too many levels of symbolic links", errEloop);
        });
    }

    [UnixFact]
    public void OneFileSystem_ResetsRootDevicePerOperandAndSkipsForeignDevices()
    {
        using TempTree tree = new();
        string d1 = tree.Dir("d1");
        tree.File("d1/local.bin", 300);

        if (!NativeIo.TryStatPath(Encoding.UTF8.GetBytes(d1 + "\0"), true, new byte[256], out NativeStat stTmp, out _))
            return;

        // Only exercise the cross-device check if /dev/shm is mounted on a distinct filesystem device.
        const string shmBase = "/dev/shm";
        if (!Directory.Exists(shmBase) ||
            !NativeIo.TryStatPath(Encoding.UTF8.GetBytes(shmBase + "\0"), true, new byte[256], out NativeStat stShm, out _) ||
            stTmp.Device == stShm.Device)
        {
            return;
        }

        string foreignDir = Path.Combine(shmBase, "hyperdu-xdev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreignDir);
        try
        {
            string foreignFile = Path.Combine(foreignDir, "remote.bin");
            File.WriteAllBytes(foreignFile, new byte[777]);
            File.CreateSymbolicLink(Path.Combine(d1, "cross"), foreignDir);

            WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
            {
                // Passing -xL across two operands resets _rootDevice for the second operand (foreignDir).
                (int rc, string stdout, string stderr) = RunWalker(["-x", "-L", "-b", "-s", d1, foreignDir]);
                Assert.Equal(0, rc);
                Assert.Empty(stderr);
                string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal([
                    $"300\t{d1}",
                    $"777\t{foreignDir}"
                ], lines);
            });
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
}
