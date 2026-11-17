using System.Diagnostics;
using System.Text;
using hyperdu.Cli;
using hyperdu.Cli.UI;
using hyperdu.Core.Du;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Tests;

public sealed class TuiCliPhase62Tests
{
    [Fact]
    public void ParseArguments_SupportsAlignedDuFlagsAndPrecedence()
    {
        (DuParseOutcome rc, Program.TuiCliOptions? opts, string stdout, string stderr) = Parse(
            ["-j", "6", "--apparent-size", "-x", "-L", "-l", "-t", "+10K", "--si", "--exclude=*.tmp", "--skip-hidden", "/tmp/work"]);

        Assert.Equal(DuParseOutcome.Run, rc);
        Assert.NotNull(opts);
        Assert.Empty(stdout);
        Assert.Empty(stderr);
        Assert.Equal(6, opts.CustomThreads);
        Assert.True(opts.ApparentSize);
        Assert.True(opts.OneFileSystem);
        Assert.True(opts.FollowSymlinks);
        Assert.True(opts.CountLinks);
        Assert.True(opts.SkipHidden);
        Assert.Equal(new ThresholdSpec(false, 10 * 1024), opts.Threshold);
        Assert.Equal(BlockSizeSpec.HumanSi, opts.DisplayBlockSize);
        Assert.True(opts.HasExplicitHumanUnit);
        Assert.True(opts.Excludes.IsExcluded("/a/b/file.tmp"u8));
        Assert.Equal("/tmp/work", opts.TargetPath);

        // -b sets apparent size + byte unit; -h after -b keeps apparent size and restores binary human formatting.
        (DuParseOutcome rcB, Program.TuiCliOptions? optsB, _, _) = Parse(["-b", "/tmp"]);
        Assert.Equal(DuParseOutcome.Run, rcB);
        Assert.True(optsB!.ApparentSize);
        Assert.Equal(BlockSizeSpec.Bytes, optsB.DisplayBlockSize);
        Assert.False(optsB.HasExplicitHumanUnit);

        (DuParseOutcome rcBh, Program.TuiCliOptions? optsBh, _, _) = Parse(["-b", "-h", "/tmp"]);
        Assert.Equal(DuParseOutcome.Run, rcBh);
        Assert.True(optsBh!.ApparentSize);
        Assert.Equal(BlockSizeSpec.HumanBinary, optsBh.DisplayBlockSize);
        Assert.True(optsBh.HasExplicitHumanUnit);

        // -h before -b switches display back to exact bytes.
        (DuParseOutcome rcHb, Program.TuiCliOptions? optsHb, _, _) = Parse(["-h", "-b", "/tmp"]);
        Assert.Equal(DuParseOutcome.Run, rcHb);
        Assert.True(optsHb!.ApparentSize);
        Assert.Equal(BlockSizeSpec.Bytes, optsHb.DisplayBlockSize);
        Assert.False(optsHb.HasExplicitHumanUnit);

        // Clustered -lxj4 applies all three flags cleanly.
        (DuParseOutcome rcCluster, Program.TuiCliOptions? optsCluster, _, _) = Parse(["-lxj4", "."]);
        Assert.Equal(DuParseOutcome.Run, rcCluster);
        Assert.False(optsCluster!.FollowSymlinks);
        Assert.True(optsCluster.CountLinks);
        Assert.True(optsCluster.OneFileSystem);
        Assert.Equal(4, optsCluster.CustomThreads);
    }

    [Fact]
    public void ParseArguments_VersionAndHelp_EmitAlignedOutput()
    {
        (DuParseOutcome rcShortV, _, string outShortV, string errShortV) = Parse(["-V"]);
        Assert.Equal(DuParseOutcome.ExitSuccess, rcShortV);
        Assert.Equal("hyperdu 1.2.3\n", outShortV);
        Assert.Empty(errShortV);

        (DuParseOutcome rcLongV, _, string outLongV, _) = Parse(["--version"]);
        Assert.Equal(DuParseOutcome.ExitSuccess, rcLongV);
        Assert.Equal("hyperdu 1.2.3\n", outLongV);

        (DuParseOutcome rcHelp, _, string outHelp, string errHelp) = Parse(["--help"]);
        Assert.Equal(DuParseOutcome.ExitSuccess, rcHelp);
        Assert.Empty(errHelp);
        Assert.Contains("-j, --threads", outHelp);
        Assert.Contains("--apparent-size", outHelp);
        Assert.Contains("-t, --threshold", outHelp);
        Assert.Contains("--no-ui", outHelp);
        Assert.Contains("--exclude=PATTERN", outHelp);
        Assert.Contains("-X, --exclude-from=FILE", outHelp);
        Assert.Contains("-V, --version", outHelp);
        Assert.Contains("-t <N> -> -j <N>", outHelp);
    }

    [Theory]
    [InlineData(new[] { "-v" }, "-V")]
    [InlineData(new[] { "-e", "node_modules" }, "--exclude")]
    [InlineData(new[] { "-e" }, "-X FILE")]
    [InlineData(new[] { "-enode_modules" }, "--exclude")]
    [InlineData(new[] { "-xefoo" }, "--exclude")]
    [InlineData(new[] { "-t", "bad" }, "-j <N>")]
    [InlineData(new[] { "-t", "-0" }, "--threads")]
    [InlineData(new[] { "-t" }, "-j <N>")]
    [InlineData(new[] { "-xt" }, "-j <N>")]
    public void ParseArguments_LegacyAndInvalidFlags_EmitMigrationDiagnostics(string[] args, string expectedHint)
    {
        (DuParseOutcome rc, Program.TuiCliOptions? opts, _, string stderr) = Parse(args);

        Assert.Equal(DuParseOutcome.ExitFailure, rc);
        Assert.Null(opts);
        Assert.Contains(expectedHint, stderr);
    }

    [Fact]
    public void ParseArguments_LongThresholdAndUnrelatedErrors_DoNotEmitSpuriousLegacyHints()
    {
        (DuParseOutcome rcLongT, _, _, string errLongT) = Parse(["--threshold=bad"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcLongT);
        Assert.Contains("invalid --threshold", errLongT);
        Assert.DoesNotContain("-j <N>", errLongT);

        (DuParseOutcome rcMasked, _, _, string errMasked) = Parse(["--exclude", "-tmp", "--unknown-flag"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcMasked);
        Assert.DoesNotContain("-j <N>", errMasked);
        Assert.DoesNotContain("-e", errMasked);
    }

    [Fact]
    public void ParseArguments_InvalidThreadsOrExtraOperand_ReturnsExitFailure()
    {
        (DuParseOutcome rcZero, _, _, string errZero) = Parse(["-j", "0"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcZero);
        Assert.Contains("invalid -j", errZero);

        (DuParseOutcome rcWord, _, _, string errWord) = Parse(["--threads=abc"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcWord);
        Assert.Contains("invalid --threads", errWord);

        (DuParseOutcome rcExtra, _, _, string errExtra) = Parse(["/first", "/second"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcExtra);
        Assert.Contains("extra operand", errExtra);
    }

    [Fact]
    public void ParseArguments_ExcludeFromStdinAndMissingFile_BehavesCleanly()
    {
        (DuParseOutcome rcOk, Program.TuiCliOptions? opts, _, string errOk) =
            Parse(["-X", "-", "."], stdinText: "*.log\nnode_modules\n");

        Assert.Equal(DuParseOutcome.Run, rcOk);
        Assert.Empty(errOk);
        Assert.True(opts!.Excludes.IsExcluded("sub/debug.log"u8));
        Assert.True(opts.Excludes.IsExcluded("proj/node_modules"u8));
        Assert.False(opts.Excludes.IsExcluded("proj/src"u8));

        (DuParseOutcome rcMissing, _, _, string errMissing) = Parse(["--exclude-from", "/no/such/exclude-file"]);
        Assert.Equal(DuParseOutcome.ExitFailure, rcMissing);
        Assert.Contains("/no/such/exclude-file", errMissing);
    }

    [Fact]
    public async Task ExcludeMatcher_GlobParity_WorksInNativeManagedListingAndHeadlessCli()
    {
        using TempTree tree = new();
        tree.File("keep.bin", 4_096);
        tree.File("ignore.tmp", 8_192);
        tree.File("sub/also.tmp", 8_192);
        tree.File("node_modules/pkg.bin", 16_384);
        tree.File("sub/node_modules/nested.bin", 16_384);

        string excludeFile = Path.Combine(tree.Root, ".patterns");
        File.WriteAllText(excludeFile, "*.tmp\nnode_modules\n.patterns\n");

        foreach (bool useNative in OperatingSystem.IsWindows() ? new[] { false } : new[] { true, false })
        {
            ScanOptions scanOpts = new()
            {
                WorkerCount = 2,
                UseNativeIo = useNative,
                ApparentSize = true
            };
            scanOpts.ExcludeMatcher.AddPattern("*.tmp");
            scanOpts.ExcludeMatcher.AddPattern("node_modules");
            scanOpts.ExcludeMatcher.AddPattern(".patterns");

            ParallelScanner scanner = new(scanOpts);
            DirectoryNode root = await scanner.ScanAsync(tree.Root);

            Assert.Equal(4_096L, root.TotalSize);
            Assert.Equal(1, scanner.FilesScanned);
            Assert.Equal(["sub"], root.Subdirectories.Select(d => d.Name).ToArray());

            List<(string Name, long Size)> listed = DirectoryListing.ListFiles(root, scanOpts);
            Assert.Equal(new List<(string Name, long Size)> { ("keep.bin", 4_096L) }, listed);
        }

        if (!OperatingSystem.IsWindows())
        {
            (int rcDu, string outDu, string errDu) = RunCli(["gdu", "-b", "-s", "--exclude=*.tmp", "--exclude=node_modules", "--exclude=.patterns", tree.Root], headlessTui: false);
            (int rcTui, string outTui, string errTui) = RunCli(["-b", "--exclude=*.tmp", "--exclude=node_modules", "--exclude=.patterns", tree.Root], headlessTui: true);
            Assert.Equal(0, rcDu);
            Assert.Equal(0, rcTui);
            Assert.Empty(errDu);
            Assert.Empty(errTui);
            Assert.Equal(outDu, outTui);

            (int rcTuiX, string outTuiX, string errTuiX) = RunCli(["-b", "-X", excludeFile, tree.Root], headlessTui: true);
            Assert.Equal(0, rcTuiX);
            Assert.Empty(errTuiX);
            Assert.Equal(outDu, outTuiX);

            // Excluding the root operand itself (including '.' when invoked inside tree.Root) produces empty output like du.
            string rootName = Path.GetFileName(tree.Root);
            (int rcRootExcl, string outRootExcl, _) = RunCli(["--exclude", rootName, tree.Root], headlessTui: true);
            Assert.Equal(0, rcRootExcl);
            Assert.Empty(outRootExcl);

            (int rcDotExcl, string outDotExcl, _) = RunCli(["--exclude=.", "."], headlessTui: true, workingDir: tree.Root);
            Assert.Equal(0, rcDotExcl);
            Assert.Empty(outDotExcl);

            // Legacy ExcludedPaths on ScanOptions also filters DirectoryListing.ListFiles.
            ScanOptions legacyPathOpts = new() { UseNativeIo = true, ApparentSize = true, ExcludedPaths = [Path.Combine(tree.Root, "ignore.tmp")] };
            ParallelScanner legacyScanner = new(legacyPathOpts);
            DirectoryNode legacyRoot = await legacyScanner.ScanAsync(tree.Root);
            Assert.DoesNotContain(DirectoryListing.ListFiles(legacyRoot, legacyPathOpts), f => f.Name == "ignore.tmp");
        }
    }

    [Fact]
    public void ExplorerRenderer_FormatSize_HandlesBinarySiBytesAndLargeUnsignedThresholds()
    {
        Assert.Equal("1.00 KB", ExplorerRenderer.FormatSize(1_024, BlockSizeSpec.HumanBinary));
        Assert.Equal("1.00 kB", ExplorerRenderer.FormatSize(1_000, BlockSizeSpec.HumanSi));
        Assert.Equal("1,024 B", ExplorerRenderer.FormatSize(1_024, BlockSizeSpec.Bytes));
        Assert.Equal("8.00 EB", ExplorerRenderer.FormatSize(9223372036854775808UL, BlockSizeSpec.HumanBinary));
        Assert.True(new ThresholdSpec(false, 1_000).Matches(1_000));
        Assert.False(new ThresholdSpec(false, 1_000).Matches(999));
        Assert.True(new ThresholdSpec(true, 1_000).Matches(1_000));
        Assert.False(new ThresholdSpec(true, 1_000).Matches(1_001));
    }

    [UnixFact]
    public void HeadlessTui_HumanSiBytesAndThreshold_MatchDuSemantics()
    {
        using TempTree tree = new();
        byte[] payload = new byte[20_480];
        payload.AsSpan().Fill(0x5A);
        using (FileStream fs = File.Create(Path.Combine(tree.Root, "data.bin")))
        {
            fs.Write(payload);
            fs.Flush(flushToDisk: true);
        }

        (int rcDuH, string outDuH, _) = RunCli(["gdu", "-h", "-s", tree.Root], headlessTui: false);
        (int rcTuiH, string outTuiH, string errTuiH) = RunCli(["-h", tree.Root], headlessTui: true);
        Assert.Equal(0, rcDuH);
        Assert.Equal(0, rcTuiH);
        Assert.Empty(errTuiH);
        Assert.Equal(outDuH, outTuiH);

        (int rcDuSi, string outDuSi, _) = RunCli(["gdu", "--si", "-s", tree.Root], headlessTui: false);
        (int rcTuiSi, string outTuiSi, string errTuiSi) = RunCli(["--si", tree.Root], headlessTui: true);
        Assert.Equal(0, rcDuSi);
        Assert.Equal(0, rcTuiSi);
        Assert.Empty(errTuiSi);
        Assert.Equal(outDuSi, outTuiSi);

        (int rcAbove, string outAbove, _) = RunCli(["-b", "-t", "+100M", tree.Root], headlessTui: true);
        Assert.Equal(0, rcAbove);
        Assert.Empty(outAbove);

        (int rcMatch, string outMatch, _) = RunCli(["-b", "-t", "10K", tree.Root], headlessTui: true);
        Assert.Equal(0, rcMatch);
        Assert.Equal($"20480\t{tree.Root}\n", outMatch);

        (int rcFlagNoUi, string outFlagNoUi, string errFlagNoUi) = RunCli(["--no-ui", "-b", tree.Root], headlessTui: false);
        Assert.Equal(0, rcFlagNoUi);
        Assert.Empty(errFlagNoUi);
        Assert.Equal($"20480\t{tree.Root}\n", outFlagNoUi);
    }

    [Fact]
    public void CliSubprocess_LegacyFlags_ExitNonZeroWithMigrationError()
    {
        (int rcV, string outV, string errV) = RunCli(["-v"], headlessTui: true);
        Assert.Equal(1, rcV);
        Assert.Empty(outV);
        Assert.Contains("-V", errV);

        (int rcE, string outE, string errE) = RunCli(["-e", "node_modules", "."], headlessTui: true);
        Assert.Equal(1, rcE);
        Assert.Empty(outE);
        Assert.Contains("--exclude", errE);
    }

    private static (DuParseOutcome Outcome, Program.TuiCliOptions? Options, string Stdout, string Stderr) Parse(
        string[] args,
        string stdinText = "")
    {
        using MemoryStream stdin = new(Encoding.UTF8.GetBytes(stdinText));
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();

        DuParseOutcome outcome = Program.ParseArguments(args, "hyperdu", "1.2.3", stdin, stdout, stderr, out Program.TuiCliOptions? parsed);
        return (
            outcome,
            parsed,
            Encoding.UTF8.GetString(stdout.ToArray()),
            Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        IReadOnlyList<string> cliArgs,
        bool headlessTui,
        string? workingDir = null)
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
        if (workingDir != null)
            psi.WorkingDirectory = workingDir;
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
}
