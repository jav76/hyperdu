using System.Globalization;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

public enum TimeKind
{
    None,
    Mtime,
    Atime,
    Ctime
}

public enum TimeStyleKind
{
    LongIso,
    FullIso,
    Iso,
    CustomFormat
}

internal readonly record struct FileTimestamp(long Seconds, uint Nanoseconds)
{
    internal static FileTimestamp Max(FileTimestamp a, FileTimestamp b) =>
        b.Seconds > a.Seconds || (b.Seconds == a.Seconds && b.Nanoseconds > a.Nanoseconds) ? b : a;
}

/// <summary>
/// Parses GNU --time/--time-style/TIME_STYLE options and formats timestamps with nanosecond %N support.
/// </summary>
internal static class TimeFormat
{
    private enum ArgMatchStatus
    {
        Ok,
        Invalid,
        Ambiguous
    }

    private static readonly (string Name, TimeKind Kind)[] TimeKindCandidates =
    [
        ("atime", TimeKind.Atime),
        ("access", TimeKind.Atime),
        ("use", TimeKind.Atime),
        ("ctime", TimeKind.Ctime),
        ("status", TimeKind.Ctime)
    ];

    private static readonly (string Name, TimeStyleKind Style)[] TimeStyleCandidates =
    [
        ("full-iso", TimeStyleKind.FullIso),
        ("long-iso", TimeStyleKind.LongIso),
        ("iso", TimeStyleKind.Iso)
    ];

    internal static bool TryParseTimeKind(string arg, string programName, Stream stderr, out TimeKind kind)
    {
        ArgMatchStatus status = TryMatchArg(TimeKindCandidates, arg, out kind);
        if (status == ArgMatchStatus.Ok)
            return true;

        string kindLabel = status == ArgMatchStatus.Ambiguous ? "ambiguous" : "invalid";
        Messages.WriteError(stderr, programName, $"{kindLabel} argument {Messages.Quote(arg)} for {Messages.Quote("--time")}");
        Messages.WriteUtf8(
            stderr,
            $"Valid arguments are:\n" +
            $"  - {Messages.Quote("atime")}, {Messages.Quote("access")}, {Messages.Quote("use")}\n" +
            $"  - {Messages.Quote("ctime")}, {Messages.Quote("status")}\n");
        Messages.WriteTryHelp(stderr, programName);
        return false;
    }

    internal static bool TryResolveTimeStyle(
        string? cliStyle,
        string programName,
        Stream stderr,
        out TimeStyleKind styleKind,
        out string? customFormat)
    {
        customFormat = null;
        string raw = cliStyle ?? StripPosixEnvPrefix(Environment.GetEnvironmentVariable("TIME_STYLE")) ?? "long-iso";
        if (raw.StartsWith('+'))
        {
            styleKind = TimeStyleKind.CustomFormat;
            customFormat = raw[1..];
            return true;
        }

        ArgMatchStatus status = TryMatchArg(TimeStyleCandidates, raw, out styleKind);
        if (status == ArgMatchStatus.Ok)
            return true;

        string kindLabel = status == ArgMatchStatus.Ambiguous ? "ambiguous" : "invalid";
        Messages.WriteError(stderr, programName, $"{kindLabel} argument {Messages.Quote(raw)} for {Messages.Quote("time style")}");
        Messages.WriteUtf8(
            stderr,
            $"Valid arguments are:\n" +
            $"  - {Messages.Quote("full-iso")}\n" +
            $"  - {Messages.Quote("long-iso")}\n" +
            $"  - {Messages.Quote("iso")}\n");
        Messages.WriteTryHelp(stderr, programName);
        return false;
    }

    internal static Formatter CreateFormatter(DuOptions options, string programName, Stream stderr, Action flushStdout)
    {
        string pattern = options.TimeStyleKind switch
        {
            TimeStyleKind.FullIso => "%Y-%m-%d %H:%M:%S.%N %z",
            TimeStyleKind.Iso => "%Y-%m-%d",
            TimeStyleKind.CustomFormat => options.CustomTimeFormat ?? string.Empty,
            _ => "%Y-%m-%d %H:%M"
        };

        return new Formatter(pattern, programName, stderr, flushStdout);
    }

    private static string? StripPosixEnvPrefix(string? envValue)
    {
        if (envValue == null)
            return null;

        while (envValue.StartsWith("posix-", StringComparison.Ordinal))
            envValue = envValue[6..];

        return envValue;
    }

    private static ArgMatchStatus TryMatchArg<T>(
        (string Name, T Value)[] candidates,
        string input,
        out T value) where T : struct, Enum
    {
        foreach ((string name, T candVal) in candidates)
        {
            if (string.Equals(name, input, StringComparison.Ordinal))
            {
                value = candVal;
                return ArgMatchStatus.Ok;
            }
        }

        bool found = false;
        T matchedVal = default;
        foreach ((string name, T candVal) in candidates)
        {
            if (!name.StartsWith(input, StringComparison.Ordinal))
                continue;

            if (!found)
            {
                found = true;
                matchedVal = candVal;
            }
            else if (!EqualityComparer<T>.Default.Equals(matchedVal, candVal))
            {
                value = default;
                return ArgMatchStatus.Ambiguous;
            }
        }

        value = matchedVal;
        return found ? ArgMatchStatus.Ok : ArgMatchStatus.Invalid;
    }

    internal sealed class Formatter : IDisposable
    {
        private readonly string _programName;
        private readonly Stream _stderr;
        private readonly Action _flushStdout;
        private readonly byte[] _rawFormatUtf8;
        private readonly bool _hasNanoSpec;
        private readonly nint _timeLocale;

        private byte[] _sentinelFormatBuf;
        private byte[] _outputBuf = new byte[256];

        internal Formatter(string pattern, string programName, Stream stderr, Action flushStdout)
        {
            _programName = programName;
            _stderr = stderr;
            _flushStdout = flushStdout;
            _rawFormatUtf8 = Encoding.UTF8.GetBytes(pattern);
            _hasNanoSpec = ContainsNanosecondSpecifier(_rawFormatUtf8);

            // Sentinel byte 'a' prevents strftime returning 0 when formatted output is empty.
            _sentinelFormatBuf = new byte[_rawFormatUtf8.Length + 2];
            if (!_hasNanoSpec)
            {
                _sentinelFormatBuf[0] = (byte)'a';
                _rawFormatUtf8.CopyTo(_sentinelFormatBuf.AsSpan(1));
                _sentinelFormatBuf[_rawFormatUtf8.Length + 1] = 0;
            }

            if (NativeIo.IsSupported)
            {
                NativeIo.SyncTimezone(Environment.GetEnvironmentVariable("TZ"));
                string? timeLocale = Messages.GetEffectiveCategoryLocale("LC_TIME");
                _timeLocale = NativeIo.CreateTimeLocaleHandle(Messages.IsCPosixLocale(timeLocale) ? "C" : timeLocale);
            }
        }

        public ReadOnlySpan<byte> Format(in FileTimestamp ts)
        {
            if (!NativeIo.IsSupported)
                return FormatManagedFallback(in ts);

            ReadOnlySpan<byte> fmt = _hasNanoSpec ? BuildExpandedFormat(ts.Nanoseconds) : _sentinelFormatBuf;

            while (true)
            {
                if (NativeIo.TryFormatLocalTime(ts.Seconds, fmt, _outputBuf, _timeLocale, out int written, out bool outOfRange))
                    return _outputBuf.AsSpan(0, written);

                if (outOfRange)
                {
                    _flushStdout();
                    string secText = ts.Seconds.ToString(CultureInfo.InvariantCulture);
                    Messages.WriteError(_stderr, _programName, $"time {Messages.Quote(secText)} is out of range");
                    int secBytes = Encoding.ASCII.GetBytes(secText, _outputBuf);
                    return _outputBuf.AsSpan(0, secBytes);
                }

                if (_outputBuf.Length >= 65536)
                    return [];

                Array.Resize(ref _outputBuf, _outputBuf.Length * 2);
            }
        }

        private ReadOnlySpan<byte> BuildExpandedFormat(uint nanoseconds)
        {
            Span<byte> nineDigits = stackalloc byte[9];
            uint n = Math.Min(nanoseconds, 999_999_999u);
            for (int d = 8; d >= 0; d--)
            {
                nineDigits[d] = (byte)('0' + (int)(n % 10));
                n /= 10;
            }

            int pos = 0;
            void AppendByte(byte b)
            {
                if (pos + 2 > _sentinelFormatBuf.Length)
                    Array.Resize(ref _sentinelFormatBuf, Math.Max(_sentinelFormatBuf.Length * 2, pos + 16));
                _sentinelFormatBuf[pos++] = b;
            }

            AppendByte((byte)'a');
            ReadOnlySpan<byte> src = _rawFormatUtf8;
            for (int i = 0; i < src.Length; i++)
            {
                byte b = src[i];
                if (b != (byte)'%' || i + 1 >= src.Length)
                {
                    AppendByte(b);
                    continue;
                }

                if (src[i + 1] == (byte)'%')
                {
                    AppendByte((byte)'%');
                    AppendByte((byte)'%');
                    i++;
                    continue;
                }

                int j = i + 1;
                int width = 0;
                bool hasDigits = false;
                while (j < src.Length && src[j] is >= (byte)'0' and <= (byte)'9')
                {
                    hasDigits = true;
                    if (width < 1000)
                        width = width * 10 + (src[j] - '0');
                    j++;
                }

                if (j < src.Length && src[j] == (byte)'N')
                {
                    int targetWidth = (!hasDigits || width == 0) ? 9 : width;
                    int copyCount = Math.Min(targetWidth, 9);
                    for (int k = 0; k < copyCount; k++)
                        AppendByte(nineDigits[k]);
                    for (int k = copyCount; k < targetWidth; k++)
                        AppendByte((byte)'0');
                    i = j;
                }
                else
                {
                    AppendByte(b);
                }
            }

            _sentinelFormatBuf[pos++] = 0;
            return _sentinelFormatBuf.AsSpan(0, pos);
        }

        private ReadOnlySpan<byte> FormatManagedFallback(in FileTimestamp ts)
        {
            DateTimeOffset dto = DateTimeOffset.FromUnixTimeSeconds(ts.Seconds).ToLocalTime();
            string text = dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            int max = Encoding.UTF8.GetMaxByteCount(text.Length);
            if (max > _outputBuf.Length)
                Array.Resize(ref _outputBuf, max);
            int len = Encoding.UTF8.GetBytes(text, _outputBuf);
            return _outputBuf.AsSpan(0, len);
        }

        private static bool ContainsNanosecondSpecifier(ReadOnlySpan<byte> utf8)
        {
            for (int i = 0; i < utf8.Length - 1; i++)
            {
                if (utf8[i] != (byte)'%')
                    continue;
                if (utf8[i + 1] == (byte)'%')
                {
                    i++;
                    continue;
                }

                int j = i + 1;
                while (j < utf8.Length && utf8[j] is >= (byte)'0' and <= (byte)'9')
                    j++;
                if (j < utf8.Length && utf8[j] == (byte)'N')
                    return true;
            }

            return false;
        }

        public void Dispose() => NativeIo.FreeLocaleHandle(_timeLocale);
    }
}
