namespace EtlTool.Infrastructure.Uploads;

internal sealed class SizeLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private long _bytesRead;

    public SizeLimitedReadStream(Stream inner, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException("The inner stream must be readable.", nameof(inner));
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _inner = inner;
        _maximumBytes = maximumBytes;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        var bytesRead = _inner.Read(buffer, offset, GetPermittedCount(count));
        return RecordRead(bytesRead);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var permittedBuffer = buffer[..GetPermittedCount(buffer.Length)];
        var bytesRead = await _inner.ReadAsync(permittedBuffer, cancellationToken)
            .ConfigureAwait(false);
        return RecordRead(bytesRead);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadArrayAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(
                buffer.AsMemory(offset, GetPermittedCount(count)),
                cancellationToken)
            .ConfigureAwait(false);
        return RecordRead(bytesRead);
    }

    private int GetPermittedCount(int requestedCount)
    {
        if (requestedCount == 0)
        {
            return 0;
        }

        var remainingWithSentinel = (_maximumBytes - _bytesRead) + 1;
        return (int)Math.Min(requestedCount, remainingWithSentinel);
    }

    private int RecordRead(int bytesRead)
    {
        if (bytesRead == 0)
        {
            return 0;
        }

        _bytesRead += bytesRead;

        if (_bytesRead > _maximumBytes)
        {
            throw new UploadSizeLimitExceededException(_maximumBytes);
        }

        return bytesRead;
    }
}
