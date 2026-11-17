using System.Globalization;

namespace hyperdu.Core.Du;

internal enum HumanMode
{
    None,
    Binary,
    Si
}

/// <summary>
/// Parsed block-size display specification from CLI options or environment variables.
/// </summary>
public readonly record struct BlockSizeSpec
{
    internal BlockSizeSpec(ulong scale, HumanMode mode, string? suffix, bool grouping)
    {
        Scale = scale;
        Mode = mode;
        Suffix = suffix;
        Grouping = grouping;
    }

    public ulong Scale { get; }
    internal HumanMode Mode { get; }
    public string? Suffix { get; }
    public bool Grouping { get; }

    public static BlockSizeSpec Default1024 => new(1024, HumanMode.None, null, false);
    public static BlockSizeSpec Default512 => new(512, HumanMode.None, null, false);
    public static BlockSizeSpec Bytes => new(1, HumanMode.None, null, false);
    public static BlockSizeSpec Megabytes => new(1048576, HumanMode.None, null, false);
    public static BlockSizeSpec HumanBinary => new(1024, HumanMode.Binary, null, false);
    public static BlockSizeSpec HumanSi => new(1000, HumanMode.Si, null, false);

    internal BlockSizeSpec WithGrouping(bool grouping) => new(Scale, Mode, Suffix, grouping);
}

/// <summary>
/// Parsed --threshold (-t) specification.
/// </summary>
public readonly record struct ThresholdSpec(bool IsNegative, ulong Value)
{
    public bool Matches(ulong value) => IsNegative ? value <= Value : value >= Value;
}

internal enum BlockSizeParseStatus
{
    Ok,
    InvalidArgument,
    InvalidSuffix,
    Overflow,
    NegativeZeroThreshold
}

/// <summary>
/// Parses GNU block-size arguments, --threshold values, and environment variables (DU_BLOCK_SIZE, BLOCK_SIZE, BLOCKSIZE).
/// </summary>
public static class BlockSize
{
    private static readonly (char Letter, int Power)[] UnitPowers =
    [
        ('K', 1),
        ('k', 1),
        ('M', 2),
        ('G', 3),
        ('T', 4),
        ('P', 5),
        ('E', 6),
        ('Z', 7),
        ('Y', 8)
    ];

    internal static BlockSizeSpec ResolveDefault(bool? humanReadableOverride = null)
    {
        string?[] envVars =
        [
            Environment.GetEnvironmentVariable("DU_BLOCK_SIZE"),
            Environment.GetEnvironmentVariable("BLOCK_SIZE"),
            Environment.GetEnvironmentVariable("BLOCKSIZE")
        ];

        foreach (string? raw in envVars)
        {
            if (raw != null && TryParse(raw, out BlockSizeSpec parsed, out _) && parsed.Scale > 0)
                return ApplyHumanOverride(parsed, humanReadableOverride);
        }

        ulong scale = Environment.GetEnvironmentVariable("POSIXLY_CORRECT") != null ? 512UL : 1024UL;
        return ApplyHumanOverride(new BlockSizeSpec(scale, HumanMode.None, null, false), humanReadableOverride);
    }

    private static BlockSizeSpec ApplyHumanOverride(BlockSizeSpec spec, bool? humanReadableOverride)
    {
        if (humanReadableOverride == true)
            return new BlockSizeSpec(1024, HumanMode.Binary, null, spec.Grouping);
        if (humanReadableOverride == false)
            return new BlockSizeSpec(1000, HumanMode.Si, null, spec.Grouping);
        return spec;
    }

    internal static bool TryParse(string raw, out BlockSizeSpec spec, out BlockSizeParseStatus errorStatus)
    {
        spec = default;
        if (string.IsNullOrEmpty(raw))
        {
            errorStatus = BlockSizeParseStatus.InvalidArgument;
            return false;
        }

        bool grouping = false;
        ReadOnlySpan<char> span = raw.AsSpan();
        if (span[0] == '\'')
        {
            grouping = true;
            span = span[1..];
            if (span.IsEmpty)
            {
                // Standalone "'" in BLOCK_SIZE enables grouping without changing scale when used via env; invalid on CLI -B.
                spec = new BlockSizeSpec(0, HumanMode.None, null, true);
                errorStatus = BlockSizeParseStatus.InvalidArgument;
                return false;
            }
        }

        if (span.Equals("human-readable", StringComparison.Ordinal) ||
            span.Equals("human", StringComparison.Ordinal))
        {
            spec = new BlockSizeSpec(1024, HumanMode.Binary, null, grouping);
            errorStatus = BlockSizeParseStatus.Ok;
            return true;
        }

        if (span.Equals("si", StringComparison.Ordinal))
        {
            spec = new BlockSizeSpec(1000, HumanMode.Si, null, grouping);
            errorStatus = BlockSizeParseStatus.Ok;
            return true;
        }

        if (!TryParseScaledValue(span, allowZero: false, out ulong totalScale, out bool hasDigits, out string? bareSuffix, out errorStatus))
            return false;

        // GNU du prints a unit suffix only when the user specified a bare unit (e.g. -BK, -BKiB, -BKB),
        // never when a leading numeric multiplier is present (e.g. -B1K, -B1024).
        spec = new BlockSizeSpec(totalScale, HumanMode.None, hasDigits ? null : bareSuffix, grouping);
        return true;
    }

    internal static bool TryParseThreshold(string raw, out ThresholdSpec threshold, out BlockSizeParseStatus errorStatus)
    {
        threshold = default;
        if (string.IsNullOrEmpty(raw))
        {
            errorStatus = BlockSizeParseStatus.InvalidArgument;
            return false;
        }

        ReadOnlySpan<char> span = raw.AsSpan();
        bool isNegative = false;
        if (span[0] == '-')
        {
            isNegative = true;
            span = span[1..];
        }
        else if (span[0] == '+')
        {
            span = span[1..];
        }

        if (span.IsEmpty)
        {
            errorStatus = BlockSizeParseStatus.InvalidArgument;
            return false;
        }

        if (!TryParseScaledValue(span, allowZero: true, out ulong magnitude, out _, out _, out errorStatus))
            return false;

        if (isNegative && magnitude == 0)
        {
            errorStatus = BlockSizeParseStatus.NegativeZeroThreshold;
            return false;
        }

        ulong maxSignedMagnitude = isNegative ? 9223372036854775808UL : (ulong)long.MaxValue;
        if (magnitude > maxSignedMagnitude)
        {
            errorStatus = BlockSizeParseStatus.Overflow;
            return false;
        }

        threshold = new ThresholdSpec(isNegative, magnitude);
        return true;
    }

    internal static string FormatErrorMessage(string optionName, string rawArg, BlockSizeParseStatus status)
    {
        if (status == BlockSizeParseStatus.NegativeZeroThreshold)
            return "invalid --threshold argument '-0'";

        string quoted = Messages.QuoteAscii(rawArg);
        return status switch
        {
            BlockSizeParseStatus.InvalidSuffix => $"invalid suffix in {optionName} argument {quoted}",
            BlockSizeParseStatus.Overflow => $"{optionName} argument {quoted} too large",
            _ => $"invalid {optionName} argument {quoted}"
        };
    }

    private static bool TryParseScaledValue(
        ReadOnlySpan<char> span,
        bool allowZero,
        out ulong totalScale,
        out bool hasDigits,
        out string? normalizedSuffix,
        out BlockSizeParseStatus errorStatus)
    {
        totalScale = 0;
        normalizedSuffix = null;

        int digitEnd = 0;
        while (digitEnd < span.Length && char.IsAsciiDigit(span[digitEnd]))
            digitEnd++;

        hasDigits = digitEnd > 0;
        ulong multiplier = 1;
        if (hasDigits)
        {
            if (!ulong.TryParse(span[..digitEnd], NumberStyles.None, CultureInfo.InvariantCulture, out multiplier))
            {
                errorStatus = BlockSizeParseStatus.Overflow;
                return false;
            }

            if (multiplier == 0 && !allowZero)
            {
                errorStatus = BlockSizeParseStatus.InvalidArgument;
                return false;
            }
        }

        ReadOnlySpan<char> suffixSpan = span[digitEnd..];
        if (suffixSpan.IsEmpty)
        {
            totalScale = multiplier;
            errorStatus = BlockSizeParseStatus.Ok;
            return true;
        }

        if (!TryParseSuffix(suffixSpan, out int power, out bool isSi, out normalizedSuffix))
        {
            errorStatus = hasDigits ? BlockSizeParseStatus.InvalidSuffix : BlockSizeParseStatus.InvalidArgument;
            return false;
        }

        ulong unitBase = isSi ? 1000UL : 1024UL;
        if (!TryComputeUnitMultiplier(multiplier, unitBase, power, out totalScale))
        {
            errorStatus = BlockSizeParseStatus.Overflow;
            return false;
        }

        errorStatus = BlockSizeParseStatus.Ok;
        return true;
    }

    private static bool TryParseSuffix(
        ReadOnlySpan<char> suffix,
        out int power,
        out bool isSi,
        out string? normalizedSuffix)
    {
        power = 0;
        isSi = false;
        normalizedSuffix = null;

        char first = suffix[0];
        foreach ((char letter, int p) in UnitPowers)
        {
            if (letter == first)
            {
                power = p;
                break;
            }
        }

        if (power == 0)
            return false;

        char canonicalUpper = char.ToUpperInvariant(first);

        if (suffix.Length == 1)
        {
            isSi = false;
            normalizedSuffix = canonicalUpper.ToString();
            return true;
        }

        if (suffix.Length == 2 && suffix[1] == 'B')
        {
            isSi = true;
            // GNU du prints lowercase "kB" for 1000^1 and uppercase "MB", "GB", ... for higher SI units.
            normalizedSuffix = power == 1 ? "kB" : string.Concat(canonicalUpper.ToString(), "B");
            return true;
        }

        if (suffix.Length == 3 && suffix[1] == 'i' && suffix[2] == 'B' && first != 'k')
        {
            isSi = false;
            normalizedSuffix = string.Concat(canonicalUpper.ToString(), "iB");
            return true;
        }

        return false;
    }

    private static bool TryComputeUnitMultiplier(ulong prefix, ulong unitBase, int power, out ulong result)
    {
        UInt128 factor = 1;
        for (int i = 0; i < power; i++)
            factor *= unitBase;

        UInt128 product = (UInt128)prefix * factor;
        if (product > ulong.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (ulong)product;
        return true;
    }
}
