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
        Span<byte> buffer = stackalloc byte[128];
        if (!TryFormatBytes(bytes, spec, buffer, out int written))
            return string.Empty;

        return Encoding.UTF8.GetString(buffer[..written]);
    }

    internal static bool TryFormatBytes(ulong bytes, BlockSizeSpec spec, Span<byte> destination, out int bytesWritten) =>
        TryFormatBytes(bytes, spec, Messages.GetNumericLocaleFormat(), destination, out bytesWritten);

    internal static bool TryFormatBytes(
        ulong bytes,
        BlockSizeSpec spec,
        in NumericLocaleFormat localeFormat,
        Span<byte> destination,
        out int bytesWritten)
    {
        bool group = spec.Grouping && localeFormat.HasGrouping;
        return spec.Mode switch
        {
            HumanMode.Binary => TryFormatHuman(bytes, 1024, BinarySuffixes, group, in localeFormat, destination, out bytesWritten),
            HumanMode.Si => TryFormatHuman(bytes, 1000, SiSuffixes, group, in localeFormat, destination, out bytesWritten),
            _ => TryFormatFixedScale(bytes, spec.Scale, spec.Suffix, group, in localeFormat, destination, out bytesWritten)
        };
    }

    private static bool TryFormatFixedScale(
        ulong bytes,
        ulong scale,
        string? suffix,
        bool group,
        in NumericLocaleFormat localeFormat,
        Span<byte> destination,
        out int bytesWritten)
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

        if (!TryFormatUInt64(blocks, group, in localeFormat, destination, out int pos))
        {
            bytesWritten = 0;
            return false;
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            if (pos + suffix.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            Encoding.ASCII.GetBytes(suffix, destination[pos..]);
            pos += suffix.Length;
        }

        bytesWritten = pos;
        return true;
    }

    private static bool TryFormatHuman(
        ulong bytes,
        uint unitBase,
        string[] suffixes,
        bool group,
        in NumericLocaleFormat localeFormat,
        Span<byte> destination,
        out int bytesWritten)
    {
        if (bytes < unitBase)
            return TryFormatUInt64(bytes, group, in localeFormat, destination, out bytesWritten);

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
            return TryWriteTenths(tenths, suffixes[unitIndex], localeFormat.DecimalPoint, destination, out bytesWritten);

        UInt128 whole = (value + divisor - 1) / divisor;
        if (whole >= unitBase && unitIndex + 1 < suffixes.Length)
        {
            unitIndex++;
            divisor *= unitBase;
            tenths = (value * 10 + divisor - 1) / divisor;
            return TryWriteTenths(tenths, suffixes[unitIndex], localeFormat.DecimalPoint, destination, out bytesWritten);
        }

        if (!TryFormatUInt128(whole, group, in localeFormat, destination, out int pos))
        {
            bytesWritten = 0;
            return false;
        }

        string suffix = suffixes[unitIndex];
        if (!string.IsNullOrEmpty(suffix))
        {
            if (pos + suffix.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            Encoding.ASCII.GetBytes(suffix, destination[pos..]);
            pos += suffix.Length;
        }

        bytesWritten = pos;
        return true;
    }

    private static bool TryWriteTenths(
        UInt128 tenths,
        string suffix,
        ReadOnlySpan<byte> decimalPoint,
        Span<byte> destination,
        out int bytesWritten)
    {
        ulong integerPart = (ulong)(tenths / 10);
        ulong decimalPart = (ulong)(tenths % 10);

        if (!integerPart.TryFormat(destination, out int pos, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        if (pos + decimalPoint.Length + 1 + suffix.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        decimalPoint.CopyTo(destination[pos..]);
        pos += decimalPoint.Length;
        destination[pos++] = (byte)('0' + (int)decimalPart);
        Encoding.ASCII.GetBytes(suffix, destination[pos..]);
        pos += suffix.Length;
        bytesWritten = pos;
        return true;
    }

    private static bool TryFormatUInt64(
        ulong value,
        bool group,
        in NumericLocaleFormat localeFormat,
        Span<byte> destination,
        out int bytesWritten)
    {
        if (!group)
            return value.TryFormat(destination, out bytesWritten, provider: CultureInfo.InvariantCulture);

        Span<byte> raw = stackalloc byte[32];
        if (!value.TryFormat(raw, out int digitLen, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        return TryInsertGrouping(raw[..digitLen], localeFormat.ThousandsSep, localeFormat.Grouping, destination, out bytesWritten);
    }

    private static bool TryFormatUInt128(
        UInt128 value,
        bool group,
        in NumericLocaleFormat localeFormat,
        Span<byte> destination,
        out int bytesWritten)
    {
        if (!group)
            return value.TryFormat(destination, out bytesWritten, provider: CultureInfo.InvariantCulture);

        Span<byte> raw = stackalloc byte[64];
        if (!value.TryFormat(raw, out int digitLen, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        return TryInsertGrouping(raw[..digitLen], localeFormat.ThousandsSep, localeFormat.Grouping, destination, out bytesWritten);
    }

    private static bool TryInsertGrouping(
        ReadOnlySpan<byte> digits,
        ReadOnlySpan<byte> thousandsSep,
        ReadOnlySpan<byte> grouping,
        Span<byte> destination,
        out int bytesWritten)
    {
        int curSize = grouping.IsEmpty ? 0 : grouping[0];
        if (thousandsSep.IsEmpty || curSize <= 0 || curSize == 127 || digits.Length <= curSize)
        {
            if (digits.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            digits.CopyTo(destination);
            bytesWritten = digits.Length;
            return true;
        }

        Span<byte> rev = stackalloc byte[128];
        int pos = rev.Length;
        int dIdx = digits.Length - 1;
        int grpIdx = 0;
        int remainingInGroup = curSize;

        while (dIdx >= 0)
        {
            if (remainingInGroup == 0)
            {
                if (pos < thousandsSep.Length)
                {
                    bytesWritten = 0;
                    return false;
                }

                for (int s = thousandsSep.Length - 1; s >= 0; s--)
                    rev[--pos] = thousandsSep[s];

                if (grpIdx + 1 < grouping.Length)
                {
                    byte next = grouping[++grpIdx];
                    if (next == 127)
                        curSize = int.MaxValue;
                    else if (next > 0)
                        curSize = next;
                }

                remainingInGroup = curSize;
            }

            if (pos == 0)
            {
                bytesWritten = 0;
                return false;
            }

            rev[--pos] = digits[dIdx--];
            remainingInGroup--;
        }

        ReadOnlySpan<byte> formatted = rev[pos..];
        if (formatted.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        formatted.CopyTo(destination);
        bytesWritten = formatted.Length;
        return true;
    }
}
