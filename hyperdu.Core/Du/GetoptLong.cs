using System.Text;

namespace hyperdu.Core.Du;

internal enum ArgumentRequirement
{
    NoArgument,
    RequiredArgument,
    OptionalArgument
}

internal sealed record LongOption(string Name, ArgumentRequirement HasArg, int Value);

internal sealed record ParsedOption(int Value, string? Argument, string? LongName);

/// <summary>
/// Port of GNU getopt_long supporting short option bundling, attached values,
/// long --opt=val, unique prefix abbreviations, ambiguity diagnostics, '--', and POSIXLY_CORRECT.
/// </summary>
internal static class GetoptLong
{
    public static bool TryParse(
        string[] args,
        string shortOpts,
        IReadOnlyList<LongOption> longOpts,
        string programName,
        Stream stderr,
        out List<ParsedOption> options,
        out List<string> operands)
    {
        options = [];
        operands = [];

        bool posixlyCorrect = Environment.GetEnvironmentVariable("POSIXLY_CORRECT") != null;
        int i = 0;

        while (i < args.Length)
        {
            string arg = args[i];

            if (arg == "--")
            {
                i++;
                while (i < args.Length)
                    operands.Add(args[i++]);
                break;
            }

            if (arg.Length <= 1 || arg[0] != '-')
            {
                operands.Add(arg);
                i++;
                if (posixlyCorrect)
                {
                    while (i < args.Length)
                        operands.Add(args[i++]);
                    break;
                }

                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!ParseLongOption(args, ref i, arg, longOpts, programName, stderr, out ParsedOption? parsed))
                    return false;

                options.Add(parsed!);
                continue;
            }

            if (!ParseShortCluster(args, ref i, arg, shortOpts, programName, stderr, options))
                return false;
        }

        return true;
    }

    private static bool ParseLongOption(
        string[] args,
        ref int index,
        string arg,
        IReadOnlyList<LongOption> longOpts,
        string programName,
        Stream stderr,
        out ParsedOption? parsed)
    {
        parsed = null;
        string body = arg.Substring(2);
        int eq = body.IndexOf('=');
        string namePart = eq >= 0 ? body.Substring(0, eq) : body;
        string? inlineArg = eq >= 0 ? body.Substring(eq + 1) : null;

        LongOption? exact = null;
        List<LongOption> matches = [];

        foreach (LongOption candidate in longOpts)
        {
            if (string.Equals(candidate.Name, namePart, StringComparison.Ordinal))
            {
                exact = candidate;
                break;
            }

            if (candidate.Name.StartsWith(namePart, StringComparison.Ordinal))
                matches.Add(candidate);
        }

        LongOption selected;
        if (exact != null)
        {
            selected = exact;
        }
        else if (matches.Count == 1)
        {
            selected = matches[0];
        }
        else if (matches.Count > 1)
        {
            // If all matching entries share identical semantics (Value + HasArg), no real ambiguity exists.
            bool allEquivalent = true;
            for (int m = 1; m < matches.Count; m++)
            {
                if (matches[m].Value != matches[0].Value || matches[m].HasArg != matches[0].HasArg)
                {
                    allEquivalent = false;
                    break;
                }
            }

            if (allEquivalent)
            {
                selected = matches[0];
            }
            else
            {
                StringBuilder sb = new();
                sb.Append($"option '--{namePart}' is ambiguous; possibilities:");
                foreach (LongOption match in matches)
                    sb.Append($" '--{match.Name}'");
                Messages.WriteError(stderr, programName, sb.ToString());
                return false;
            }
        }
        else
        {
            Messages.WriteError(stderr, programName, $"unrecognized option '--{namePart}'");
            return false;
        }

        index++;
        switch (selected.HasArg)
        {
            case ArgumentRequirement.NoArgument:
                if (inlineArg != null)
                {
                    Messages.WriteError(stderr, programName, $"option '--{selected.Name}' doesn't allow an argument");
                    return false;
                }

                parsed = new ParsedOption(selected.Value, null, selected.Name);
                return true;

            case ArgumentRequirement.OptionalArgument:
                parsed = new ParsedOption(selected.Value, inlineArg, selected.Name);
                return true;

            case ArgumentRequirement.RequiredArgument:
                if (inlineArg != null)
                {
                    parsed = new ParsedOption(selected.Value, inlineArg, selected.Name);
                    return true;
                }

                if (index >= args.Length)
                {
                    Messages.WriteError(stderr, programName, $"option '--{selected.Name}' requires an argument");
                    return false;
                }

                parsed = new ParsedOption(selected.Value, args[index++], selected.Name);
                return true;

            default:
                return false;
        }
    }

    private static bool ParseShortCluster(
        string[] args,
        ref int index,
        string arg,
        string shortOpts,
        string programName,
        Stream stderr,
        List<ParsedOption> options)
    {
        index++;
        for (int cIndex = 1; cIndex < arg.Length; cIndex++)
        {
            char c = arg[cIndex];
            if (!TryLookupShortOption(shortOpts, c, out ArgumentRequirement req))
            {
                Messages.WriteError(stderr, programName, $"invalid option -- '{c}'");
                return false;
            }

            switch (req)
            {
                case ArgumentRequirement.NoArgument:
                    options.Add(new ParsedOption(c, null, null));
                    break;

                case ArgumentRequirement.OptionalArgument:
                {
                    string? optArg = cIndex + 1 < arg.Length ? arg.Substring(cIndex + 1) : null;
                    options.Add(new ParsedOption(c, optArg, null));
                    return true;
                }

                case ArgumentRequirement.RequiredArgument:
                {
                    if (cIndex + 1 < arg.Length)
                    {
                        options.Add(new ParsedOption(c, arg.Substring(cIndex + 1), null));
                        return true;
                    }

                    if (index >= args.Length)
                    {
                        Messages.WriteError(stderr, programName, $"option requires an argument -- '{c}'");
                        return false;
                    }

                    options.Add(new ParsedOption(c, args[index++], null));
                    return true;
                }
            }
        }

        return true;
    }

    private static bool TryLookupShortOption(string shortOpts, char c, out ArgumentRequirement requirement)
    {
        for (int i = 0; i < shortOpts.Length; i++)
        {
            if (shortOpts[i] == ':')
                continue;

            if (shortOpts[i] == c)
            {
                if (i + 1 < shortOpts.Length && shortOpts[i + 1] == ':')
                {
                    requirement = (i + 2 < shortOpts.Length && shortOpts[i + 2] == ':')
                        ? ArgumentRequirement.OptionalArgument
                        : ArgumentRequirement.RequiredArgument;
                }
                else
                {
                    requirement = ArgumentRequirement.NoArgument;
                }

                return true;
            }
        }

        requirement = ArgumentRequirement.NoArgument;
        return false;
    }
}
