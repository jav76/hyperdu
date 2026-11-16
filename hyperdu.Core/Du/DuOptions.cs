using System.Globalization;

namespace hyperdu.Core.Du;

public enum DuParseOutcome
{
    Run,
    ExitSuccess,
    ExitFailure
}

/// <summary>
/// Full GNU du option table parser for Phase 1 flags, recognizing Phase 2/3 options
/// with explicit not-yet-implemented errors.
/// </summary>
public sealed class DuOptions
{
    private const int OptApparentSize = 256;
    private const int OptExclude = 257;
    private const int OptFiles0From = 258;
    private const int OptInodes = 259;
    private const int OptSi = 260;
    private const int OptTime = 261;
    private const int OptTimeStyle = 262;
    private const int OptHelp = 263;
    private const int OptVersion = 264;

    private const string ShortOptions = "0aB:bcDd:HhklLmPsSt:X:x";

    // Preserves GNU du.c long_options ordering so ambiguous prefixes match GNU du output.
    private static readonly LongOption[] LongOptions =
    [
        new("all", ArgumentRequirement.NoArgument, 'a'),
        new("apparent-size", ArgumentRequirement.NoArgument, OptApparentSize),
        new("block-size", ArgumentRequirement.RequiredArgument, 'B'),
        new("bytes", ArgumentRequirement.NoArgument, 'b'),
        new("count-links", ArgumentRequirement.NoArgument, 'l'),
        new("dereference", ArgumentRequirement.NoArgument, 'L'),
        new("dereference-args", ArgumentRequirement.NoArgument, 'D'),
        new("exclude", ArgumentRequirement.RequiredArgument, OptExclude),
        new("exclude-from", ArgumentRequirement.RequiredArgument, 'X'),
        new("files0-from", ArgumentRequirement.RequiredArgument, OptFiles0From),
        new("human-readable", ArgumentRequirement.NoArgument, 'h'),
        new("inodes", ArgumentRequirement.NoArgument, OptInodes),
        new("max-depth", ArgumentRequirement.RequiredArgument, 'd'),
        new("null", ArgumentRequirement.NoArgument, '0'),
        new("no-dereference", ArgumentRequirement.NoArgument, 'P'),
        new("one-file-system", ArgumentRequirement.NoArgument, 'x'),
        new("si", ArgumentRequirement.NoArgument, OptSi),
        new("separate-dirs", ArgumentRequirement.NoArgument, 'S'),
        new("summarize", ArgumentRequirement.NoArgument, 's'),
        new("total", ArgumentRequirement.NoArgument, 'c'),
        new("threshold", ArgumentRequirement.RequiredArgument, 't'),
        new("time", ArgumentRequirement.OptionalArgument, OptTime),
        new("time-style", ArgumentRequirement.RequiredArgument, OptTimeStyle),
        new("help", ArgumentRequirement.NoArgument, OptHelp),
        new("version", ArgumentRequirement.NoArgument, OptVersion)
    ];

    public bool NullTerminator { get; private set; }
    public bool All { get; private set; }
    public bool ApparentSize { get; private set; }
    public bool Summarize { get; private set; }
    public bool Total { get; private set; }
    public bool SeparateDirs { get; private set; }
    public ulong? MaxDepth { get; private set; }
    public BlockSizeSpec BlockSizeSpec { get; private set; }
    public IReadOnlyList<string> Operands { get; private set; } = [];

    public static DuParseOutcome Parse(
        string[] args,
        string programName,
        string version,
        Stream stdout,
        Stream stderr,
        out DuOptions? parsedOptions)
    {
        parsedOptions = null;

        if (!GetoptLong.TryParse(args, ShortOptions, LongOptions, programName, stderr, out List<ParsedOption> opts, out List<string> operands))
        {
            Messages.WriteTryHelp(stderr, programName);
            return DuParseOutcome.ExitFailure;
        }

        DuOptions result = new();
        bool hasExplicitBlockSize = false;
        BlockSizeSpec explicitBlockSize = BlockSizeSpec.Default1024;
        bool? humanOverride = null;

        foreach (ParsedOption opt in opts)
        {
            switch (opt.Value)
            {
                case OptHelp:
                    Messages.WriteUtf8(stdout, FormatHelp(programName));
                    stdout.Flush();
                    return DuParseOutcome.ExitSuccess;

                case OptVersion:
                    Messages.WriteUtf8(stdout, FormatVersion(version));
                    stdout.Flush();
                    return DuParseOutcome.ExitSuccess;

                case '0':
                    result.NullTerminator = true;
                    break;

                case 'a':
                    result.All = true;
                    break;

                case OptApparentSize:
                    result.ApparentSize = true;
                    break;

                case 'b':
                    result.ApparentSize = true;
                    explicitBlockSize = BlockSizeSpec.Bytes;
                    hasExplicitBlockSize = true;
                    humanOverride = null;
                    break;

                case 'k':
                    explicitBlockSize = BlockSizeSpec.Default1024;
                    hasExplicitBlockSize = true;
                    humanOverride = null;
                    break;

                case 'm':
                    explicitBlockSize = BlockSizeSpec.Megabytes;
                    hasExplicitBlockSize = true;
                    humanOverride = null;
                    break;

                case 'h':
                    humanOverride = true;
                    hasExplicitBlockSize = false;
                    break;

                case OptSi:
                    humanOverride = false;
                    hasExplicitBlockSize = false;
                    break;

                case 'B':
                {
                    if (!BlockSize.TryParse(opt.Argument ?? string.Empty, out BlockSizeSpec parsedSpec, out BlockSizeParseStatus err))
                    {
                        string optLabel = opt.LongName != null ? $"--{opt.LongName}" : "-B";
                        Messages.WriteError(stderr, programName, BlockSize.FormatErrorMessage(optLabel, opt.Argument ?? string.Empty, err));
                        return DuParseOutcome.ExitFailure;
                    }

                    explicitBlockSize = parsedSpec;
                    hasExplicitBlockSize = true;
                    humanOverride = null;
                    break;
                }

                case 'c':
                    result.Total = true;
                    break;

                case 'd':
                {
                    string rawDepth = opt.Argument ?? string.Empty;
                    if (!TryParseDepth(rawDepth, out ulong depth))
                    {
                        Messages.WriteError(stderr, programName, $"invalid maximum depth {Messages.Quote(rawDepth)}");
                        Messages.WriteTryHelp(stderr, programName);
                        return DuParseOutcome.ExitFailure;
                    }

                    result.MaxDepth = depth;
                    break;
                }

                case 's':
                    result.Summarize = true;
                    break;

                case 'S':
                    result.SeparateDirs = true;
                    break;

                case 'P':
                    // Default policy in Phase 1 is never dereferencing symlinks.
                    break;

                case 'D':
                case 'H':
                case 'L':
                case 'l':
                case 't':
                case 'X':
                case 'x':
                case OptExclude:
                case OptFiles0From:
                case OptInodes:
                case OptTime:
                case OptTimeStyle:
                {
                    string flagName = opt.LongName != null ? $"--{opt.LongName}" : $"-{(char)opt.Value}";
                    Messages.WriteError(stderr, programName, $"option '{flagName}' is not yet implemented");
                    return DuParseOutcome.ExitFailure;
                }
            }
        }

        if (result.Summarize && result.All)
        {
            Messages.WriteError(stderr, programName, "cannot both summarize and show all entries");
            Messages.WriteTryHelp(stderr, programName);
            return DuParseOutcome.ExitFailure;
        }

        if (result.Summarize && result.MaxDepth is ulong maxDepth)
        {
            if (maxDepth == 0)
            {
                Messages.WriteWarning(stderr, programName, "summarizing is the same as using --max-depth=0");
            }
            else
            {
                Messages.WriteWarning(stderr, programName, $"summarizing conflicts with --max-depth={maxDepth}");
                Messages.WriteTryHelp(stderr, programName);
                return DuParseOutcome.ExitFailure;
            }
        }

        result.BlockSizeSpec = hasExplicitBlockSize
            ? explicitBlockSize
            : BlockSize.ResolveDefault(humanOverride);

        result.Operands = operands.Count > 0 ? operands : ["."];
        parsedOptions = result;
        return DuParseOutcome.Run;
    }

    private static bool TryParseDepth(string raw, out ulong depth)
    {
        if (string.IsNullOrEmpty(raw))
        {
            depth = 0;
            return false;
        }

        return ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out depth);
    }

    public static string FormatVersion(string version) => $"du (hyperdu) {version}\n";

    public static string FormatHelp(string programName) =>
        $"""
        Usage: {programName} [OPTION]... [FILE]...
        Summarize device usage of the set of FILEs, recursively for directories.

        Mandatory arguments to long options are mandatory for short options too.
          -0, --null            end each output line with NUL, not newline
          -a, --all             write counts for all files, not just directories
              --apparent-size   print apparent sizes, rather than device usage
          -B, --block-size=SIZE scale sizes by SIZE before printing them
          -b, --bytes           equivalent to '--apparent-size --block-size=1'
          -c, --total           produce a grand total
          -d, --max-depth=N     print the total for a directory only if N or fewer levels deep
          -h, --human-readable  print sizes in human readable format (e.g., 1K 234M 2G)
          -k                    like --block-size=1K
          -m                    like --block-size=1M
          -P, --no-dereference  don't follow any symbolic links (this is the default)
          -S, --separate-dirs   for directories do not include size of subdirectories
              --si              like -h, but use powers of 1000 not 1024
          -s, --summarize       display only a total for each argument
              --help            display this help and exit
              --version         output version information and exit

        Display values are in units of the first available SIZE from --block-size,
        and the DU_BLOCK_SIZE, BLOCK_SIZE and BLOCKSIZE environment variables.
        Otherwise, units default to 1024 bytes (or 512 if POSIXLY_CORRECT is set).

        """;
}
