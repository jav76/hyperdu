using System.Buffers;
using System.Text;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

/// <summary>
/// Implements gnulib exclude semantics (fnmatch without FNM_PATHNAME or FNM_PERIOD,
/// matched against the full path and every suffix following '/').
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly List<byte[]> _patterns = [];

    public bool IsEmpty => _patterns.Count == 0;
    public bool MatchBasenameOnly { get; set; }

    public void AddPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return;

        _patterns.Add(Encoding.UTF8.GetBytes(pattern));
    }

    public void AddPatternBytes(ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty)
            return;

        _patterns.Add(pattern.ToArray());
    }

    internal void CopyFrom(ExcludeMatcher source)
    {
        MatchBasenameOnly = source.MatchBasenameOnly;
        foreach (byte[] pattern in source._patterns)
            _patterns.Add((byte[])pattern.Clone());
    }

    internal bool IsExcludedManagedPath(string path) =>
        IsExcludedManagedParts(path, []);

    internal bool IsExcludedManagedParts(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName)
    {
        if (IsEmpty || (directory.IsEmpty && fileName.IsEmpty))
            return false;

        bool needsSep = !directory.IsEmpty && !fileName.IsEmpty &&
                        directory[^1] != Path.DirectorySeparatorChar &&
                        directory[^1] != Path.AltDirectorySeparatorChar;
        int maxBytes = Encoding.UTF8.GetMaxByteCount(directory.Length) +
                       (needsSep ? 1 : 0) +
                       Encoding.UTF8.GetMaxByteCount(fileName.Length);

        byte[]? rented = maxBytes > 512 ? ArrayPool<byte>.Shared.Rent(maxBytes) : null;
        Span<byte> buf = rented ?? stackalloc byte[512];
        try
        {
            int pos = !directory.IsEmpty ? Encoding.UTF8.GetBytes(directory, buf) : 0;
            if (needsSep)
                buf[pos++] = (byte)'/';
            if (!fileName.IsEmpty)
                pos += Encoding.UTF8.GetBytes(fileName, buf[pos..]);
            if (OperatingSystem.IsWindows())
                buf[..pos].Replace((byte)'\\', (byte)'/');
            return IsExcluded(buf[..pos]);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal bool TryAddFromFile(string filePath, Stream stdin, string programName, Stream stderr)
    {
        if (!TryReadPatternFile(filePath, stdin, out byte[] data, out int errno))
        {
            Messages.WriteError(stderr, programName, $"{filePath}: {NativeIo.DescribePosixError(errno)}");
            Messages.WriteTryHelp(stderr, programName);
            return false;
        }

        ReadOnlySpan<byte> span = data;
        while (!span.IsEmpty)
        {
            int nl = span.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl >= 0 ? span[..nl] : span;
            if (!line.IsEmpty)
                AddPatternBytes(line);

            if (nl < 0)
                break;
            span = span[(nl + 1)..];
        }

        return true;
    }

    private static bool TryReadPatternFile(string filePath, Stream stdin, out byte[] data, out int errno)
    {
        if (filePath == "-")
        {
            using MemoryStream ms = new();
            stdin.CopyTo(ms);
            data = ms.ToArray();
            errno = 0;
            return true;
        }

        if (NativeIo.IsSupported)
        {
            if (!NativeIo.TryOpenReadOnly(filePath, out int fd, out errno))
            {
                data = [];
                return false;
            }

            try
            {
                using MemoryStream ms = new();
                byte[] buf = ArrayPool<byte>.Shared.Rent(16384);
                try
                {
                    while (true)
                    {
                        if (!NativeIo.TryReadFd(fd, buf, out int bytesRead, out errno))
                        {
                            data = [];
                            return false;
                        }

                        if (bytesRead == 0)
                            break;

                        ms.Write(buf, 0, bytesRead);
                    }

                    data = ms.ToArray();
                    return true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            finally
            {
                NativeIo.CloseFd(fd);
            }
        }

        try
        {
            data = File.ReadAllBytes(filePath);
            errno = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errno = ExtractPosixErrno(ex);
            data = [];
            return false;
        }
    }

    public bool IsExcluded(ReadOnlySpan<byte> path)
    {
        if (_patterns.Count == 0 || path.IsEmpty)
            return false;

        if (MatchBasenameOnly)
            return IsExcludedChildBasename(DuAccounting.ExtractBasename(path));

        foreach (byte[] pattern in _patterns)
        {
            if (MatchesPathOrSuffix(pattern, path))
                return true;
        }

        return false;
    }

    public bool IsExcludedChildBasename(ReadOnlySpan<byte> childBasename)
    {
        if (_patterns.Count == 0)
            return false;

        foreach (byte[] pattern in _patterns)
        {
            if (Fnmatch(pattern, childBasename))
                return true;
        }

        return false;
    }

    private static bool MatchesPathOrSuffix(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> path)
    {
        if (Fnmatch(pattern, path))
            return true;

        int pos = 0;
        while (pos < path.Length)
        {
            int slash = path[pos..].IndexOf((byte)'/');
            if (slash < 0)
                break;

            int suffixStart = pos + slash + 1;
            if (suffixStart < path.Length && Fnmatch(pattern, path[suffixStart..]))
                return true;

            pos = suffixStart;
        }

        return false;
    }

    internal static bool Fnmatch(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text)
    {
        int p = 0;
        int t = 0;
        int starP = -1;
        int starT = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length)
            {
                byte pc = pattern[p];
                if (pc == (byte)'*')
                {
                    while (p < pattern.Length && pattern[p] == (byte)'*')
                        p++;
                    starP = p - 1;
                    starT = t;
                    continue;
                }

                if (pc == (byte)'?')
                {
                    p++;
                    t++;
                    continue;
                }

                if (pc == (byte)'\\')
                {
                    byte literal = p + 1 < pattern.Length ? pattern[p + 1] : (byte)'\\';
                    int advance = p + 1 < pattern.Length ? 2 : 1;
                    if (text[t] == literal)
                    {
                        p += advance;
                        t++;
                        continue;
                    }
                }
                else if (pc == (byte)'[')
                {
                    if (TryMatchBracket(pattern[p..], text[t], out int consumed, out bool matched))
                    {
                        if (matched)
                        {
                            p += consumed;
                            t++;
                            continue;
                        }
                    }
                    else if (text[t] == (byte)'[')
                    {
                        p++;
                        t++;
                        continue;
                    }
                }
                else if (pc == text[t])
                {
                    p++;
                    t++;
                    continue;
                }
            }

            if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == (byte)'*')
            p++;

        return p == pattern.Length;
    }

    private static bool TryMatchBracket(ReadOnlySpan<byte> pat, byte ch, out int consumed, out bool matched)
    {
        consumed = 0;
        matched = false;
        int i = 1;
        if (i >= pat.Length)
            return false;

        bool negate = false;
        if (pat[i] == (byte)'!' || pat[i] == (byte)'^')
        {
            negate = true;
            i++;
        }

        bool anyMatch = false;
        bool first = true;
        while (i < pat.Length)
        {
            byte c = pat[i];
            if (!first && c == (byte)']')
            {
                consumed = i + 1;
                matched = negate ? !anyMatch : anyMatch;
                return true;
            }

            first = false;
            i++;

            if (i + 1 < pat.Length && pat[i] == (byte)'-' && pat[i + 1] != (byte)']')
            {
                byte end = pat[i + 1];
                i += 2;
                if (c <= ch && ch <= end)
                    anyMatch = true;
            }
            else if (c == ch)
            {
                anyMatch = true;
            }
        }

        return false;
    }

    internal static int ExtractPosixErrno(Exception ex)
    {
        if (ex.HResult is > 0 and < 4096)
            return ex.HResult;

        return ex switch
        {
            FileNotFoundException => Libc.ENOENT,
            DirectoryNotFoundException => Libc.ENOENT,
            UnauthorizedAccessException => Libc.EACCES,
            _ => Libc.EIO
        };
    }
}
