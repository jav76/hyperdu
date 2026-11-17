using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Core.Du;
using hyperdu.Core.Native;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed unsafe partial class DuPhase3Tests : IDisposable
{
    private readonly string? _prevDialect = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");

    public DuPhase3Tests() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", "gnu");

    public void Dispose() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", _prevDialect);
    [StructLayout(LayoutKind.Sequential)]
    private struct PosixTimespec
    {
        public long TvSec;
        public long TvNsec;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int utimensat(int dirFd, byte* path, PosixTimespec* times, int flags);

    private static void SetTimestamps(string path, long atimeSec, long atimeNsec, long mtimeSec, long mtimeNsec)
    {
        int atFdCwd = OperatingSystem.IsMacOS() ? -2 : -100;
        byte[] pathUtf8 = Encoding.UTF8.GetBytes(path + "\0");
        PosixTimespec* ts = stackalloc PosixTimespec[2];
        ts[0].TvSec = atimeSec;
        ts[0].TvNsec = atimeNsec;
        ts[1].TvSec = mtimeSec;
        ts[1].TvNsec = mtimeNsec;

        fixed (byte* pPath = pathUtf8)
        {
            Assert.Equal(0, utimensat(atFdCwd, pPath, ts, 0));
        }
    }

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
    public void TimeOptions_ParseWordsStylesAbbreviationsEnvAndConflicts()
    {
        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = null }, () =>
        {
            using MemoryStream stdout = new();
            using MemoryStream stderr = new();

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time"], "du", "1.0", stdout, stderr, out DuOptions? o1));
            Assert.Equal(TimeKind.Mtime, o1!.TimeKind);
            Assert.Equal(TimeStyleKind.LongIso, o1.TimeStyleKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time=a"], "du", "1.0", stdout, stderr, out DuOptions? oA));
            Assert.Equal(TimeKind.Atime, oA!.TimeKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time=use"], "du", "1.0", stdout, stderr, out DuOptions? oUse));
            Assert.Equal(TimeKind.Atime, oUse!.TimeKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time=status"], "du", "1.0", stdout, stderr, out DuOptions? oStatus));
            Assert.Equal(TimeKind.Ctime, oStatus!.TimeKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time", "--time-style=f"], "du", "1.0", stdout, stderr, out DuOptions? oFull));
            Assert.Equal(TimeStyleKind.FullIso, oFull!.TimeStyleKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time", "--time-style=i"], "du", "1.0", stdout, stderr, out DuOptions? oIso));
            Assert.Equal(TimeStyleKind.Iso, oIso!.TimeStyleKind);

            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time", "--time-style=+%s.%N"], "du", "1.0", stdout, stderr, out DuOptions? oCustom));
            Assert.Equal(TimeStyleKind.CustomFormat, oCustom!.TimeStyleKind);
            Assert.Equal("%s.%N", oCustom.CustomTimeFormat);

            // --time-style alone (even invalid) does not enable time or fail unless --time is passed.
            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time-style=bogus"], "du", "1.0", stdout, stderr, out DuOptions? oNoTime));
            Assert.False(oNoTime!.ShowTime);

            // --help documents --time, --time=WORD, and --time-style=STYLE.
            Assert.Equal(DuParseOutcome.ExitSuccess, DuOptions.Parse(["--help"], "du", "1.0", stdout, stderr, out _));
            string help = Encoding.UTF8.GetString(stdout.ToArray());
            Assert.Contains(" --time ", help);
            Assert.Contains(" --time=WORD ", help);
            Assert.Contains(" --time-style=STYLE ", help);
        });

        // CLI rejects posix- prefix; TIME_STYLE env strips repeated posix- prefixes and empty string is ambiguous.
        WithEnv(new() { ["LC_ALL"] = "en_US.UTF-8", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = null }, () =>
        {
            (int rcMtime, _, string errMtime) = RunWalker(["--time=mtime", "."]);
            Assert.Equal(1, rcMtime);
            Assert.Equal(
                "du: invalid argument \u2018mtime\u2019 for \u2018--time\u2019\n" +
                "Valid arguments are:\n" +
                "  - \u2018atime\u2019, \u2018access\u2019, \u2018use\u2019\n" +
                "  - \u2018ctime\u2019, \u2018status\u2019\n" +
                "Try 'du --help' for more information.\n",
                errMtime);

            (int rcAmb, _, string errAmb) = RunWalker(["--time=", "."]);
            Assert.Equal(1, rcAmb);
            Assert.Contains("du: ambiguous argument \u2018\u2019 for \u2018--time\u2019\n", errAmb);

            (int rcCliPosix, _, string errCliPosix) = RunWalker(["--time", "--time-style=posix-full-iso", "."]);
            Assert.Equal(1, rcCliPosix);
            Assert.Contains("du: invalid argument \u2018posix-full-iso\u2019 for \u2018time style\u2019\n", errCliPosix);
        });

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = "posix-posix-full-iso" }, () =>
        {
            using MemoryStream stdout = new();
            using MemoryStream stderr = new();
            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time"], "du", "1.0", stdout, stderr, out DuOptions? oEnv));
            Assert.Equal(TimeStyleKind.FullIso, oEnv!.TimeStyleKind);

            // CLI --time-style overrides TIME_STYLE env var even if env is invalid.
            Environment.SetEnvironmentVariable("TIME_STYLE", "invalid");
            Assert.Equal(DuParseOutcome.Run, DuOptions.Parse(["--time", "--time-style=iso"], "du", "1.0", stdout, stderr, out DuOptions? oCli));
            Assert.Equal(TimeStyleKind.Iso, oCli!.TimeStyleKind);

            // Empty TIME_STYLE fails as ambiguous when --time is passed.
            Environment.SetEnvironmentVariable("TIME_STYLE", string.Empty);
            Assert.Equal(DuParseOutcome.ExitFailure, DuOptions.Parse(["--time"], "du", "1.0", stdout, stderr, out _));
            Assert.Contains("du: ambiguous argument '' for 'time style'\n", Encoding.UTF8.GetString(stderr.ToArray()));
        });
    }

    [Fact]
    public void HumanFormat_SupportsCustomSeparatorsMultiByteUtf8AndComplexGrouping()
    {
        Span<byte> buf = stackalloc byte[128];

        NumericLocaleFormat german = new([(byte)','], [(byte)'.'], [3]);
        Assert.True(HumanFormat.TryFormatBytes(4096, BlockSizeSpec.HumanBinary, in german, buf, out int w1));
        Assert.Equal("4,0K", Encoding.UTF8.GetString(buf[..w1]));

        BlockSizeSpec groupedBytes = BlockSizeSpec.Bytes.WithGrouping(true);
        Assert.True(HumanFormat.TryFormatBytes(4096, groupedBytes, in german, buf, out int w2));
        Assert.Equal("4.096", Encoding.UTF8.GetString(buf[..w2]));

        NumericLocaleFormat french = new([(byte)','], [0xE2, 0x80, 0xAF], [3]);
        Assert.True(HumanFormat.TryFormatBytes(1234567, groupedBytes, in french, buf, out int w3));
        Assert.Equal("1\u202f234\u202f567", Encoding.UTF8.GetString(buf[..w3]));

        NumericLocaleFormat indian = new([(byte)'.'], [(byte)','], [3, 2]);
        Assert.True(HumanFormat.TryFormatBytes(12345678, groupedBytes, in indian, buf, out int w4));
        Assert.Equal("1,23,45,678", Encoding.UTF8.GetString(buf[..w4]));

        NumericLocaleFormat stopped = new([(byte)'.'], [(byte)','], [3, 127]);
        Assert.True(HumanFormat.TryFormatBytes(1234567, groupedBytes, in stopped, buf, out int w5));
        Assert.Equal("1234,567", Encoding.UTF8.GetString(buf[..w5]));

        Assert.True(HumanFormat.TryFormatBytes(4096, BlockSizeSpec.HumanBinary, in NumericLocaleFormat.Invariant, buf, out int w6));
        Assert.Equal("4.0K", Encoding.UTF8.GetString(buf[..w6]));

        Assert.True(HumanFormat.TryFormatBytes(1234567, groupedBytes, in NumericLocaleFormat.Invariant, buf, out int w7));
        Assert.Equal("1234567", Encoding.UTF8.GetString(buf[..w7]));
    }

    [UnixFact]
    public void LocalePrecedence_AppliesLcAllOverLcNumericAndLang()
    {
        using TempTree tree = new();
        string f = tree.File("payload.bin", 4096);

        WithEnv(new() { ["LC_ALL"] = null, ["LC_NUMERIC"] = "en_US.UTF-8", ["LANG"] = "C", ["POSIXLY_CORRECT"] = null }, () =>
        {
            (int rcEn, string outEn, string errEn) = RunWalker(["-b", "-B", "'1", f]);
            Assert.Equal(0, rcEn);
            Assert.Empty(errEn);
            Assert.Equal($"4,096\t{f}\n", outEn);

            Environment.SetEnvironmentVariable("LC_ALL", "C");
            (int rcC, string outC, _) = RunWalker(["-b", "-B", "'1", f]);
            Assert.Equal(0, rcC);
            Assert.Equal($"4096\t{f}\n", outC);
        });

        // If de_DE.UTF-8 is installed on the host OS, verify full end-to-end glibc decimal and grouping behavior.
        if (NativeIo.TryGetNumericLocaleStrings("de_DE.UTF-8", out byte[] dec, out byte[] sep, out byte[] grp) &&
            dec.SequenceEqual([(byte)',']))
        {
            WithEnv(new() { ["LC_ALL"] = "de_DE.UTF-8", ["POSIXLY_CORRECT"] = null }, () =>
            {
                Assert.Equal("4,0K", HumanFormat.Format(4096, BlockSizeSpec.HumanBinary));
                if (sep.SequenceEqual([(byte)'.']) && grp.Length > 0 && grp[0] == 3)
                {
                    (int rcDe, string outDe, _) = RunWalker(["-b", "-B", "'1", f]);
                    Assert.Equal(0, rcDe);
                    Assert.Equal($"4.096\t{f}\n", outDe);
                }
            });
        }
    }

    [UnixFact]
    public void TimeFormatting_HandlesIsoStylesNanosecondsDstAndPre1970()
    {
        using TempTree tree = new();
        string summer = tree.File("summer.bin", 10);
        string winter = tree.File("winter.bin", 20);
        string pre1970 = tree.File("pre1970.bin", 30);

        SetTimestamps(summer, 1700000000, 111222333, 1751373296, 987654321); // 2025-07-01 12:34:56.987654321 UTC
        SetTimestamps(winter, 1700000000, 111222333, 1764592496, 123456789); // 2025-12-01 12:34:56.123456789 UTC
        SetTimestamps(pre1970, -1, 900000000, -1, 900000000);                  // 1969-12-31 23:59:59.900000000 UTC

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = null, ["TZ"] = "UTC" }, () =>
        {
            (int rcDef, string outDef, string errDef) = RunWalker(["-b", "--time", summer, winter]);
            Assert.Equal(0, rcDef);
            Assert.Empty(errDef);
            Assert.Equal($"10\t2025-07-01 12:34\t{summer}\n20\t2025-12-01 12:34\t{winter}\n", outDef);

            (int rcIso, string outIso, _) = RunWalker(["-b", "--time", "--time-style=iso", summer]);
            Assert.Equal(0, rcIso);
            Assert.Equal($"10\t2025-07-01\t{summer}\n", outIso);

            (int rcFullUtc, string outFullUtc, _) = RunWalker(["-b", "--time", "--time-style=full-iso", summer, winter]);
            Assert.Equal(0, rcFullUtc);
            Assert.Equal(
                $"10\t2025-07-01 12:34:56.987654321 +0000\t{summer}\n" +
                $"20\t2025-12-01 12:34:56.123456789 +0000\t{winter}\n",
                outFullUtc);

            (int rcCustom, string outCustom, _) = RunWalker(["-b", "--time", "--time-style=+%s.%N|%6N|%3N|%0N|%12N|%%N", summer]);
            Assert.Equal(0, rcCustom);
            Assert.Equal($"10\t1751373296.987654321|987654|987|987654321|987654321000|%N\t{summer}\n", outCustom);

            (int rcEmptyFmt, string outEmptyFmt, _) = RunWalker(["-b", "--time", "--time-style=+", summer]);
            Assert.Equal(0, rcEmptyFmt);
            Assert.Equal($"10\t\t{summer}\n", outEmptyFmt);

            // Pre-1970 entry keeps negative timestamp while -c total starts at epoch (0, 0).
            (int rcPre, string outPre, _) = RunWalker(["-b", "-c", "--time", "--time-style=full-iso", pre1970]);
            Assert.Equal(0, rcPre);
            Assert.Equal(
                $"30\t1969-12-31 23:59:59.900000000 +0000\t{pre1970}\n" +
                "30\t1970-01-01 00:00:00.000000000 +0000\ttotal\n",
                outPre);
        });

        // Verify DST zone switch in the same process run (EDT -0400 in July vs EST -0500 in December).
        WithEnv(new() { ["LC_ALL"] = "en_US.UTF-8", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = null, ["TZ"] = "America/New_York" }, () =>
        {
            (int rcNy, string outNy, string errNy) = RunWalker(["-b", "--time", "--time-style=full-iso", summer, winter]);
            Assert.Equal(0, rcNy);
            Assert.Empty(errNy);
            Assert.Equal(
                $"10\t2025-07-01 08:34:56.987654321 -0400\t{summer}\n" +
                $"20\t2025-12-01 07:34:56.123456789 -0500\t{winter}\n",
                outNy);
        });
    }

    [UnixFact]
    public void TimeAggregation_RespectsSubtreeMaxSeparateDirsExcludesThresholdInodesAndKinds()
    {
        using TempTree tree = new();
        string p = tree.Dir("p");
        string sub = tree.Dir("p/sub");
        string direct = tree.File("p/direct.bin", 10);
        string smallNew = tree.File("p/small.bin", 5);
        string ignoredNew = tree.File("p/ignored.tmp", 15);
        string subFile = tree.File("p/sub/deep.bin", 20);

        // Set distinct atime vs mtime on subFile and smallNew after creation so parent mtime stays older.
        SetTimestamps(direct, 1700000000, 100000000, 1700000000, 100000000);
        SetTimestamps(smallNew, 1710000000, 200000000, 1715000000, 300000000);
        SetTimestamps(subFile, 1720000000, 400000000, 1760000000, 500000000);
        SetTimestamps(ignoredNew, 1790000000, 999999999, 1790000000, 999999999);
        SetTimestamps(sub, 1650000000, 0, 1650000000, 0);
        SetTimestamps(p, 1600000000, 0, 1600000000, 0);

        WithEnv(new() { ["LC_ALL"] = "C", ["POSIXLY_CORRECT"] = null, ["TIME_STYLE"] = null, ["TZ"] = "UTC" }, () =>
        {
            // --exclude skips ignoredNew; -S omits sub's 1760000000 on p but -c total includes full max (1760000000).
            (int rcSep, string outSep, string errSep) = RunWalker(
                ["-b", "-S", "-c", "--exclude=*.tmp", "--time", "--time-style=+%s.%N", p]);
            Assert.Equal(0, rcSep);
            Assert.Empty(errSep);
            Assert.Equal(
                $"20\t1760000000.500000000\t{sub}\n" +
                $"15\t1715000000.300000000\t{p}\n" +
                "35\t1760000000.500000000\ttotal\n",
                outSep);

            // -t filters output only; smallNew (5 < 10) is excluded from listing but still raises p's max timestamp.
            (int rcThr, string outThr, _) = RunWalker(
                ["-b", "-S", "-a", "-t", "10", "--exclude=*.tmp", "--time", "--time-style=+%s", p]);
            Assert.Equal(0, rcThr);
            string[] thrLines = outThr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, thrLines.Length);
            Assert.Equal($"15\t1715000000\t{p}", thrLines[^1]);
            Assert.Contains($"20\t1760000000\t{subFile}", thrLines);
            Assert.Contains($"20\t1760000000\t{sub}", thrLines);
            Assert.Contains($"10\t1700000000\t{direct}", thrLines);

            // --time=atime uses st_atime instead of st_mtime (check file operand directly so readdir doesn't alter dir atime).
            (int rcAtime, string outAtime, _) = RunWalker(
                ["-b", "--time=atime", "--time-style=+%s.%N", smallNew]);
            Assert.Equal(0, rcAtime);
            Assert.Equal($"5\t1710000000.200000000\t{smallNew}\n", outAtime);

            // --time=ctime prints status change timestamp (strictly newer than pre-2025 artificial mtime on direct).
            (int rcCtime, string outCtime, _) = RunWalker(
                ["-b", "--time=ctime", "--time-style=+%s", direct]);
            Assert.Equal(0, rcCtime);
            long parsedCtime = long.Parse(outCtime.Split('\t')[1]);
            Assert.True(parsedCtime > 1700000000L);

            // --inodes combines cleanly with --time.
            (int rcIno, string outIno, _) = RunWalker(
                ["--inodes", "--exclude=*.tmp", "--time", "--time-style=+%s", "-s", p]);
            Assert.Equal(0, rcIno);
            Assert.Equal($"5\t1760000000\t{p}\n", outIno);

            // Empty --files0-from with -c --time emits 1970-01-01 epoch total.
            (int rcEmptyTotal, string outEmptyTotal, _) = RunWalker(["-c", "--time", "--files0-from=-"], []);
            Assert.Equal(0, rcEmptyTotal);
            Assert.Equal("0\t1970-01-01 00:00\ttotal\n", outEmptyTotal);
        });
    }
}
