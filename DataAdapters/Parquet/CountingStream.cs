namespace Dmart.DataAdapters.Parquet;

// Counts bytes written, so the encoder can record page offsets without asking
// the destination where it is.
//
// Parquet's footer stores an absolute file offset per column chunk, and the
// obvious way to get one is `stream.Position`. That quietly requires a seekable
// destination — which an HTTP response body is not, and a multi-GB export
// streamed straight to a client is exactly the case this format exists for.
// Counting instead keeps the writer usable against any write-only stream.
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _written;

    public long BytesWritten => _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        _written += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _written += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        _written++;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // The caller owns the destination; disposing this must not close it, so
    // `inner` is deliberately left alone.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
