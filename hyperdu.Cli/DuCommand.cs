using hyperdu.Core.Du;
using hyperdu.Core.Native;

namespace hyperdu.Cli;

/// <summary>
/// Entrypoint for GNU-compatible du persona execution (du / gdu symlink or `hyperdu du`).
/// Bypasses Spectre.Console and TUI setup so stdout and stderr stay byte-exact and noiseless.
/// </summary>
internal static class DuCommand
{
    internal readonly record struct StdStreams(Stream Stdin, Stream Stdout, Stream Stderr);

    public static int Run(string[] args, string programName, string version, bool restoreSigpipe = true)
    {
        if (restoreSigpipe)
            NativeIo.RestoreDefaultSigpipe();

        using Stream stdin = OpenStandardFd(0, FileAccess.Read) ?? Console.OpenStandardInput();
        using Stream stdout = OpenStandardFd(1, FileAccess.Write) ?? Console.OpenStandardOutput();
        using Stream stderr = OpenStandardFd(2, FileAccess.Write) ?? Console.OpenStandardError();

        return Run(args, programName, version, new StdStreams(stdin, stdout, stderr), restoreSigpipe);
    }

    internal static int Run(
        string[] args,
        string programName,
        string version,
        StdStreams streams,
        bool restoreSigpipe = false)
    {
        try
        {
            DuParseOutcome outcome = DuOptions.Parse(
                args,
                programName,
                version,
                streams.Stdin,
                streams.Stdout,
                streams.Stderr,
                out DuOptions? options);

            if (outcome == DuParseOutcome.ExitSuccess)
                return 0;

            if (outcome != DuParseOutcome.Run || options == null)
                return 1;

            return new ParallelDuWalker(options, programName, streams.Stdin, streams.Stdout, streams.Stderr).Run();
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            if (restoreSigpipe)
                NativeIo.RestoreDefaultSigpipeAndReraise();
            return 141;
        }
    }

    private static Stream? OpenStandardFd(int fd, FileAccess access)
    {
        if (OperatingSystem.IsWindows())
            return null;

        return new RawUnixFdStream(fd, writable: access == FileAccess.Write);
    }

    private static bool IsBrokenPipe(IOException ex) =>
        ex.HResult == 32 ||
        (ex.HResult & 0xFFFF) == 32 ||
        ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase);
}
