namespace hyperdu.Core.Native;

/// <summary>
/// Minimal unseekable POSIX file-descriptor Stream for stdin/stdout/stderr in CLI du mode.
/// Issues sequential read(2)/write(2) calls using the kernel descriptor offset directly,
/// avoiding both ConsolePal's lazy signal-handler thread and FileStream's per-instance
/// userspace position tracking (which corrupts redirected stdout+stderr on regular files).
/// </summary>
public sealed unsafe class RawUnixFdStream : Stream
{
    private readonly int _fd;
    private readonly bool _canWrite;

    public RawUnixFdStream(int fd, bool writable)
    {
        _fd = fd;
        _canWrite = writable;
    }

    public override bool CanRead => !_canWrite;
    public override bool CanSeek => false;
    public override bool CanWrite => _canWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (_canWrite)
            throw new NotSupportedException();
        if (count == 0)
            return 0;

        fixed (byte* p = &buffer[offset])
        {
            while (true)
            {
                nint n = Libc.read(_fd, p, (nuint)count);
                if (n >= 0)
                    return (int)n;

                int errno = NativeIo.GetLastErrno();
                if (errno == Libc.EINTR)
                    continue;

                throw new IOException(NativeIo.DescribePosixError(errno), errno);
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_canWrite)
            throw new NotSupportedException();

        int remaining = buffer.Length;
        int pos = 0;
        fixed (byte* basePtr = buffer)
        {
            while (remaining > 0)
            {
                nint written = Libc.write(_fd, basePtr + pos, (nuint)remaining);
                if (written > 0)
                {
                    pos += (int)written;
                    remaining -= (int)written;
                    continue;
                }

                if (written == 0)
                    throw new IOException("Zero-byte write to POSIX file descriptor.", Libc.EIO);

                int errno = NativeIo.GetLastErrno();
                if (errno == Libc.EINTR)
                    continue;

                throw new IOException(NativeIo.DescribePosixError(errno), errno);
            }
        }
    }
}
