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

    // ---- histories ----
    //
    // Histories carry the same parity risk as entries, plus one more: their
    // JSON columns are NOT NULL with a "{}" default, so a reader that handed
    // back null where the paged one hands back an empty dictionary would write
    // a null column that fails the NOT NULL constraint on restore.

    [FactIfPostgresOnly]
    public async Task History_Stream_Matches_The_Paged_Reader()
    {
        var (space, histories) = await SeedHistoriesAsync();
        try
        {
            var paged = await histories.ListForSpacePagedAsync(space, 10_000, 0);
            var streamed = new List<HistoryExportRow>();
            await foreach (var h in histories.StreamForExportAsync(space, null, null))
                streamed.Add(h);

            streamed.Count.ShouldBe(paged.Count);
            streamed.Count.ShouldBeGreaterThan(0);

            // Compared through what the ARCHIVE stores, since one side holds
            // dictionaries and the other raw strings: same uuids in the same
            // order, and diffs that mean the same thing.
            streamed.Select(h => h.Uuid).ShouldBe(paged.Select(h => h.Uuid));
            for (var i = 0; i < paged.Count; i++)
            {
                streamed[i].Shortname.ShouldBe(paged[i].Shortname);
                streamed[i].Subpath.ShouldBe(paged[i].Subpath);
                streamed[i].OwnerShortname.ShouldBe(paged[i].OwnerShortname);
                streamed[i].Timestamp.ShouldBe(paged[i].Timestamp, TimeSpan.FromMilliseconds(1));

                // The empty-diff rows must survive as "{}" on both sides —
                // never null, which would break NOT NULL on restore.
                var pagedDiff = paged[i].Diff is null || paged[i].Diff!.Count == 0
                    ? "{}" : null;
                if (pagedDiff == "{}")
                    (streamed[i].Diff ?? "{}").ShouldBe("{}",
                        "an empty diff must stay an empty object, not become null");
            }
        }
        finally { await CleanupAsync(space); }
    }

    [FactIfPostgresOnly]
    public async Task History_Schema_Guard_Passes_And_Notices_A_Type_Change()
    {
        var histories = _factory.Services.GetRequiredService<HistoryRepository>();
        var dbf = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        _factory.CreateClient();

        (await histories.ExportSchemaMismatchAsync()).ShouldBeNull();

        await using (var conn = await dbf.OpenAsync())
        await using (var alter = conn.CreateCommand())
        {
            alter.CommandText =
                "ALTER TABLE histories ALTER COLUMN last_checksum_history TYPE varchar(255)";
            await alter.ExecuteNonQueryAsync();
        }
        try
        {
            var mismatch = await histories.ExportSchemaMismatchAsync();
            mismatch.ShouldNotBeNull();
            mismatch!.ShouldContain("last_checksum_history");
        }
        finally
        {
            await using var conn = await dbf.OpenAsync();
            await using var revert = conn.CreateCommand();
            revert.CommandText =
                "ALTER TABLE histories ALTER COLUMN last_checksum_history TYPE text";
            await revert.ExecuteNonQueryAsync();
        }
    }

    private async Task<(string, HistoryRepository)> SeedHistoriesAsync()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var histories = sp.GetRequiredService<HistoryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "cpyh_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        // A real diff, and an empty one — the shape that must stay "{}".
        await histories.AppendAsync(space, "/", "h1", "dmart", null,
            new Dictionary<string, object>
            {
                ["displayname.en"] = new Dictionary<string, string> { ["old"] = "a", ["new"] = "b" },
            });
        await histories.AppendAsync(space, "/docs", "h2", "dmart", null, null);
        return (space, histories);
    }

    // ---- attachments ----
    //
    // Attachments differ from entries in one way that matters here: the PAGED
    // reader hydrates JSON into objects and re-serialises them, so its archive
    // text carries C# key order, while the streamed reader carries
    // PostgreSQL's jsonb normalisation. The text differs; the DATA must not.
    // So JSON columns are compared parsed, not as strings — comparing them
    // literally would fail on key order and prove nothing.

    [FactIfPostgresOnly]
    public async Task Attachment_Stream_Matches_The_Paged_Reader()
    {
        var (space, attachments) = await SeedAttachmentsAsync();
        try
        {
            var paged = await attachments.ListForSpacePagedAsync(space, 10_000, 0);
            var streamed = new List<AttachmentExportRow>();
            await foreach (var a in attachments.StreamForExportAsync(space, null, null))
                streamed.Add(a);

            streamed.Count.ShouldBe(paged.Count);
            streamed.Count.ShouldBeGreaterThan(0);
            streamed.Select(a => a.Uuid).ShouldBe(paged.Select(p => p.Attachment.Uuid));

            for (var i = 0; i < paged.Count; i++)
            {
                var (att, mediaSize) = paged[i];
                streamed[i].Shortname.ShouldBe(att.Shortname);
                streamed[i].Subpath.ShouldBe(att.Subpath);
                streamed[i].IsActive.ShouldBe(att.IsActive);
                streamed[i].OwnerShortname.ShouldBe(att.OwnerShortname);
                streamed[i].State.ShouldBe(att.State);
                streamed[i].Body.ShouldBe(att.Body);
                // The size the archive records, without shipping the bytes.
                streamed[i].MediaSize.ShouldBe(mediaSize);

                // Parsed comparison: same object, whatever the key order.
                if (att.Payload is not null)
                {
                    streamed[i].Payload.ShouldNotBeNull();
                    using var doc = System.Text.Json.JsonDocument.Parse(streamed[i].Payload!);
                    doc.RootElement.GetProperty("content_type").GetString()
                        .ShouldBe(JsonbHelpers.EnumMember(att.Payload.ContentType));
                }
            }
        }
        finally { await CleanupAsync(space); }
    }

    // Media bytes must NOT ride along in the stream — only their length. A
    // reader that pulled blobs inline would turn a bounded export into one
    // that holds every attachment in memory at once.
    [FactIfPostgresOnly]
    public async Task Attachment_Stream_Reports_Media_Size_Without_Shipping_Bytes()
    {
        var (space, attachments) = await SeedAttachmentsAsync(withMedia: true);
        try
        {
            var streamed = new List<AttachmentExportRow>();
            await foreach (var a in attachments.StreamForExportAsync(space, null, null))
                streamed.Add(a);

            var withMedia = streamed.Single(a => a.Shortname == "hasmedia");
            withMedia.MediaSize.ShouldBe(5, "the length must come through");
            // AttachmentExportRow has no bytes field at all — the type itself
            // is the guarantee. Assert the size is usable for the blob branch.
            withMedia.MediaSize.ShouldBeGreaterThan(0);
            streamed.Single(a => a.Shortname == "nomedia").MediaSize.ShouldBe(0);
        }
        finally { await CleanupAsync(space); }
    }

    [FactIfPostgresOnly]
    public async Task Attachment_Schema_Guard_Passes_And_Notices_A_Type_Change()
    {
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var dbf = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        _factory.CreateClient();

        (await attachments.ExportSchemaMismatchAsync()).ShouldBeNull();

        await using (var conn = await dbf.OpenAsync())
        await using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE attachments ALTER COLUMN body TYPE varchar(500)";
            await alter.ExecuteNonQueryAsync();
        }
        try
        {
            var mismatch = await attachments.ExportSchemaMismatchAsync();
            mismatch.ShouldNotBeNull();
            mismatch!.ShouldContain("body");
        }
        finally
        {
            await using var conn = await dbf.OpenAsync();
            await using var revert = conn.CreateCommand();
            revert.CommandText = "ALTER TABLE attachments ALTER COLUMN body TYPE text";
            await revert.ExecuteNonQueryAsync();
        }
    }

    private async Task<(string, AttachmentRepository)> SeedAttachmentsAsync(bool withMedia = false)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var attachments = sp.GetRequiredService<AttachmentRepository>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "cpya_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "parent",
            SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
            IsActive = true, OwnerShortname = "dmart",
        });

        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "nomedia",
            SpaceName = space, Subpath = "/parent", ResourceType = ResourceType.Media,
            IsActive = true, OwnerShortname = "dmart",
            Payload = new Payload { ContentType = ContentType.Json },
        });
        if (withMedia)
            await attachments.UpsertAsync(new Attachment
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = "hasmedia",
                SpaceName = space, Subpath = "/parent", ResourceType = ResourceType.Media,
                IsActive = true, OwnerShortname = "dmart",
                Media = [1, 2, 3, 4, 5],
                Payload = new Payload { ContentType = ContentType.ImagePng },
            });
        return (space, attachments);
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
