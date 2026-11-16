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

        return Encoding.ASCII.GetString(buffer[..written]);
    }

    internal static bool TryFormatBytes(ulong bytes, BlockSizeSpec spec, Span<byte> destination, out int bytesWritten)
    {
        bool group = spec.Grouping && Messages.ShouldGroupThousands();
        return spec.Mode switch
        {
            HumanMode.Binary => TryFormatHuman(bytes, 1024, BinarySuffixes, group, destination, out bytesWritten),
            HumanMode.Si => TryFormatHuman(bytes, 1000, SiSuffixes, group, destination, out bytesWritten),
            _ => TryFormatFixedScale(bytes, spec.Scale, spec.Suffix, group, destination, out bytesWritten)
        };
    }

    private static bool TryFormatFixedScale(
        ulong bytes,
        ulong scale,
        string? suffix,
        bool group,
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

        if (!TryFormatUInt64(blocks, group, destination, out int pos))
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
        Span<byte> destination,
        out int bytesWritten)
    {
        if (bytes < unitBase)
            return TryFormatUInt64(bytes, group, destination, out bytesWritten);

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
            return TryWriteTenths(tenths, suffixes[unitIndex], destination, out bytesWritten);

        UInt128 whole = (value + divisor - 1) / divisor;
        if (whole >= unitBase && unitIndex + 1 < suffixes.Length)
        {
            unitIndex++;
            divisor *= unitBase;
            tenths = (value * 10 + divisor - 1) / divisor;
            return TryWriteTenths(tenths, suffixes[unitIndex], destination, out bytesWritten);
        }

        if (!TryFormatUInt128(whole, group, destination, out int pos))
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

    private static bool TryWriteTenths(UInt128 tenths, string suffix, Span<byte> destination, out int bytesWritten)
    {
        ulong integerPart = (ulong)(tenths / 10);
        ulong decimalPart = (ulong)(tenths % 10);

        if (!integerPart.TryFormat(destination, out int pos, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        if (pos + 2 + suffix.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        destination[pos++] = (byte)'.';
        destination[pos++] = (byte)('0' + (int)decimalPart);
        Encoding.ASCII.GetBytes(suffix, destination[pos..]);
        pos += suffix.Length;
        bytesWritten = pos;
        return true;
    }

    private static bool TryFormatUInt64(ulong value, bool group, Span<byte> destination, out int bytesWritten)
    {
        if (!group)
            return value.TryFormat(destination, out bytesWritten, provider: CultureInfo.InvariantCulture);

        Span<byte> raw = stackalloc byte[32];
        if (!value.TryFormat(raw, out int digitLen, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        return TryInsertThousandsCommas(raw[..digitLen], destination, out bytesWritten);
    }

    private static bool TryFormatUInt128(UInt128 value, bool group, Span<byte> destination, out int bytesWritten)
    {
        if (!group)
            return value.TryFormat(destination, out bytesWritten, provider: CultureInfo.InvariantCulture);

        Span<byte> raw = stackalloc byte[64];
        if (!value.TryFormat(raw, out int digitLen, provider: CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        return TryInsertThousandsCommas(raw[..digitLen], destination, out bytesWritten);
    }

    private static bool TryInsertThousandsCommas(ReadOnlySpan<byte> digits, Span<byte> destination, out int bytesWritten)
    {
        if (digits.Length <= 3)
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

        int commaCount = (digits.Length - 1) / 3;
        int total = digits.Length + commaCount;
        if (total > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        int firstGroupLen = digits.Length % 3;
        if (firstGroupLen == 0)
            firstGroupLen = 3;

        int dstPos = 0;
        digits[..firstGroupLen].CopyTo(destination);
        dstPos += firstGroupLen;

        for (int i = firstGroupLen; i < digits.Length; i += 3)
        {
            destination[dstPos++] = (byte)',';
            digits.Slice(i, 3).CopyTo(destination[dstPos..]);
            dstPos += 3;
        }

        bytesWritten = dstPos;
        return true;
    }
}
