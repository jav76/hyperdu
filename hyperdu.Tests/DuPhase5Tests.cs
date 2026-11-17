using System.Diagnostics;
using System.Text;
using hyperdu.Cli;
using hyperdu.Core.Du;

namespace hyperdu.Tests;

[Collection("DuEnv")]
public sealed class DuPhase5Tests : IDisposable
{
    private readonly string? _prevDialect = Environment.GetEnvironmentVariable("HYPERDU_DU_DIALECT");

    public DuPhase5Tests() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", "gnu");

    public void Dispose() => Environment.SetEnvironmentVariable("HYPERDU_DU_DIALECT", _prevDialect);
    [Fact]
    public void DuCommand_BrokenPipeOnVersion_Returns141WithoutStderrNoise()
    {
        using MemoryStream stdin = new();
        using MemoryStream stderr = new();
        using BrokenPipeWriteStream brokenStdout = new(throwAfterBytes: 0);

        int versionRc = DuCommand.Run(
            ["--version"],
            "du",
            "1.2.3",
            new DuCommand.StdStreams(stdin, brokenStdout, stderr),
            restoreSigpipe: false);
        Assert.Equal(141, versionRc);
        Assert.Empty(stderr.ToArray());
    }

    [UnixFact]
    public void DuCommand_BrokenPipeDuringTraversal_Returns141WithoutStderrNoise()
    {
        using MemoryStream stdin = new();
        using MemoryStream stderr = new();
        using TempTree tree = new();
        for (int i = 0; i < 16; i++)
            tree.File($"f_{i:D2}.dat", 256);

        using BrokenPipeWriteStream partialStdout = new(throwAfterBytes: 16);
        int walkRc = DuCommand.Run(
            ["-a", tree.Root],
            "du",
            "1.2.3",
            new DuCommand.StdStreams(stdin, partialStdout, stderr),
            restoreSigpipe: false);
        Assert.Equal(141, walkRc);
        Assert.Empty(stderr.ToArray());
    }

    [Fact]
    public void DuOptions_VersionDoesNotImpersonateGnu()
    {
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        DuParseOutcome outcome = DuOptions.Parse(["--version"], "du", "2.0.0", Stream.Null, stdout, stderr, out _);

        Assert.Equal(DuParseOutcome.ExitSuccess, outcome);
        string text = Encoding.UTF8.GetString(stdout.ToArray());
        Assert.Equal("du (hyperdu) 2.0.0\n", text);
        Assert.DoesNotContain("GNU", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stderr.ToArray());
    }

    [UnixFact]
    public void Messages_LocaleAvailabilityHandlesMissingAndPlatformUtf8Locales()
    {
        string? backup = Environment.GetEnvironmentVariable("LC_ALL");
        try
        {
            Environment.SetEnvironmentVariable("LC_ALL", "nonexistent_locale.UTF-8");
            Assert.False(Messages.IsUtf8Locale());
            Assert.Equal("'err1'", Messages.Quote("err1"));

            if (OperatingSystem.IsMacOS())
            {
                Environment.SetEnvironmentVariable("LC_ALL", "UTF-8");
                Assert.True(Messages.IsUtf8Locale());
                Assert.Equal("\u2018err1\u2019", Messages.Quote("err1"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LC_ALL", backup);
        }
    }

    [UnixFact]
    public void DuCommand_SubprocessBashPipefail_Exits141WithNoStderr()
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "hyperdu.dll");
        Assert.True(File.Exists(cliDll), $"Expected built CLI assembly at {cliDll}");

        using TempTree tree = new();
        for (int i = 0; i < 4000; i++)
            tree.File($"f_{i:D4}", 0);

        foreach (string trapPrefix in new[] { "", "trap '' PIPE; " })
        {
            (int rc, _, string stderrText) = RunProcess(
                "bash",
                ["-c", $"{trapPrefix}set -o pipefail; dotnet exec \"$1\" du -a \"$2\" | head -n 1 >/dev/null", "_", cliDll, tree.Root]);

            Assert.Equal(string.Empty, stderrText);
            Assert.Equal(141, rc);
        }
    }

    [UnixFact]
    public void PackageUnixArchiveScript_CreatesRelativeDuAndGduSymlinks()
    {
        string repoRoot = FindRepoRoot();
        string pkgScript = Path.Combine(repoRoot, ".github", "scripts", "package-unix-archive.sh");
        Assert.True(File.Exists(pkgScript), $"Missing packaging script at {pkgScript}");

        using TempTree tmp = new();
        string fakeBin = Path.Combine(tmp.Root, "hyperdu");
        File.WriteAllText(fakeBin, "#!/bin/sh\nexit 0\n");
        string tarPath = Path.Combine(tmp.Root, "hyperdu-test.tar.gz");

        RunChecked("bash", [pkgScript, fakeBin, tarPath, Path.Combine(repoRoot, "LICENSE"), Path.Combine(repoRoot, "README.md")]);

        string extractDir = tmp.Dir("extracted");
        RunChecked("tar", ["-xzf", tarPath, "-C", extractDir]);

        FileInfo hyperduInfo = new(Path.Combine(extractDir, "hyperdu"));
        FileInfo duInfo = new(Path.Combine(extractDir, "du"));
        FileInfo gduInfo = new(Path.Combine(extractDir, "gdu"));

        Assert.True(hyperduInfo.Exists);
        Assert.Equal("hyperdu", duInfo.LinkTarget);
        Assert.Equal("hyperdu", gduInfo.LinkTarget);
        Assert.True(File.Exists(Path.Combine(extractDir, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(extractDir, "README.md")));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "hyperdu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate hyperdu.slnx from test output directory.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(15000), $"Timed out running {fileName}");
        return (proc.ExitCode, stdout, stderr);
    }

    private static void RunChecked(string fileName, IReadOnlyList<string> args)
    {
        (int rc, string stdout, string stderr) = RunProcess(fileName, args);
        Assert.True(rc == 0, $"{fileName} failed ({rc}):\nstdout: {stdout}\nstderr: {stderr}");
    }

    private sealed class BrokenPipeWriteStream : Stream
    {
        private readonly long _throwAfterBytes;
        private long _written;

        public BrokenPipeWriteStream(long throwAfterBytes) => _throwAfterBytes = throwAfterBytes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush()
        {
            if (_written >= _throwAfterBytes)
                throw new IOException("Broken pipe", 32);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _written += buffer.Length;
            if (_written > _throwAfterBytes)
                throw new IOException("Broken pipe", 32);
        }
    }
}
