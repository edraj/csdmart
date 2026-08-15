using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The COPY-based export reader must be INDISTINGUISHABLE from the paged one.
//
// It is an optimisation, so the only thing that makes it safe is that it
// produces the same rows with the same values — a fast reader that quietly
// drops a null, mis-orders a column or truncates a JSON blob would corrupt
// every backup taken through it, silently, because nothing downstream
// re-reads the source to compare.
//
// So these tests do not check the COPY path against hand-written expectations.
// They check it against the paged path, field by field, on rows built to
// exercise the shapes the binary decoder can get wrong: nulls in every
// nullable column, empty and populated JSON, an empty text[], and a NULL
// boolean (is_open), whose skip-vs-read branch is its own code path.
public class CopyExportReaderTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public CopyExportReaderTests(DmartFactory factory) => _factory = factory;

    [FactIfPostgresOnly]
    public async Task Streams_The_Same_Rows_And_Values_As_The_Paged_Reader()
    {
        var (repo, space) = await SeedAsync();
        try
        {
            var paged = await repo.ListForExportPagedAsync(space, null, null, 10_000, 0);
            var streamed = new List<EntryRepository.EntryExportRow>();
            await foreach (var r in repo.StreamForExportAsync(space, null, null))
                streamed.Add(r);

            streamed.Count.ShouldBe(paged.Count);
            streamed.Count.ShouldBeGreaterThan(0, "the fixture must actually produce rows");

            for (var i = 0; i < paged.Count; i++)
            {
                // QueryPolicies is compared separately and by SEQUENCE. The
                // record's generated Equals compares List<string> by reference,
                // so two lists with identical contents are unequal and every
                // row would "differ" — a failure that says nothing about the
                // reader. Null-out the list, compare the rest by value, then
                // compare the sequences.
                (streamed[i] with { QueryPolicies = null })
                    .ShouldBe(paged[i] with { QueryPolicies = null },
                              $"row {i} differs between the two readers");
                (streamed[i].QueryPolicies ?? []).ShouldBe(
                    paged[i].QueryPolicies ?? [], $"row {i}: query_policies differ");
            }
        }
        finally { await CleanupAsync(space); }
    }

    // `since` and `subpath` are filters, and a fast reader that applied them
    // differently would export the wrong SET of rows rather than wrong values —
    // just as damaging and harder to notice.
    [FactIfPostgresOnly]
    public async Task Applies_Subpath_And_Since_Filters_Identically()
    {
        var (repo, space) = await SeedAsync();
        try
        {
            foreach (var (since, subpath) in new (DateTime?, string?)[]
                     {
                         (null, "/docs"),
                         (DateTime.Now.AddDays(-1), null),
                         (DateTime.Now.AddDays(-1), "/docs"),
                         (DateTime.Now.AddYears(1), null),   // matches nothing
                     })
            {
                var paged = await repo.ListForExportPagedAsync(space, since, subpath, 10_000, 0);
                var streamed = new List<EntryRepository.EntryExportRow>();
                await foreach (var r in repo.StreamForExportAsync(space, since, subpath))
                    streamed.Add(r);

                streamed.Select(r => r.Shortname).ShouldBe(
                    paged.Select(r => r.Shortname),
                    $"since={since:o} subpath={subpath} selected a different set");
            }
        }
        finally { await CleanupAsync(space); }
    }

    // COPY takes no parameters, so the filters are inlined into the statement.
    // A space name carrying a quote must not be able to alter it.
    [FactIfPostgresOnly]
    public async Task A_Quote_In_The_Space_Name_Cannot_Break_The_Statement()
    {
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        _factory.CreateClient();

        // No such space, so the correct answer is zero rows — the wrong answer
        // is a SQL error, which is what an unescaped quote would produce.
        var rows = new List<EntryRepository.EntryExportRow>();
        await foreach (var r in repo.StreamForExportAsync("no'such--space", null, null))
            rows.Add(r);
        rows.ShouldBeEmpty();
    }

    // The guard is what lets the reader trust the binary stream. On an
    // unmodified schema it must report NO mismatch, or every export silently
    // falls back to the slow path and the optimisation is dead code.
    [FactIfPostgresOnly]
    public async Task The_Schema_Guard_Passes_On_The_Real_Schema()
    {
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        _factory.CreateClient();
        (await repo.ExportSchemaMismatchAsync()).ShouldBeNull();
    }

    // And it must actually notice a type change — the one schema edit that
    // keeps the column list aligned while changing the bytes on the wire.
    [FactIfPostgresOnly]
    public async Task The_Schema_Guard_Notices_A_Column_Type_Change()
    {
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        var dbf = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        _factory.CreateClient();

        await using (var conn = await dbf.OpenAsync())
        await using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE entries ALTER COLUMN slug TYPE varchar(255)";
            await alter.ExecuteNonQueryAsync();
        }
        try
        {
            var mismatch = await repo.ExportSchemaMismatchAsync();
            mismatch.ShouldNotBeNull("a changed column type must be caught");
            mismatch!.ShouldContain("slug");
        }
        finally
        {
            await using var conn = await dbf.OpenAsync();
            await using var revert = conn.CreateCommand();
            revert.CommandText = "ALTER TABLE entries ALTER COLUMN slug TYPE text";
            await revert.ExecuteNonQueryAsync();
        }
    }

    // ---- fixture ----

    private async Task<(EntryRepository, string)> SeedAsync()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var repo = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "cpy_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        // Row 0: every nullable column null, every list empty.
        await repo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "bare",
            SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
            IsActive = false, OwnerShortname = "dmart",
        });

        // Row 1: every nullable column populated, in a subpath, with the JSON
        // shapes the decoder has to hand back verbatim.
        await repo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "full",
            SpaceName = space, Subpath = "/docs", ResourceType = ResourceType.Ticket,
            IsActive = true, OwnerShortname = "dmart",
            Slug = "a-slug", Tags = ["x", "y"],
            Displayname = new Translation { En = "Full", Ar = "كامل" },
            Description = new Translation { En = "has every field" },
            State = "open", IsOpen = true, WorkflowShortname = "wf",
            ResolutionReason = "done",
            Collaborators = new Dictionary<string, string> { ["reviewer"] = "dmart" },
            QueryPolicies = ["p1", "p2"],
        });

        // Row 2: is_open NULL specifically — its own branch in the decoder.
        await repo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "noopen",
            SpaceName = space, Subpath = "/docs", ResourceType = ResourceType.Ticket,
            IsActive = true, OwnerShortname = "dmart", IsOpen = null,
            Tags = [],
        });

        return (repo, space);
    }

    private async Task CleanupAsync(string space)
    {
        try { await _factory.Services.GetRequiredService<SpaceRepository>().DeleteAsync(space); }
        catch { /* best effort */ }
    }
}
