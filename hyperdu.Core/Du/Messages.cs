using System.Buffers;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

internal readonly record struct NumericLocaleFormat(byte[] DecimalPoint, byte[] ThousandsSep, byte[] Grouping)
{
    internal static readonly NumericLocaleFormat Invariant = new([(byte)'.'], [], []);
    internal static readonly NumericLocaleFormat FallbackGrouped = new([(byte)'.'], [(byte)','], [3]);

    internal bool HasGrouping => ThousandsSep.Length > 0 && Grouping.Length > 0 && Grouping[0] > 0 && Grouping[0] != 127;
}

/// <summary>
/// Formats GNU-compatible diagnostic messages, locale-aware quotes, and POSIX errno descriptions.
/// </summary>
public static class Messages
{
    private sealed record CachedNumericLocale(string Key, NumericLocaleFormat Format);

    private static readonly byte[] CQuoteOpen = [(byte)'\''];
    private static readonly byte[] CQuoteClose = [(byte)'\''];
    private static readonly byte[] Utf8QuoteOpen = [0xE2, 0x80, 0x98]; // U+2018
    private static readonly byte[] Utf8QuoteClose = [0xE2, 0x80, 0x99]; // U+2019

    private static volatile CachedNumericLocale? s_cachedNumericLocale;

    internal static bool IsUtf8Locale()
    {
        string? locale = GetEffectiveCategoryLocale("LC_CTYPE");
        if (string.IsNullOrEmpty(locale))
            return false;

        if (string.Equals(locale, "C", StringComparison.Ordinal) ||
            string.Equals(locale, "POSIX", StringComparison.Ordinal))
            return false;

        bool looksUtf8 = locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) ||
                         locale.Contains("utf8", StringComparison.OrdinalIgnoreCase);
        if (!looksUtf8)
            return false;

        return !NativeIo.IsSupported || NativeIo.IsPosixCtypeLocaleAvailable(locale);
    }

    internal static bool ShouldGroupThousands() => GetNumericLocaleFormat().HasGrouping;

    internal static NumericLocaleFormat GetNumericLocaleFormat()
    {
        string? locale = GetEffectiveCategoryLocale("LC_NUMERIC");
        if (IsCPosixLocale(locale))
            return NumericLocaleFormat.Invariant;

        CachedNumericLocale? cached = s_cachedNumericLocale;
        if (cached != null && string.Equals(cached.Key, locale, StringComparison.Ordinal))
            return cached.Format;

        NumericLocaleFormat resolved;
        if (!NativeIo.IsSupported)
        {
            resolved = NumericLocaleFormat.FallbackGrouped;
        }
        else if (NativeIo.TryGetNumericLocaleStrings(locale!, out byte[] decimalPoint, out byte[] thousandsSep, out byte[] grouping))
        {
            resolved = new NumericLocaleFormat(decimalPoint, thousandsSep, grouping);
        }
        else
        {
            resolved = NumericLocaleFormat.Invariant;
        }

        s_cachedNumericLocale = new CachedNumericLocale(locale!, resolved);
        return resolved;
    }

    internal static bool IsCPosixLocale(string? locale) =>
        string.IsNullOrEmpty(locale) ||
        string.Equals(locale, "C", StringComparison.Ordinal) ||
        string.Equals(locale, "POSIX", StringComparison.Ordinal) ||
        locale.StartsWith("C.", StringComparison.Ordinal) ||
        locale.StartsWith("POSIX.", StringComparison.Ordinal);

    internal static string? GetEffectiveCategoryLocale(string category)
    {
        string? value = Environment.GetEnvironmentVariable("LC_ALL");
        if (!string.IsNullOrEmpty(value)) return value;

        value = Environment.GetEnvironmentVariable(category);
        if (!string.IsNullOrEmpty(value)) return value;

        value = Environment.GetEnvironmentVariable("LANG");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static string Quote(string value)
    {
        if (IsUtf8Locale())
            return string.Concat("\u2018", value, "\u2019");
        return string.Concat("'", value, "'");
    }

    internal static string QuoteAscii(string value) => string.Concat("'", value, "'");

    internal static string BsdProgramName(string programName)
    {
        if (string.IsNullOrEmpty(programName))
            return "du";

        string fileName = Path.GetFileName(programName);
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^4];

        return string.IsNullOrEmpty(fileName) ? "du" : fileName;
    }

    internal static void WriteError(Stream stderr, string programName, string message)
    {
        WriteUtf8(stderr, $"{programName}: {message}\n");
    }

    internal static void WriteWarning(Stream stderr, string programName, string message)
    {
        WriteUtf8(stderr, $"{programName}: warning: {message}\n");
    }

    internal static void WriteTryHelp(Stream stderr, string programName)
    {
        WriteUtf8(stderr, $"Try '{programName} --help' for more information.\n");
    }

    internal static void WritePosixError(Stream stderr, string programName, string verb, ReadOnlySpan<byte> pathBytes, int errno)
    {
        // gnulib quotearg_colon always uses ASCII straight single quotes for path diagnostics.
        string prefix = $"{programName}: {verb} ";
        string suffix = errno == 0 ? "\n" : $": {NativeIo.DescribePosixError(errno)}\n";
        WriteFormattedError(stderr, prefix, CQuoteOpen, pathBytes, CQuoteClose, suffix);
    }

    internal static void WriteBsdPosixError(Stream stderr, string programName, ReadOnlySpan<byte> pathBytes, int errno)
    {
        string prefix = $"{BsdProgramName(programName)}: ";
        string suffix = errno == 0 ? "\n" : $": {NativeIo.DescribePosixError(errno)}\n";
        WriteFormattedError(stderr, prefix, [], pathBytes, [], suffix);
    }

    private static void WriteFormattedError(
        Stream stderr,
        string prefix,
        ReadOnlySpan<byte> openQuote,
        ReadOnlySpan<byte> pathBytes,
        ReadOnlySpan<byte> closeQuote,
        string suffix)
    {
        int prefixLen = Encoding.UTF8.GetByteCount(prefix);
        int suffixLen = Encoding.UTF8.GetByteCount(suffix);
        int totalLen = prefixLen + openQuote.Length + pathBytes.Length + closeQuote.Length + suffixLen;

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            Span<byte> dest = rented.AsSpan(0, totalLen);
            int pos = Encoding.UTF8.GetBytes(prefix, dest);
            if (!openQuote.IsEmpty)
            {
                openQuote.CopyTo(dest.Slice(pos));
                pos += openQuote.Length;
            }

            pathBytes.CopyTo(dest.Slice(pos));
            pos += pathBytes.Length;
            if (!closeQuote.IsEmpty)
            {
                closeQuote.CopyTo(dest.Slice(pos));
                pos += closeQuote.Length;
            }

            Encoding.UTF8.GetBytes(suffix, dest.Slice(pos));
            stderr.Write(dest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static void WriteUtf8(Stream stream, string text)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int count = Encoding.UTF8.GetBytes(text, rented);
            stream.Write(rented, 0, count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
