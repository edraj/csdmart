using System.IO.Compression;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Round-trip coverage for /managed/export → /managed/import with the
// Python-compatible zip layout. Seeds a scratch space + a handful of
// entries, exports, asserts the zip layout matches Python's shape, then
// deletes everything and re-imports from the same zip and verifies the DB
// state comes back identical. Bypasses the HTTP endpoints so admin login
// infra-flakes don't gate the test.
public class ImportExportRoundTripTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ImportExportRoundTripTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Export_Then_Import_Round_Trips_Entries_Folders_And_Payload_Bodies()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();

        var spaceName = "iex_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        // Seed: two folders + three content entries with JSON payloads.
        var folderA = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "products", SpaceName = spaceName,
            Subpath = "/", ResourceType = ResourceType.Folder, IsActive = true,
            OwnerShortname = "dmart", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var folderB = new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "widgets", SpaceName = spaceName,
            Subpath = "/products", ResourceType = ResourceType.Folder, IsActive = true,
            OwnerShortname = "dmart", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var c1 = MakeContent(spaceName, "/products/widgets", "w1", new { sku = "A-1", price = 10 });
        var c2 = MakeContent(spaceName, "/products/widgets", "w2", new { sku = "A-2", price = 25 });
        var c3 = MakeContent(spaceName, "/", "readme", new { text = "hello" });
        foreach (var e in new[] { folderA, folderB, c1, c2, c3 })
            await entryRepo.UpsertAsync(e);

        // --- EXPORT ---
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = spaceName, Subpath = "/",
            FilterSchemaNames = new(), Limit = 10_000, RetrieveJsonPayload = true,
        };
        await using var exported = await io.ExportAsync(q, actor: null);
        using var ms = new MemoryStream();
        await exported.CopyToAsync(ms);
        ms.Position = 0;

        // Layout sanity checks on the zip. Everything must live under the
        // `{space}/...` root (no legacy flat layout).
        using (var read = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            var names = read.Entries.Select(e => e.FullName).ToList();
            names.ShouldContain($"{spaceName}/.dm/meta.space.json");
            // Folders: meta lives inside the folder's own .dm
            names.ShouldContain($"{spaceName}/products/.dm/meta.folder.json");
            names.ShouldContain($"{spaceName}/products/widgets/.dm/meta.folder.json");
            // Non-folder: meta inside {subpath}/.dm/{sn}/meta.{rt}.json
            names.ShouldContain($"{spaceName}/products/widgets/.dm/w1/meta.content.json");
            names.ShouldContain($"{spaceName}/products/widgets/.dm/w2/meta.content.json");
            names.ShouldContain($"{spaceName}/.dm/readme/meta.content.json");
            // Externalized JSON payload bodies next to the meta dir
            names.ShouldContain($"{spaceName}/products/widgets/w1.json");
            names.ShouldContain($"{spaceName}/products/widgets/w2.json");
            names.ShouldContain($"{spaceName}/readme.json");
            // Field stripping — meta must NOT carry space_name/subpath/resource_type
            var w1Meta = read.GetEntry($"{spaceName}/products/widgets/.dm/w1/meta.content.json")!;
            using var stream = w1Meta.Open();
            using var doc = JsonDocument.Parse(stream);
            doc.RootElement.TryGetProperty("space_name", out _).ShouldBeFalse();
            doc.RootElement.TryGetProperty("subpath", out _).ShouldBeFalse();
            doc.RootElement.TryGetProperty("resource_type", out _).ShouldBeFalse();
            // Payload body should now be the externalized filename
            doc.RootElement.TryGetProperty("payload", out var payload).ShouldBeTrue();
            payload.GetProperty("body").GetString().ShouldBe("w1.json");
        }

        // --- DELETE + IMPORT ---
        foreach (var e in new[] { folderA, folderB, c1, c2, c3 })
            await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType);

        ms.Position = 0;
        var resp = await io.ImportZipAsync(ms, actor: null);
        resp.Status.ShouldBe(Status.Success);
        var stats = resp.Attributes!;
        ((int)stats["entries_inserted"]!).ShouldBeGreaterThanOrEqualTo(5);

        // --- VERIFY ---
        var w1 = await entryRepo.GetAsync(spaceName, "/products/widgets", "w1", ResourceType.Content);
        w1.ShouldNotBeNull();
        w1!.Payload.ShouldNotBeNull();
        w1.Payload!.Body.ShouldNotBeNull();
        var sku = w1.Payload.Body!.Value.GetProperty("sku").GetString();
        sku.ShouldBe("A-1");

        var readme = await entryRepo.GetAsync(spaceName, "/", "readme", ResourceType.Content);
        readme.ShouldNotBeNull();

        var widgets = await entryRepo.GetAsync(spaceName, "/products", "widgets", ResourceType.Folder);
        widgets.ShouldNotBeNull();
        widgets!.ResourceType.ShouldBe(ResourceType.Folder);

        // Cleanup
        foreach (var e in new[] { folderA, folderB, c1, c2, c3 })
            try { await entryRepo.DeleteAsync(e.SpaceName, e.Subpath, e.Shortname, e.ResourceType); } catch { }
        try { await spaceRepo.DeleteAsync(spaceName); } catch { }
    }

    [FactIfPg]
    public async Task Import_Hard_Fails_On_Legacy_Flat_Layout()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();

        // Build a legacy flat-layout zip (nothing under a space root).
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var ze = zip.CreateEntry(".dm/foo/meta.content.json");
            using var s = ze.Open();
            using var w = new StreamWriter(s);
            await w.WriteAsync("{\"shortname\":\"foo\"}");
        }
        ms.Position = 0;

        var resp = await io.ImportZipAsync(ms, actor: null);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Message.ShouldContain("legacy flat layout is not supported");
    }

    [FactIfPg]
    public async Task RootSubpath_History_Round_Trips()
    {
        // Regression: TryImportHistoryAsync previously computed the subpath via
        // `rest.IndexOf("/.dm/")` which returns -1 for an entry at subpath "/"
        // (its zip path starts with `.dm/` with no leading slash) → import
        // crashed with "history path missing /.dm/" and the row was lost.
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var historyRepo = sp.GetRequiredService<HistoryRepository>();

        var spaceName = "rhist_" + Guid.NewGuid().ToString("N")[..6];
        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName, SpaceName = spaceName, Subpath = "/",
            OwnerShortname = "dmart", IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var sn = "readme";
        var seedEntry = MakeContent(spaceName, "/", sn, new { text = "hi" });
        await entryRepo.UpsertAsync(seedEntry);
        // Seed one history row directly on the entry at subpath "/".
        await historyRepo.AppendAsync(spaceName, "/", sn, "dmart", null,
            new Dictionary<string, object>
            {
                ["state"] = new Dictionary<string, object>
                    { ["old"] = "new", ["new"] = "confirmed" },
            });

        var q = new Query
        {
            Type = QueryType.Search, SpaceName = spaceName, Subpath = "/",
            FilterSchemaNames = new(), Limit = 1000, RetrieveJsonPayload = true,
        };
        await using var exported = await io.ExportAsync(q, actor: null);
        using var ms = new MemoryStream();
        await exported.CopyToAsync(ms);

        // Assert history.jsonl landed at the root-subpath path shape.
        ms.Position = 0;
        using (var read = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            read.Entries.Select(e => e.FullName)
                .ShouldContain($"{spaceName}/.dm/{sn}/history.jsonl");
        }

        // Wipe entry + histories and re-import.
        await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Content);
        // Best-effort history cleanup — HistoryRepository doesn't expose a
        // targeted delete, so we'll rely on the re-import to assert the row
        // reappears rather than that it's strictly absent first.

        ms.Position = 0;
        var resp = await io.ImportZipAsync(ms, actor: null);

        try
        {
            resp.Status.ShouldBe(Status.Success);
            var stats = resp.Attributes!;
            ((int)stats["histories_inserted"]!).ShouldBeGreaterThanOrEqualTo(1);
            // `failed` must not contain a history-path error.
            var failed = (List<Dictionary<string, object>>)stats["failed"]!;
            var hasHistoryPathError = failed.Any(f =>
                f.ContainsKey("kind") && (string)f["kind"] == "history"
                && f.ContainsKey("error")
                && ((string)f["error"]).Contains("history path missing /.dm/", StringComparison.Ordinal));
            hasHistoryPathError.ShouldBeFalse("root-subpath history must import cleanly");
        }
        finally
        {
            try { await entryRepo.DeleteAsync(spaceName, "/", sn, ResourceType.Content); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    [FactIfPg]
    public async Task Import_Falls_Back_To_Dmart_Owner_When_Meta_Has_No_Owner_Shortname()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();

        var spaceName = "iex_" + Guid.NewGuid().ToString("N")[..6];
        var spaceUuid = Guid.NewGuid().ToString();
        var entryUuid = Guid.NewGuid().ToString();

        // Hand-craft a zip whose space + entry meta JSON omits owner_shortname.
        // Without the EnsureOwner fallback the FK insert (or the required-field
        // deserialization) fails; with it, both rows land owned by "dmart".
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var spaceMeta = zip.CreateEntry($"{spaceName}/.dm/meta.space.json");
            using (var s = spaceMeta.Open())
            using (var w = new StreamWriter(s))
                await w.WriteAsync($"{{\"uuid\":\"{spaceUuid}\",\"shortname\":\"{spaceName}\",\"is_active\":true,\"languages\":[\"en\"],\"resource_type\":\"space\"}}");

            var entryMeta = zip.CreateEntry($"{spaceName}/.dm/orphan/meta.content.json");
            using (var s = entryMeta.Open())
            using (var w = new StreamWriter(s))
                await w.WriteAsync($"{{\"uuid\":\"{entryUuid}\",\"shortname\":\"orphan\",\"is_active\":true,\"resource_type\":\"content\"}}");
        }

        ms.Position = 0;
        var resp = await io.ImportZipAsync(ms, actor: null);
        resp.Status.ShouldBe(Status.Success);

        var imported = await entryRepo.GetAsync(spaceName, "/", "orphan", ResourceType.Content);
        imported.ShouldNotBeNull();
        imported!.OwnerShortname.ShouldBe("dmart");

        // Cleanup
        try { await entryRepo.DeleteAsync(spaceName, "/", "orphan", ResourceType.Content); } catch { }
        try { await spaceRepo.DeleteAsync(spaceName); } catch { }
    }

    // Regression guard: the exported zip must carry the attachment's actual
    // BYTES, not just its meta.
    //
    // AttachmentRepository.ListForParentAsync defaults to a media-less
    // projection (`NULL::bytea AS media`) because the listing and query paths
    // only ever render metadata. Export is the one lister that does need the
    // bytes — WriteAttachmentsAsync writes them into the archive — so it has to
    // opt in with `includeMedia: true`. When it didn't, `att.Media` came back
    // null, the media branch never fired, and every export silently produced a
    // zip full of attachment meta with no files behind it. Nothing caught that,
    // because no test had ever asserted the bytes survive.
    [FactIfPg]
    public async Task Export_Writes_Attachment_Media_Bytes_Into_The_Zip()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var spaceRepo = sp.GetRequiredService<SpaceRepository>();
        var attachRepo = sp.GetRequiredService<AttachmentRepository>();

        var spaceName = "iexmedia_" + Guid.NewGuid().ToString("N")[..6];
        const string parentShortname = "doc";
        const string mediaFilename = "logo.png";
        // Deliberately not valid PNG — export must move the bytes verbatim
        // without interpreting them.
        var mediaBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF };

        await spaceRepo.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var parent = MakeContent(spaceName, "/", parentShortname, new { title = "with media" });
        await entryRepo.UpsertAsync(parent);

        // Attachments hang at "{parent subpath}/{parent shortname}".
        await attachRepo.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "att1",
            SpaceName = spaceName,
            Subpath = $"/{parentShortname}",
            ResourceType = ResourceType.Media,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Media = mediaBytes,
            Payload = new Payload
            {
                ContentType = ContentType.ImagePng,
                // Python's convention: payload.body names the on-disk file the
                // bytes are written to.
                Body = JsonDocument.Parse($"\"{mediaFilename}\"").RootElement.Clone(),
            },
        });

        try
        {
            var q = new Query
            {
                Type = QueryType.Search, SpaceName = spaceName, Subpath = "/",
                FilterSchemaNames = new(), Limit = 10_000, RetrieveJsonPayload = true,
            };
            await using var exported = await io.ExportAsync(q, actor: null);
            using var ms = new MemoryStream();
            await exported.CopyToAsync(ms);
            ms.Position = 0;

            using var read = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
            var mediaPath = $"{spaceName}/.dm/{parentShortname}/attachments.media/{mediaFilename}";

            var entry = read.GetEntry(mediaPath);
            entry.ShouldNotBeNull(
                $"attachment media missing from the export. Zip contained: {string.Join(", ", read.Entries.Select(e => e.FullName))}");

            using var entryStream = entry!.Open();
            using var actual = new MemoryStream();
            await entryStream.CopyToAsync(actual);
            actual.ToArray().ShouldBe(mediaBytes);
        }
        finally
        {
            try { await attachRepo.DeleteUnderSubpathAsync(spaceName, $"/{parentShortname}"); } catch { }
            try { await entryRepo.DeleteAsync(spaceName, "/", parentShortname, ResourceType.Content); } catch { }
            try { await spaceRepo.DeleteAsync(spaceName); } catch { }
        }
    }

    private static Entry MakeContent(string space, string subpath, string shortname, object body)
    {
        var bodyJson = JsonSerializer.Serialize(body);
        return new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
            },
        };
    }
}
