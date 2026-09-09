namespace Dmart.DataAdapters.Sql;

// A seekable, read-only Stream over one attachment's `media` column, backed by
// substr() reads rather than a materialised byte[].
//
// This exists so /payload can hand a Stream to Results.File and let ASP.NET do
// the Range arithmetic — 206, Content-Range, If-Range, 416 — while only the
// bytes actually asked for come out of the database. Handing it a byte[]
// instead means every seek re-reads the entire blob: a browser scrubbing a
// 50MB video issues dozens of Range requests, and each one shipped 50MB from
// PostgreSQL and allocated a 50MB large-object-heap array to return ~100KB.
//
// Reads are chunked rather than passed straight through because the copy loop
// above us uses a 64KB buffer: unbuffered, a full 50MB download would become
// ~800 round-trips. One chunk per ChunkSize bytes keeps that to ~50 while
// keeping peak memory at one chunk instead of one whole attachment.
internal sealed class AttachmentMediaStream : Stream
{
    // 1 MiB: two orders of magnitude below the 50MB upload cap, so peak memory
    // is bounded well under the LOH threshold's practical pain, and large
    // enough that a full read is tens of queries rather than hundreds.
    private const int ChunkSize = 1024 * 1024;

    private readonly AttachmentRepository _repo;
    private readonly string _space;
    private readonly string _subpath;
    private readonly string _shortname;

    // The currently loaded window. `_bufferStart` is its offset in the blob.
    private byte[] _buffer = [];
    private long _bufferStart = -1;

    public AttachmentMediaStream(
        AttachmentRepository repo, string space, string subpath, string shortname, long length)
    {
        _repo = repo;
        _space = space;
        _subpath = subpath;
        _shortname = shortname;
        Length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;   // required, or Results.File ignores Range entirely
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position { get; set; }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> destination, CancellationToken ct = default)
    {
        if (Position >= Length || destination.IsEmpty) return 0;

        // Refill when the position falls outside the loaded window — which a
        // seek backwards does too, so this stays correct for out-of-order ranges.
        if (_bufferStart < 0 || Position < _bufferStart || Position >= _bufferStart + _buffer.Length)
        {
            var want = (int)Math.Min(ChunkSize, Length - Position);
            _buffer = await _repo.ReadMediaSliceAsync(_space, _subpath, _shortname, Position, want, ct);
            _bufferStart = Position;
            // A row deleted mid-stream (or a short read) yields nothing more.
            if (_buffer.Length == 0) return 0;
        }

        var offsetInBuffer = (int)(Position - _bufferStart);
        var n = Math.Min(destination.Length, _buffer.Length - offsetInBuffer);
        _buffer.AsSpan(offsetInBuffer, n).CopyTo(destination.Span);
        Position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    // Synchronous reads are not on any path we serve — ASP.NET's response body
    // copy is async, and Kestrel disallows sync IO by default — but Stream
    // requires the override, and blocking is a truer answer than throwing for a
    // caller that legitimately has no async context.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return Position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
