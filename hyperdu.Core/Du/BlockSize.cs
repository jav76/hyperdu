using System.Globalization;

namespace hyperdu.Core.Du;

internal enum HumanMode
{
    None,
    Binary,
    Si,
    BsdBinary,
    BsdSi
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
    public static BlockSizeSpec BsdHumanBinary => new(1024, HumanMode.BsdBinary, null, false);
    public static BlockSizeSpec BsdHumanSi => new(1000, HumanMode.BsdSi, null, false);

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

    /// <summary>
    /// Resolves BSD getbsize(3) from the BLOCKSIZE environment variable (512 default, 1G max, 512 min).
    /// </summary>
    internal static ulong ResolveBsdBlockSize(string diagName, Stream stderr)
    {
        const ulong kb = 1024UL;
        const ulong mb = 1024UL * 1024UL;
        const ulong gb = 1024UL * 1024UL * 1024UL;

        string? raw = Environment.GetEnvironmentVariable("BLOCKSIZE");
        if (string.IsNullOrEmpty(raw))
            return 512UL;

        long parsed = ParseStrtolPrefix(raw, out int endIdx, out bool overflow);
        if (parsed < 0)
        {
            Messages.WriteError(stderr, diagName, "minimum blocksize is 512");
            return 512UL;
        }

        long n = parsed == 0 && !overflow ? 1 : parsed;
        int remaining = raw.Length - endIdx;
        if (remaining > 1)
        {
            Messages.WriteError(stderr, diagName, $"{raw}: unknown blocksize");
            return 512UL;
        }

        ulong max;
        ulong mul;
        char unit = remaining == 1 ? raw[endIdx] : '\0';
        switch (unit)
        {
            case 'G':
            case 'g':
                max = 1;
                mul = gb;
                break;
            case 'K':
            case 'k':
                max = gb / kb;
                mul = kb;
                break;
            case 'M':
            case 'm':
                max = gb / mb;
                mul = mb;
                break;
            case '\0':
                max = gb;
                mul = 1;
                break;
            default:
                Messages.WriteError(stderr, diagName, $"{raw}: unknown blocksize");
                return 512UL;
        }

        ulong un = overflow ? gb + 1 : (ulong)n;
        if (un > max)
        {
            Messages.WriteError(stderr, diagName, "maximum blocksize is 1G");
            un = max;
        }

        ulong blockSize = un * mul;
        if (blockSize < 512UL)
        {
            Messages.WriteError(stderr, diagName, "minimum blocksize is 512");
            return 512UL;
        }

        return blockSize;
    }

    internal static bool TryParseBsdAtoi(string raw, out int value, out bool overflow)
    {
        long parsed = ParseStrtolPrefix(raw, out _, out overflow);
        value = unchecked((int)parsed);
        return !overflow;
    }

    internal static bool TryParseBsdExpandNumber(string raw, out long value)
    {
        value = 0;
        int i = 0;
        bool negative = SkipWhitespaceAndSign(raw, ref i);

        if (i >= raw.Length || !char.IsAsciiDigit(raw[i]))
            return false;

        int numBase = 10;
        if (raw[i] == '0')
        {
            if (i + 1 < raw.Length && (raw[i + 1] == 'x' || raw[i + 1] == 'X'))
            {
                numBase = 16;
                i += 2;
                if (i >= raw.Length || !char.IsAsciiHexDigit(raw[i]))
                    return false;
            }
            else
            {
                numBase = 8;
            }
        }

        ulong number = 0;
        int digitStart = i;
        while (i < raw.Length)
        {
            int digit = ParseDigitForBase(raw[i], numBase);
            if (digit < 0)
                break;

            if (number > (ulong.MaxValue - (ulong)digit) / (ulong)numBase)
                return false;

            number = number * (ulong)numBase + (ulong)digit;
            i++;
        }

        if (i == digitStart && numBase == 16)
            return false;

        int shift = 0;
        if (i < raw.Length)
        {
            shift = char.ToLowerInvariant(raw[i]) switch
            {
                'e' => 60,
                'p' => 50,
                't' => 40,
                'g' => 30,
                'm' => 20,
                'k' => 10,
                _ => -1
            };

            if (shift > 0)
                i++;
            else
                shift = 0;
        }

        if (i < raw.Length && (raw[i] == 'b' || raw[i] == 'B'))
            i++;

        if (i != raw.Length)
            return false;

        if (shift > 0)
        {
            ulong shifted = number << shift;
            if ((shifted >> shift) != number)
                return false;
            number = shifted;
        }

        if (negative)
        {
            if (number > 0x8000000000000000UL)
                return false;
            value = unchecked(-(long)number);
        }
        else
        {
            if (number > long.MaxValue)
                return false;
            value = (long)number;
        }

        return true;
    }

    private static long ParseStrtolPrefix(string raw, out int endIdx, out bool overflow)
    {
        overflow = false;
        int i = 0;
        bool negative = SkipWhitespaceAndSign(raw, ref i);

        int digitStart = i;
        ulong acc = 0;
        ulong limit = negative ? 0x8000000000000000UL : (ulong)long.MaxValue;
        while (i < raw.Length && char.IsAsciiDigit(raw[i]))
        {
            ulong d = (ulong)(raw[i] - '0');
            if (!overflow)
            {
                if (acc > (limit - d) / 10UL)
                    overflow = true;
                else
                    acc = acc * 10UL + d;
            }
            i++;
        }

        endIdx = i == digitStart ? i : i;
        if (overflow)
            return negative ? long.MinValue : long.MaxValue;

        return negative ? unchecked(-(long)acc) : (long)acc;
    }

    private static bool SkipWhitespaceAndSign(string raw, ref int index)
    {
        while (index < raw.Length && char.IsWhiteSpace(raw[index]))
            index++;

        bool negative = false;
        if (index < raw.Length && raw[index] == '-')
        {
            negative = true;
            index++;
        }
        else if (index < raw.Length && raw[index] == '+')
        {
            index++;
        }

        return negative;
    }

    private static int ParseDigitForBase(char c, int numBase)
    {
        if (c >= '0' && c <= '9')
        {
            int d = c - '0';
            return d < numBase ? d : -1;
        }

        if (numBase == 16)
        {
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        }

        return -1;
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
