using System.Buffers;
using hyperdu.Core.Native;

namespace hyperdu.Core.Du;

internal delegate void OperandCallback(ReadOnlySpan<byte> operand);

/// <summary>
/// Streams NUL-separated operands for --files0-from=F with exact GNU diagnostics.
/// </summary>
internal static class Files0Reader
{
    internal static bool ReadAll(
        string sourcePath,
        Stream stdin,
        string programName,
        Stream stderr,
        OperandCallback onOperand)
    {
        bool isStdin = sourcePath == "-";
        bool useNativeFd = !isStdin && NativeIo.IsPosix;
        int nativeFd = -1;
        Stream? openedFile = null;

        if (!isStdin)
        {
            if (useNativeFd)
            {
                if (!NativeIo.TryOpenReadOnly(sourcePath, out nativeFd, out int openErrno))
                {
                    Messages.WriteError(stderr, programName, $"cannot open {Messages.QuoteAscii(sourcePath)} for reading: {NativeIo.DescribePosixError(openErrno)}");
                    return false;
                }
            }
            else
            {
                try
                {
                    openedFile = File.OpenRead(sourcePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    int errno = ExcludeMatcher.ExtractSystemErrorCode(ex);
                    Messages.WriteError(stderr, programName, $"cannot open {Messages.QuoteAscii(sourcePath)} for reading: {NativeIo.DescribePosixError(errno)}");
                    return false;
                }
            }
        }

        byte[] readBuf = ArrayPool<byte>.Shared.Rent(65536);
        byte[] recordBuf = new byte[4096];
        int recordLen = 0;
        long recordNumber = 0;
        bool ok = true;

        try
        {
            while (true)
            {
                int n;
                if (useNativeFd)
                {
                    if (!NativeIo.TryReadFd(nativeFd, readBuf, out n, out int readErrno))
                    {
                        Messages.WriteError(stderr, programName, $"{sourcePath}: read error: {NativeIo.DescribePosixError(readErrno)}");
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        n = (openedFile ?? stdin).Read(readBuf, 0, readBuf.Length);
                    }
                    catch (IOException ex)
                    {
                        int errno = ExcludeMatcher.ExtractSystemErrorCode(ex);
                        Messages.WriteError(stderr, programName, $"{sourcePath}: read error: {NativeIo.DescribePosixError(errno)}");
                        return false;
                    }
                }

                if (n == 0)
                    break;

                int pos = 0;
                while (pos < n)
                {
                    int nul = Array.IndexOf(readBuf, (byte)0, pos, n - pos);
                    if (nul < 0)
                    {
                        AppendRecordSlice(ref recordBuf, ref recordLen, readBuf.AsSpan(pos, n - pos));
                        break;
                    }

                    int chunkLen = nul - pos;
                    if (recordLen == 0)
                    {
                        recordNumber++;
                        if (!EmitRecord(readBuf.AsSpan(pos, chunkLen), sourcePath, isStdin, recordNumber, programName, stderr, onOperand))
                            ok = false;
                    }
                    else
                    {
                        AppendRecordSlice(ref recordBuf, ref recordLen, readBuf.AsSpan(pos, chunkLen));
                        recordNumber++;
                        if (!EmitRecord(recordBuf.AsSpan(0, recordLen), sourcePath, isStdin, recordNumber, programName, stderr, onOperand))
                            ok = false;
                        recordLen = 0;
                    }

                    pos = nul + 1;
                }
            }

            if (recordLen > 0)
            {
                recordNumber++;
                if (!EmitRecord(recordBuf.AsSpan(0, recordLen), sourcePath, isStdin, recordNumber, programName, stderr, onOperand))
                    ok = false;
            }

            return ok;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            openedFile?.Dispose();
            NativeIo.CloseFd(nativeFd);
        }
    }

    private static bool EmitRecord(
        ReadOnlySpan<byte> name,
        string sourcePath,
        bool isStdin,
        long recordNumber,
        string programName,
        Stream stderr,
        OperandCallback onOperand)
    {
        if (name.IsEmpty)
        {
            Messages.WriteError(stderr, programName, $"{sourcePath}:{recordNumber}: invalid zero-length file name");
            return false;
        }

        if (isStdin && name.SequenceEqual("-"u8))
        {
            Messages.WriteError(stderr, programName, "when reading file names from stdin, no file name of '-' allowed");
            return false;
        }

        onOperand(name);
        return true;
    }

    private static void AppendRecordSlice(ref byte[] recordBuf, ref int recordLen, ReadOnlySpan<byte> slice)
    {
        int needed = recordLen + slice.Length;
        if (needed > recordBuf.Length)
        {
            int newSize = Math.Max(recordBuf.Length * 2, needed);
            Array.Resize(ref recordBuf, newSize);
        }

        slice.CopyTo(recordBuf.AsSpan(recordLen));
        recordLen = needed;
    }
}
