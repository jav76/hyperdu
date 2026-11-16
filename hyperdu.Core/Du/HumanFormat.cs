using System.Globalization;
using System.Text;

namespace hyperdu.Core.Du;

/// <summary>
/// Formats byte counts into GNU du human-readable or scaled integer strings using exact ceiling rounding.
/// </summary>
public static class HumanFormat
{
    private static readonly string[] BinarySuffixes = ["", "K", "M", "G", "T", "P", "E", "Z", "Y"];
    private static readonly string[] SiSuffixes = ["", "k", "M", "G", "T", "P", "E", "Z", "Y"];

    public static string Format(ulong bytes, BlockSizeSpec spec)
    {
        bool group = spec.Grouping && Messages.ShouldGroupThousands();
        return spec.Mode switch
        {
            HumanMode.Binary => FormatHuman(bytes, 1024, BinarySuffixes, group),
            HumanMode.Si => FormatHuman(bytes, 1000, SiSuffixes, group),
            _ => FormatFixedScale(bytes, spec.Scale, spec.Suffix, group)
        };
    }

    private static string FormatFixedScale(ulong bytes, ulong scale, string? suffix, bool group)
    {
        ulong blocks;
        if (bytes == 0 || scale <= 1)
        {
            blocks = bytes;
        }
        else
        {
            blocks = (ulong)(((UInt128)bytes + scale - 1) / scale);
        }

        string formatted = FormatUnsignedInteger(blocks, group);
        return string.IsNullOrEmpty(suffix) ? formatted : string.Concat(formatted, suffix);
    }

    private static string FormatHuman(ulong bytes, uint unitBase, string[] suffixes, bool group)
    {
        if (bytes < unitBase)
            return FormatUnsignedInteger(bytes, group);

        UInt128 value = bytes;
        UInt128 divisor = unitBase;
        int unitIndex = 1;

        while (value / divisor >= unitBase && unitIndex + 1 < suffixes.Length)
        {
            unitIndex++;
            divisor *= unitBase;
        }

        UInt128 tenths = (value * 10 + divisor - 1) / divisor;
        if (tenths < 100)
        {
            ulong integerPart = (ulong)(tenths / 10);
            ulong decimalPart = (ulong)(tenths % 10);
            return string.Create(CultureInfo.InvariantCulture, $"{integerPart}.{decimalPart}{suffixes[unitIndex]}");
        }

        UInt128 whole = (value + divisor - 1) / divisor;
        if (whole >= unitBase && unitIndex + 1 < suffixes.Length)
        {
            unitIndex++;
            divisor *= unitBase;
            tenths = (value * 10 + divisor - 1) / divisor;
            ulong integerPart = (ulong)(tenths / 10);
            ulong decimalPart = (ulong)(tenths % 10);
            return string.Create(CultureInfo.InvariantCulture, $"{integerPart}.{decimalPart}{suffixes[unitIndex]}");
        }

        string wholeText = FormatUInt128(whole, group);
        return string.Concat(wholeText, suffixes[unitIndex]);
    }

    private static string FormatUnsignedInteger(ulong value, bool group)
    {
        string digits = value.ToString(CultureInfo.InvariantCulture);
        return group ? InsertThousandsCommas(digits) : digits;
    }

    private static string FormatUInt128(UInt128 value, bool group)
    {
        string digits = value.ToString(CultureInfo.InvariantCulture);
        return group ? InsertThousandsCommas(digits) : digits;
    }

    private static string InsertThousandsCommas(string digits)
    {
        if (digits.Length <= 3)
            return digits;

        int commaCount = (digits.Length - 1) / 3;
        StringBuilder sb = new(digits.Length + commaCount);
        int firstGroupLen = digits.Length % 3;
        if (firstGroupLen == 0)
            firstGroupLen = 3;

        sb.Append(digits, 0, firstGroupLen);
        for (int i = firstGroupLen; i < digits.Length; i += 3)
        {
            sb.Append(',');
            sb.Append(digits, i, 3);
        }

        return sb.ToString();
    }
}
