using System.Globalization;

namespace hyperdu.Core.Du;

public enum DuParseOutcome
{
    Run,
    ExitSuccess,
    ExitFailure
}

public enum SymlinkPolicy
{
    NoDereference,
    DereferenceArgs,
    DereferenceAll
}

/// <summary>
/// Full GNU du option table parser across Phases 1 through 3.
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
    public bool OneFileSystem { get; private set; }
    public bool CountLinks { get; private set; }
    public bool Inodes { get; private set; }
    public TimeKind TimeKind { get; private set; } = TimeKind.None;
    public TimeStyleKind TimeStyleKind { get; private set; } = TimeStyleKind.LongIso;
    public string? CustomTimeFormat { get; private set; }
    public bool ShowTime => TimeKind != TimeKind.None;
    public SymlinkPolicy SymlinkPolicy { get; private set; } = SymlinkPolicy.NoDereference;
    public ThresholdSpec? Threshold { get; private set; }
    public string? Files0From { get; private set; }
    public ExcludeMatcher Excludes { get; } = new();
    public ulong? MaxDepth { get; private set; }
    public BlockSizeSpec BlockSizeSpec { get; private set; }
    public IReadOnlyList<string> Operands { get; private set; } = [];

    public static DuParseOutcome Parse(
        string[] args,
        string programName,
        string version,
        Stream stdout,
        Stream stderr,
        out DuOptions? parsedOptions) =>
        Parse(args, programName, version, Stream.Null, stdout, stderr, out parsedOptions);

    public static DuParseOutcome Parse(
        string[] args,
        string programName,
        string version,
        Stream stdin,
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
        string? cliTimeStyle = null;

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
                    result.SymlinkPolicy = SymlinkPolicy.NoDereference;
                    break;

                case 'D':
                case 'H':
                    result.SymlinkPolicy = SymlinkPolicy.DereferenceArgs;
                    break;

                case 'L':
                    result.SymlinkPolicy = SymlinkPolicy.DereferenceAll;
                    break;

                case 'l':
                    result.CountLinks = true;
                    break;

                case 'x':
                    result.OneFileSystem = true;
                    break;

                case OptInodes:
                    result.Inodes = true;
                    break;

                case 't':
                {
                    string rawThreshold = opt.Argument ?? string.Empty;
                    if (!BlockSize.TryParseThreshold(rawThreshold, out ThresholdSpec parsedThreshold, out BlockSizeParseStatus err))
                    {
                        string optLabel = opt.LongName != null ? $"--{opt.LongName}" : "-t";
                        Messages.WriteError(stderr, programName, BlockSize.FormatErrorMessage(optLabel, rawThreshold, err));
                        return DuParseOutcome.ExitFailure;
                    }

                    result.Threshold = parsedThreshold;
                    break;
                }

                case OptExclude:
                    result.Excludes.AddPattern(opt.Argument ?? string.Empty);
                    break;

                case 'X':
                    if (!result.Excludes.TryAddFromFile(opt.Argument ?? string.Empty, stdin, programName, stderr))
                        return DuParseOutcome.ExitFailure;
                    break;

                case OptFiles0From:
                    result.Files0From = opt.Argument ?? string.Empty;
                    break;

                case OptTime:
                    if (opt.Argument == null)
                    {
                        result.TimeKind = TimeKind.Mtime;
                    }
                    else if (TimeFormat.TryParseTimeKind(opt.Argument, programName, stderr, out TimeKind parsedTimeKind))
                    {
                        result.TimeKind = parsedTimeKind;
                    }
                    else
                    {
                        return DuParseOutcome.ExitFailure;
                    }
                    break;

                case OptTimeStyle:
                    cliTimeStyle = opt.Argument ?? string.Empty;
                    break;
            }
        }

        if (result.TimeKind != TimeKind.None)
        {
            if (!TimeFormat.TryResolveTimeStyle(cliTimeStyle, programName, stderr, out TimeStyleKind resolvedStyle, out string? customFmt))
                return DuParseOutcome.ExitFailure;

            result.TimeStyleKind = resolvedStyle;
            result.CustomTimeFormat = customFmt;
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

        if (result.Inodes && result.ApparentSize)
            Messages.WriteWarning(stderr, programName, "options --apparent-size and -b are ineffective with --inodes");

        if (result.Files0From != null && operands.Count > 0)
        {
            Messages.WriteError(stderr, programName, $"extra operand {Messages.Quote(operands[0])}");
            Messages.WriteUtf8(stderr, "file operands cannot be combined with --files0-from\n");
            Messages.WriteTryHelp(stderr, programName);
            return DuParseOutcome.ExitFailure;
        }

        BlockSizeSpec resolvedBlockSize = hasExplicitBlockSize
            ? explicitBlockSize
            : BlockSize.ResolveDefault(humanOverride);

        if (result.Inodes && resolvedBlockSize.Mode == HumanMode.None)
            resolvedBlockSize = BlockSizeSpec.Bytes.WithGrouping(resolvedBlockSize.Grouping);

        result.BlockSizeSpec = resolvedBlockSize;
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
          or:  {programName} [OPTION]... --files0-from=F
        Summarize device usage of the set of FILEs, recursively for directories.

        Mandatory arguments to long options are mandatory for short options too.
          -0, --null            end each output line with NUL, not newline
          -a, --all             write counts for all files, not just directories
              --apparent-size   print apparent sizes, rather than device usage
          -B, --block-size=SIZE scale sizes by SIZE before printing them
          -b, --bytes           equivalent to '--apparent-size --block-size=1'
          -c, --total           produce a grand total
          -D, --dereference-args  dereference only symlinks on the command line
          -d, --max-depth=N     print the total for a directory only if N or fewer levels deep
              --files0-from=F   summarize device usage of NUL-terminated file names in F
          -H                    equivalent to --dereference-args (-D)
          -h, --human-readable  print sizes in human readable format (e.g., 1K 234M 2G)
              --inodes          list inode usage information instead of block usage
          -k                    like --block-size=1K
          -L, --dereference     dereference all symbolic links
          -l, --count-links     count sizes many times if hard linked
          -m                    like --block-size=1M
          -P, --no-dereference  don't follow any symbolic links (this is the default)
          -S, --separate-dirs   for directories do not include size of subdirectories
              --si              like -h, but use powers of 1000 not 1024
          -s, --summarize       display only a total for each argument
          -t, --threshold=SIZE  exclude entries smaller than SIZE if positive,
                                  or entries greater than SIZE if negative
              --time            show time of the last modification of any file in the
                                  directory, or any of its subdirectories
              --time=WORD       show time as WORD instead of modification time:
                                  atime, access, use, ctime or status
              --time-style=STYLE  show times using STYLE, which can be:
                                    full-iso, long-iso, iso, or +FORMAT;
                                    FORMAT is interpreted like in 'date'
          -X, --exclude-from=FILE  exclude files that match any pattern in FILE
              --exclude=PATTERN    exclude files that match PATTERN
          -x, --one-file-system    skip directories on different file systems
              --help            display this help and exit
              --version         output version information and exit

        Display values are in units of the first available SIZE from --block-size,
        and the DU_BLOCK_SIZE, BLOCK_SIZE and BLOCKSIZE environment variables.
        Otherwise, units default to 1024 bytes (or 512 if POSIXLY_CORRECT is set).

        """;
}
