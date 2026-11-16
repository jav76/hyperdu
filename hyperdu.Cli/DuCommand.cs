using hyperdu.Core.Du;

namespace hyperdu.Cli;

/// <summary>
/// Entrypoint for GNU-compatible du persona execution (du / gdu symlink or `hyperdu du`).
/// Bypasses Spectre.Console and TUI setup so stdout and stderr stay byte-exact and noiseless.
/// </summary>
internal static class DuCommand
{
    public static int Run(string[] args, string programName, string version, bool restoreSigpipe = true)
    {
        if (restoreSigpipe)
            SequentialWalker.RestoreDefaultSigpipe();

        using Stream stdout = Console.OpenStandardOutput();
        using Stream stderr = Console.OpenStandardError();

        try
        {
            DuParseOutcome outcome = DuOptions.Parse(
                args,
                programName,
                version,
                stdout,
                stderr,
                out DuOptions? options);

            return outcome switch
            {
                DuParseOutcome.ExitSuccess => 0,
                DuParseOutcome.ExitFailure => 1,
                DuParseOutcome.Run when options != null => new SequentialWalker(options, programName, stdout, stderr).Run(),
                _ => 1
            };
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            return 141;
        }
    }

    private static bool IsBrokenPipe(IOException ex) =>
        ex.HResult == 32 ||
        (ex.HResult & 0xFFFF) == 32 ||
        ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase);
}
