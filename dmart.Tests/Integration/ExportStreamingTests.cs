using System.IO.Compression;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// ExportAsync used to build the entire ZipArchive in a MemoryStream, so a 4 GB
// export needed 4 GB of RAM. It now spools to a temp file and hands back a
// delete-on-close reader; ExportToAsync writes straight into a destination the
// caller already has.
//
// A memory ceiling is awkward to assert directly in a test. What these pin
// instead are the observable consequences: the spool file does not leak, the
// direct-write form works against a stream that is not seekable, and the
// archive is still a valid zip with the same contents either way.
public class ExportStreamingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ExportStreamingTests(DmartFactory factory) => _factory = factory;

    // The spool is an implementation detail, but a leaked one fills /tmp on a
    // server that exports nightly — silently, until a disk alert.
    //
    // Asserts on THIS export's spool by name rather than counting
    // dmart-export-*.zip across the whole temp directory. That count was shared
    // state: Path.GetTempPath() is machine-wide, CI runs the sqlite and
    // postgresql legs concurrently on one runner, and a spool created by the
    // other leg between the two counts made this fail with "should be 0 but was
    // 1" — a red build caused by a passing test somewhere else. ExportAsync
    // hands back the DeleteOnClose handle itself, so the exact path is right
    // there and nothing about the check has to be probabilistic.
    [FactIfPg]
    public async Task Spool_File_Is_Deleted_When_The_Returned_Stream_Is_Disposed()
    {
        await WithSpaceAsync(5, async (io, space) =>
        {
            string spoolPath;
            await using (var stream = await io.ExportAsync(space, "/", actor: null))
            {
                // The returned stream IS the spool handle — see
                // ImportExportService.ExportAsync, which opens it with
                // FileOptions.DeleteOnClose and returns it directly.
                spoolPath = stream.ShouldBeOfType<FileStream>().Name;
                // The naming is what an operator greps for when /tmp fills.
                Path.GetFileName(spoolPath).ShouldStartWith("dmart-export-");
                File.Exists(spoolPath).ShouldBeTrue("the spool should exist while the stream is open");
                stream.Length.ShouldBeGreaterThan(0);
            }

            File.Exists(spoolPath).ShouldBeFalse(
                "the spool must be gone once the caller disposes the stream");
        });
    }

    // The XML doc on ExportToAsync claims a non-seekable destination is fine
    // (ZipArchiveMode.Create writes sequentially and falls back to data
    // descriptors). Claims in comments are worth checking — an HTTP response
    // body is exactly such a stream.
    [FactIfPg]
    public async Task Export_Writes_To_A_Non_Seekable_Destination()
    {
        await WithSpaceAsync(5, async (io, space) =>
        {
            using var backing = new MemoryStream();
            await using (var forwardOnly = new ForwardOnlyStream(backing))
                await io.ExportToAsync(forwardOnly, space, "/", actor: null);

            backing.Length.ShouldBeGreaterThan(0);
            backing.Position = 0;
            using var zip = new ZipArchive(backing, ZipArchiveMode.Read);
            zip.Entries.Count.ShouldBeGreaterThan(0, "a non-seekable write must still produce a readable zip");
        });
    }

    // The spooling form and the direct form must produce the same archive —
    // otherwise the convenience overload is a second implementation that can
    // drift.
    [FactIfPg]
    public async Task Both_Forms_Produce_The_Same_Entries()
    {
        await WithSpaceAsync(6, async (io, space) =>
        {
            List<string> viaSpool;
            await using (var stream = await io.ExportAsync(space, "/", actor: null))
                viaSpool = EntryNames(stream);

            using var direct = new MemoryStream();
            await io.ExportToAsync(direct, space, "/", actor: null);
            direct.Position = 0;
            var viaDirect = EntryNames(direct);

            viaDirect.ShouldBe(viaSpool);
            viaDirect.Count(n => n.EndsWith("/meta.content.json", StringComparison.Ordinal))
                     .ShouldBe(6);
        });
    }

    private static List<string> EntryNames(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        return zip.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private async Task WithSpaceAsync(int entryCount, Func<ImportExportService, string, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "exst_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/",
            IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            for (var i = 0; i < entryCount; i++)
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"e{i:D4}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });
            await body(io, space);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    // Write-only, forward-only: what an HTTP response body behaves like.
    // Seeking or reading throws, so a regression that reintroduces a rewind
    // fails loudly here instead of only in production.
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => inner.WriteAsync(buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
