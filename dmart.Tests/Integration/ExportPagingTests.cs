using System.IO.Compression;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The export used to run ONE query with LIMIT 100_000 and drop everything past
// it in silence: a larger space exported partially, reported success, and
// produced an archive that looked complete. Silent truncation of a backup is
// only ever discovered at restore.
//
// These pin the fix at the seam that matters — the number of entries that come
// out the other end — rather than at the query. `ImportExportService.ExportPageSize`
// is lowered so the multi-page path runs on a handful of rows; proving the
// original bug at the real page size would need 100,001 entries.
public class ExportPagingTests : IClassFixture<DmartFactory>, IDisposable
{
    private readonly DmartFactory _factory;
    private readonly int _originalPageSize = ImportExportService.ExportPageSize;

    public ExportPagingTests(DmartFactory factory) => _factory = factory;

    public void Dispose()
    {
        ImportExportService.ExportPageSize = _originalPageSize;
        GC.SuppressFinalize(this);
    }

    // The regression itself: more rows than fit in one page must all be
    // exported. With the old single-capped-query code this returns exactly one
    // page's worth and reports success.
    [FactIfPg]
    public async Task Export_Emits_Every_Entry_Across_Page_Boundaries()
    {
        ImportExportService.ExportPageSize = 7;   // 25 entries => 4 pages, last one short

        await WithSpaceAsync(25, async (io, space, shortnames) =>
        {
            var names = await ExportedEntryShortnamesAsync(io, space);

            names.Count.ShouldBe(25,
                "every entry must be exported, not just the first page");
            names.OrderBy(x => x, StringComparer.Ordinal)
                 .ShouldBe(shortnames.OrderBy(x => x, StringComparer.Ordinal));
        });
    }

    // A total multiple of the page size is the off-by-one case: the loop must
    // ask once more and stop on the empty page, not stop early or spin.
    [FactIfPg]
    public async Task Export_Handles_An_Exact_Multiple_Of_The_Page_Size()
    {
        ImportExportService.ExportPageSize = 5;   // 20 entries => exactly 4 full pages

        await WithSpaceAsync(20, async (io, space, shortnames) =>
        {
            var names = await ExportedEntryShortnamesAsync(io, space);
            names.Count.ShouldBe(20);
            names.OrderBy(x => x, StringComparer.Ordinal)
                 .ShouldBe(shortnames.OrderBy(x => x, StringComparer.Ordinal));
        });
    }

    // Nothing duplicated. Paging with an unstable sort would repeat rows as the
    // window advanced, which is the same class of silent corruption the cap
    // caused — this is what the forced `uuid` ordering is there to prevent.
    [FactIfPg]
    public async Task Export_Does_Not_Duplicate_Rows_Across_Pages()
    {
        ImportExportService.ExportPageSize = 3;

        await WithSpaceAsync(11, async (io, space, _) =>
        {
            var names = await ExportedEntryShortnamesAsync(io, space);
            names.Count.ShouldBe(names.Distinct(StringComparer.Ordinal).Count(),
                "a row appearing twice means the page window moved without a total order");
        });
    }

    // A caller-supplied limit is still honoured exactly — sampling an export is
    // a legitimate request, and the paging fix must not turn every bounded
    // export into a full one.
    [FactIfPg]
    public async Task Caller_Supplied_Limit_Is_Still_Honoured()
    {
        ImportExportService.ExportPageSize = 4;

        await WithSpaceAsync(15, async (io, space, _) =>
        {
            var names = await ExportedEntryShortnamesAsync(io, space, limit: 6);
            names.Count.ShouldBe(6, "an explicit limit must cap the export");
        });
    }

    // ====================================================================

    private async Task WithSpaceAsync(
        int entryCount, Func<ImportExportService, string, List<string>, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "expg_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/",
            IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            var shortnames = new List<string>();
            for (var i = 0; i < entryCount; i++)
            {
                var sn = $"e{i:D4}";
                shortnames.Add(sn);
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = sn,
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });
            }
            await body(io, space, shortnames);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    // Read the produced zip back and pull out the entry shortnames, which is
    // what an operator actually restores from — asserting on the archive rather
    // than on the query keeps the test honest about the end-to-end result.
    private static async Task<List<string>> ExportedEntryShortnamesAsync(
        ImportExportService io, string space, int limit = 0)
    {
        await using var stream = limit > 0
            ? await io.ExportAsync(new Dmart.Models.Api.Query
            {
                Type = QueryType.Search, SpaceName = space, Subpath = "/",
                FilterSchemaNames = new(), Limit = limit, RetrieveJsonPayload = true,
            }, actor: null)
            : await io.ExportAsync(space, "/", actor: null);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries
            .Where(e => e.FullName.EndsWith("/meta.content.json", StringComparison.Ordinal))
            .Select(e => e.FullName.Split('/')[^2])
            .ToList();
    }
}
