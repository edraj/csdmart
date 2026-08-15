using System.IO.Compression;
using Dmart.DataAdapters.Sql;
using Dmart.QueryGrammar;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A zip export continues past an entry it cannot emit — deliberately, so one
// bad row does not abandon the whole archive. What it must NOT do is stay
// silent about it.
//
// The cost of a swallowed failure is larger than it looks: WriteEntryAsync
// emits the entry's meta, THEN its attachments, THEN its history, so a throw
// part-way loses all three. The archive stays a valid zip and still restores,
// with less in it than the source had — which is indistinguishable from a
// complete backup until someone tries to use it.
public class ExportPartialFailureTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    [FactIfPg]
    public async Task An_Entry_That_Cannot_Be_Exported_Is_Counted()
    {
        var sp = factory.Services;
        factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var db = sp.GetRequiredService<IDbConnectionFactory>();

        var space = "epf_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space, SpaceName = space,
            Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        try
        {
            foreach (var sn in new[] { "good1", "bad", "good2" })
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = sn,
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });

            // An ATTACHMENT whose content_type the enum does not define.
            //
            // On the attachment rather than the entry deliberately: entry
            // payloads are deserialised by the QUERY, so a bad one fails the
            // whole export loudly. Attachments are read per entry inside
            // WriteEntryAsync, which is the path that swallowed. This is the
            // exact shape that produced a 1094-byte archive instead of 2037
            // with no error and exit 0.
            //
            // Inserted through the repository and then corrupted with a
            // PARAMETERISED update, so the seeding works on both drivers —
            // gen_random_uuid()/::jsonb are PostgreSQL-only.
            var attachments = sp.GetRequiredService<AttachmentRepository>();
            await attachments.UpsertAsync(new Attachment
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = "att_bad",
                SpaceName = space, Subpath = "/bad", ResourceType = ResourceType.Media,
                IsActive = true, OwnerShortname = "dmart",
            });

            await using (var conn = await db.OpenAsync())
            await using (var cmd = conn.CreateCommand())
            {
                var payload = DbParams.Add(
                    cmd, """{"content_type":"not_a_real_type","body":"x.bin"}""", SqlValueKind.Json);
                DbParams.Add(cmd, space);
                cmd.CommandText =
                    $"UPDATE attachments SET payload = {payload} WHERE space_name = $2 AND shortname = 'att_bad'";
                await cmd.ExecuteNonQueryAsync();
            }

            using var ms = new MemoryStream();
            var failed = await io.ExportToAsync(ms, space, "/", actor: null);

            failed.ShouldBe(1, "the unexportable entry must be counted, not merely logged");

            // The archive is still valid and still holds the good entries —
            // continuing past the failure is the intended behaviour.
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            names.ShouldContain(n => n.Contains("good1", StringComparison.Ordinal));
            names.ShouldContain(n => n.Contains("good2", StringComparison.Ordinal));
            // "bad" itself is lost — meta, attachments and history together —
            // which is precisely why the count matters.
            names.ShouldNotContain(n => n.Contains("att_bad", StringComparison.Ordinal));
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    // The healthy case must report zero, or the signal is useless.
    [FactIfPg]
    public async Task A_Clean_Export_Reports_No_Failures()
    {
        var sp = factory.Services;
        factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "epf_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space, SpaceName = space,
            Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        try
        {
            for (var i = 0; i < 3; i++)
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"ok{i}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });

            using var ms = new MemoryStream();
            (await io.ExportToAsync(ms, space, "/", actor: null)).ShouldBe(0);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }
}
