using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;

namespace hyperdu.Tests;

[CollectionDefinition("DuEnv", DisableParallelization = true)]
public sealed class DuEnvCollection;

[Collection("DuEnv")]
public sealed partial class DuPhase1Tests : IDisposable
{
    private readonly string? _prevDialect = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");

    public DuPhase1Tests() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", "gnu");

    public void Dispose() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", _prevDialect);
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

    [Fact]
    public void GetoptLong_BundlingAttachedLongAbbrevAndDoubleDashWork()
    {
        WithEnv(new() { ["POSIXLY_CORRECT"] = null }, () =>
        {
            LongOption[] longOpts =
            [
                new("all", ArgumentRequirement.NoArgument, 'a'),
                new("apparent-size", ArgumentRequirement.NoArgument, 256),
                new("block-size", ArgumentRequirement.RequiredArgument, 'B'),
                new("max-depth", ArgumentRequirement.RequiredArgument, 'd'),
                new("summarize", ArgumentRequirement.NoArgument, 's'),
                new("total", ArgumentRequirement.NoArgument, 'c'),
                new("threshold", ArgumentRequirement.RequiredArgument, 't'),
                new("time", ArgumentRequirement.OptionalArgument, 261),
                new("time-style", ArgumentRequirement.RequiredArgument, 262)
            ];

            using MemoryStream err = new();
            bool ok = GetoptLong.TryParse(
                ["dirA", "-shc", "-d1", "--max=2", "--ap", "--", "--not-an-opt", "dirB"],
                "achsd:B:t:",
                longOpts,
                "du",
                err,
                out List<ParsedOption> opts,
                out List<string> operands);

            Assert.True(ok);
            Assert.Empty(err.ToArray());
            Assert.Equal(['s', 'h', 'c', 'd', 'd', 256], opts.Select(o => o.Value).ToArray());
            Assert.Equal("1", opts[3].Argument);
            Assert.Equal("2", opts[4].Argument);
            Assert.Equal(["dirA", "--not-an-opt", "dirB"], operands);
        });
    }

    [Fact]
    public void GetoptLong_AmbiguousPrefixReportsExactMatches()
    {
        LongOption[] longOpts =
        [
            new("total", ArgumentRequirement.NoArgument, 'c'),
            new("threshold", ArgumentRequirement.RequiredArgument, 't'),
            new("time", ArgumentRequirement.OptionalArgument, 261),
            new("time-style", ArgumentRequirement.RequiredArgument, 262)
        ];

        using MemoryStream err = new();
        bool ok = GetoptLong.TryParse(["--t", "d"], "ct:", longOpts, "du", err, out _, out _);

        Assert.False(ok);
        string msg = Encoding.UTF8.GetString(err.ToArray());
        Assert.Equal("du: option '--t' is ambiguous; possibilities: '--total' '--threshold' '--time' '--time-style'\n", msg);
    }

    [Fact]
    public void GetoptLong_PosixlyCorrectStopsPermutationAtFirstOperand()
    {
        WithEnv(new() { ["POSIXLY_CORRECT"] = "1" }, () =>
        {
            LongOption[] longOpts = [new("summarize", ArgumentRequirement.NoArgument, 's')];
            using MemoryStream err = new();
            bool ok = GetoptLong.TryParse(["d", "-s"], "s", longOpts, "du", err, out List<ParsedOption> opts, out List<string> operands);

            Assert.True(ok);
            Assert.Empty(opts);
            Assert.Equal(["d", "-s"], operands);
        });
    }

    [Fact]
    public void BlockSize_ParsesScalesSuffixesGroupingAndRejectsInvalidValues()
    {
        Assert.True(BlockSize.TryParse("1024", out BlockSizeSpec s1024, out _));
        Assert.Equal(1024UL, s1024.Scale);
        Assert.Null(s1024.Suffix);

        Assert.True(BlockSize.TryParse("K", out BlockSizeSpec sk, out _));
        Assert.Equal(1024UL, sk.Scale);
        Assert.Equal("K", sk.Suffix);

        Assert.True(BlockSize.TryParse("1K", out BlockSizeSpec s1k, out _));
        Assert.Equal(1024UL, s1k.Scale);
        Assert.Null(s1k.Suffix);

        Assert.True(BlockSize.TryParse("KiB", out BlockSizeSpec skib, out _));
        Assert.Equal(1024UL, skib.Scale);
        Assert.Equal("KiB", skib.Suffix);

        Assert.True(BlockSize.TryParse("KB", out BlockSizeSpec skb, out _));
        Assert.Equal(1000UL, skb.Scale);
        Assert.Equal("kB", skb.Suffix);

        Assert.True(BlockSize.TryParse("kB", out BlockSizeSpec slkb, out _));
        Assert.Equal(1000UL, slkb.Scale);
        Assert.Equal("kB", slkb.Suffix);

        Assert.True(BlockSize.TryParse("MB", out BlockSizeSpec smb, out _));
        Assert.Equal(1_000_000UL, smb.Scale);
        Assert.Equal("MB", smb.Suffix);

        Assert.True(BlockSize.TryParse("MiB", out BlockSizeSpec smib, out _));
        Assert.Equal(1048576UL, smib.Scale);
        Assert.Equal("MiB", smib.Suffix);

        Assert.True(BlockSize.TryParse("human-readable", out BlockSizeSpec shuman, out _));
        Assert.Equal(HumanMode.Binary, shuman.Mode);

        Assert.True(BlockSize.TryParse("si", out BlockSizeSpec ssi, out _));
        Assert.Equal(HumanMode.Si, ssi.Mode);

        Assert.True(BlockSize.TryParse("'1", out BlockSizeSpec sgroup, out _));
        Assert.True(sgroup.Grouping);
        Assert.Equal(1UL, sgroup.Scale);

        Assert.False(BlockSize.TryParse("0", out _, out BlockSizeParseStatus err0));
        Assert.Equal(BlockSizeParseStatus.InvalidArgument, err0);

        Assert.False(BlockSize.TryParse("abc", out _, out BlockSizeParseStatus errAbc));
        Assert.Equal(BlockSizeParseStatus.InvalidArgument, errAbc);

        Assert.False(BlockSize.TryParse("1B", out _, out BlockSizeParseStatus err1b));
        Assert.Equal(BlockSizeParseStatus.InvalidSuffix, err1b);

        Assert.False(BlockSize.TryParse("1Z", out _, out BlockSizeParseStatus err1z));
        Assert.Equal(BlockSizeParseStatus.Overflow, err1z);
    }

    [Fact]
    public void HumanFormat_CeilingAndTenthsBoundariesMatchGnu()
    {
        Assert.Equal("0", HumanFormat.Format(0, BlockSizeSpec.HumanBinary));
        Assert.Equal("1", HumanFormat.Format(1, BlockSizeSpec.HumanBinary));
        Assert.Equal("1023", HumanFormat.Format(1023, BlockSizeSpec.HumanBinary));
        Assert.Equal("1.0K", HumanFormat.Format(1024, BlockSizeSpec.HumanBinary));
        Assert.Equal("1.1K", HumanFormat.Format(1025, BlockSizeSpec.HumanBinary));
        Assert.Equal("9.8K", HumanFormat.Format(9999, BlockSizeSpec.HumanBinary));
        Assert.Equal("10k", HumanFormat.Format(9999, BlockSizeSpec.HumanSi));
        Assert.Equal("10K", HumanFormat.Format(10239, BlockSizeSpec.HumanBinary));
        Assert.Equal("11k", HumanFormat.Format(10239, BlockSizeSpec.HumanSi));
        Assert.Equal("1.0M", HumanFormat.Format(1048575, BlockSizeSpec.HumanBinary));

        Assert.True(BlockSize.TryParse("'1", out BlockSizeSpec groupedSpec, out _));
        WithEnv(new() { ["LC_ALL"] = "en_US.UTF-8" }, () =>
            Assert.Equal("4,096", HumanFormat.Format(4096, groupedSpec)));
        WithEnv(new() { ["LC_ALL"] = "C" }, () =>
            Assert.Equal("4096", HumanFormat.Format(4096, groupedSpec)));
    }

    [Fact]
    public void DuOptions_ConflictsVersionAndUnimplementedFlagsWork()
    {
        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            using (MemoryStream out1 = new())
            using (MemoryStream err1 = new())
            {
                DuParseOutcome res = DuOptions.Parse(["-s", "-a", "."], "du", "1.0.0", out1, err1, out _);
                Assert.Equal(DuParseOutcome.ExitFailure, res);
                string errText = Encoding.UTF8.GetString(err1.ToArray());
                Assert.Contains("du: cannot both summarize and show all entries", errText);
                Assert.Contains("Try 'du --help' for more information.", errText);
            }

            using (MemoryStream out2 = new())
            using (MemoryStream err2 = new())
            {
                DuParseOutcome res = DuOptions.Parse(["-s", "-d", "1", "."], "du", "1.0.0", out2, err2, out _);
                Assert.Equal(DuParseOutcome.ExitFailure, res);
                Assert.Contains("summarizing conflicts with --max-depth=1", Encoding.UTF8.GetString(err2.ToArray()));
            }

            using (MemoryStream out3 = new())
            using (MemoryStream err3 = new())
            {
                DuParseOutcome res = DuOptions.Parse(["-s", "-d", "0", "."], "du", "1.0.0", out3, err3, out DuOptions? opts3);
                Assert.Equal(DuParseOutcome.Run, res);
                Assert.NotNull(opts3);
                Assert.Contains("du: warning: summarizing is the same as using --max-depth=0", Encoding.UTF8.GetString(err3.ToArray()));
            }

            using (MemoryStream out4 = new())
            using (MemoryStream err4 = new())
            {
                DuParseOutcome res = DuOptions.Parse(["--version"], "du", "1.2.3", out4, err4, out _);
                Assert.Equal(DuParseOutcome.ExitSuccess, res);
                string ver = Encoding.UTF8.GetString(out4.ToArray());
                Assert.Equal("du (hyperdu) 1.2.3\n", ver);
                Assert.DoesNotContain("GNU coreutils", ver);
            }

            foreach (string flag in new[] { "--time", "--time-style=full-iso" })
            {
                using MemoryStream outF = new();
                using MemoryStream errF = new();
                DuParseOutcome res = DuOptions.Parse([flag, "."], "du", "1.0.0", outF, errF, out DuOptions? parsedF);
                Assert.Equal(DuParseOutcome.Run, res);
                Assert.NotNull(parsedF);
                Assert.Empty(Encoding.UTF8.GetString(errF.ToArray()));
            }
        });
    }

    [UnixFact]
    public void SequentialWalker_HandlesTrailingSlashDedupeApparentSizeDepthSeparateDirsAndNull()
    {
        using TempTree tree = new();
        string d = tree.Dir("d");
        string f1 = tree.File("d/a/file1.bin", 100);
        string sub = tree.Dir("d/b");
        Assert.Equal(0, link(f1, Path.Combine(sub, "hardlink.bin")));

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            // Trailing slash preserved and triple slash collapsed to single slash, deduplicating second operand.
            (int rcSlash, string outSlash, string errSlash) = RunWalker(["-s", d + "///", d]);
            Assert.Equal(0, rcSlash);
            Assert.Empty(errSlash);
            string[] slashLines = outSlash.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(slashLines);
            Assert.EndsWith("\t" + d + "/", slashLines[0]);

            // Apparent size -b on single file vs -c -S -d -a -0.
            (int rcFile, string outFile, _) = RunWalker(["-b", f1]);
            Assert.Equal(0, rcFile);
            Assert.Equal($"100\t{f1}\n", outFile);

            (int rcAll0, string outAll0, _) = RunWalker(["-0", "-a", d]);
            Assert.Equal(0, rcAll0);
            Assert.EndsWith("\0", outAll0);
            // Note readdir order is filesystem-dependent, so compare against actual file/link inclusion cleanly.
            int linkOrOrigMatches = (outAll0.Contains("file1.bin") ? 1 : 0) + (outAll0.Contains("hardlink.bin") ? 1 : 0);
            Assert.Equal(1, linkOrOrigMatches);

            (int rcSep, string outSep, _) = RunWalker(["-S", "-b", "-c", d]);
            Assert.Equal(0, rcSep);
            string[] sepLines = outSep.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, sepLines.Length);
            Assert.Contains($"\t{Path.Combine(d, "a")}\n", outSep);
            Assert.Contains($"\t{Path.Combine(d, "b")}\n", outSep);
            ulong dVal = ulong.Parse(sepLines[2].Split('\t')[0]);
            ulong totalVal = ulong.Parse(sepLines[3].Split('\t')[0]);
            Assert.Equal(0UL, dVal);
            Assert.Equal(100UL, totalVal);
        });
    }

    [UnixFact]
    public void SequentialWalker_ReportsMissingAndUnreadableErrorsAndStillPrintsD0()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) || Environment.IsPrivilegedProcess)
            return;

        using TempTree tree = new();
        string parent = tree.Dir("parent");
        string secret = tree.Dir("parent/secret");
        tree.File("parent/secret/hidden", 123);
        string missing = Path.Combine(tree.Root, "does-not-exist");

        File.SetUnixFileMode(secret, UnixFileMode.None);
        try
        {
            WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
            {
                (int rcPerm, string outPerm, string errPerm) = RunWalker(["-d", "0", parent]);
                Assert.Equal(1, rcPerm);
                Assert.Contains($"du: cannot read directory '{secret}': Permission denied", errPerm);
                Assert.EndsWith($"\t{parent}\n", outPerm);

                (int rcMissing, string outMissing, string errMissing) = RunWalker([missing]);
                Assert.Equal(1, rcMissing);
                Assert.Empty(outMissing);
                Assert.Equal($"du: cannot access '{missing}': No such file or directory\n", errMissing);
            });
        }
        finally
        {
            File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [UnixFact]
    public void SequentialWalker_GrowsEntryStackAndArenaPastInitialCapacity()
    {
        using TempTree tree = new();
        string wide = tree.Dir("wide");
        for (int i = 0; i < 4200; i++)
            File.WriteAllBytes(Path.Combine(wide, $"entry_item_{i:D4}_payload_padding.dat"), [1, 2, 3]);

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            (int rc, string stdout, string stderr) = RunWalker(["-b", "-a", wide]);
            Assert.Equal(0, rc);
            Assert.Empty(stderr);
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4201, lines.Length);
            Assert.Equal($"12600\t{wide}", lines[^1]);
        });
    }

    private static (int ExitCode, string Stdout, string Stderr) RunWalker(string[] args)
    {
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        DuParseOutcome outcome = DuOptions.Parse(args, "du", "1.0.0", stdout, stderr, out DuOptions? opts);
        int rc = outcome switch
        {
            DuParseOutcome.ExitSuccess => 0,
            DuParseOutcome.ExitFailure => 1,
            DuParseOutcome.Run when opts != null => new SequentialWalker(opts, "du", stdout, stderr).Run(),
            _ => 1
        };

        return (rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }
}
