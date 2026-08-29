using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Cli;

// `dmart client-schema-migrate -p=<dir> [--clean|--deep-clean] [--seed]`
//
// Applies a directory of combined schema+folder definitions to the database, so
// a client repo can keep its schemas in version control and replay them into a
// local database instead of hand-editing entries through the API.
//
// ONE FILE PER SCHEMA, at the root of <dir>:
//
//   {
//     "shortname": "plan",        // the schema's shortname
//     "space":     "public",      // holds the schema, the folder and the content
//     "subpath":   "plans",       // the folder: "plans/addons" => folder `addons`
//                                 //   at subpath "/plans"
//     "schema":    { ...JSON Schema... }
//   }
//
// Each file produces up to three things:
//   1. a schema entry at   <space>/schema/<shortname>
//   2. a folder entry at   <space>/<subpath>            (ancestors auto-created)
//   3. with --seed, content entries inside that folder
//
// EVERY WRITE GOES THROUGH EntryService, never the repository or raw SQL.
// That is the point of the command: creates and updates pay for permission
// checks, schema validation, folder content policy, uniqueness, plugin hooks,
// history rows and query_policies generation exactly as an API caller would.
// A migration that bypassed those would produce rows the running server then
// rejects or cannot see — which is the manual-process failure mode this
// command exists to remove. Reads use EntryRepository directly, since reading
// has no application logic to respect.
//
// Deletion scope is deliberately narrow — see CleanAsync.
internal static class SchemaMigrateCommand
{
    // Schema entries live at <space>/schema/<shortname>. SchemaValidator probes
    // "/schema" first, so that is what we write.
    private const string SchemaSubpath = "/schema";

    // Schemas are themselves documents described by meta_schema, and folder
    // bodies by folder_rendering. Both are prerequisites the operator seeds
    // before running this command, and neither may ever be deleted by --clean:
    // removing meta_schema breaks the validation of every schema in the
    // database, and folder_rendering is centralised in `management` for every
    // space at once (SchemaValidator.ValidateDetailedAsync).
    private const string MetaSchema = "meta_schema";
    private const string FolderSchema = "folder_rendering";
    private static readonly HashSet<string> ProtectedSchemas =
        new(StringComparer.Ordinal) { MetaSchema, FolderSchema };

    public sealed record Options(
        string Path,
        bool Clean,
        bool DeepClean,
        bool Seed,
        int SeedCount,
        bool DryRun,
        string Actor);

    /// <summary>One JSON file: a schema plus the folder that will hold its content.</summary>
    /// <param name="Source">The file it came from — every diagnostic names it.</param>
    /// <param name="Subpath">
    /// The folder, as written. The LAST segment is the folder's shortname and
    /// everything before it is that folder's own subpath, so "plans/addons"
    /// means the folder `addons` living at "/plans", and "plans" means the
    /// folder `plans` living at "/".
    /// </param>
    private sealed record Manifest(
        string Source, string Space, string Subpath, string Shortname, JsonElement Schema)
    {
        /// <summary>Full path of the folder, and the subpath its content lives at.</summary>
        public string FolderPath => Locator.NormalizeSubpath(Subpath);
    }

    private sealed class Tally
    {
        public int SchemasCreated, SchemasUpdated, SchemasUnchanged, SchemasDeleted;
        public int FoldersCreated, FoldersUnchanged, FoldersDeleted;
        public int RecordsCreated, RecordsRepaired, RecordsDeleted;
        public readonly List<string> Errors = [];
    }

    public static async Task<int> RunAsync(
        Options opts, DmartSettings settings, IDbConnectionFactory db, CancellationToken ct = default)
    {
        if (!Directory.Exists(opts.Path))
        {
            Console.Error.WriteLine($"Error: path not found: {opts.Path}");
            return 1;
        }

        // Parse EVERYTHING before writing anything. A manifest set that is
        // half-valid would otherwise leave the database half-migrated, and the
        // operator re-running after a fix would hit a mix of create and update
        // paths for reasons unrelated to their edit.
        var (manifests, parseErrors) = LoadManifests(opts.Path);
        if (parseErrors.Count > 0)
        {
            foreach (var e in parseErrors) Console.Error.WriteLine($"Error: {e}");
            return 1;
        }
        if (manifests.Count == 0)
        {
            Console.Error.WriteLine($"Error: no .json manifests found in {opts.Path}");
            return 1;
        }

        var services = CliBootstrap.BuildEntryService(settings, db);

        // The actor must be an effective super admin: the command writes across
        // spaces and must not half-apply because one space happened to be
        // outside the caller's grants. Checking up front turns "prerequisites
        // not met" into one clear message instead of a run of NOT_ALLOWED
        // failures the operator has to reverse-engineer.
        if (!await services.Permissions.IsGlobalAdminAsync(opts.Actor, ct))
        {
            Console.Error.WriteLine(
                $"Error: actor '{opts.Actor}' is not a super admin (needs an active permission over " +
                $"__all_spaces__ / __all_subpaths__ with create+update). Start the server once so " +
                $"AdminBootstrap seeds it, or pass --actor <shortname>.");
            return 1;
        }

        // Deletion is scoped to the spaces the manifests actually declare — a
        // space with no file on disk is never touched. See CleanAsync.
        var managedSpaces = manifests.Select(m => m.Space).ToHashSet(StringComparer.Ordinal);

        Console.WriteLine(
            $"client-schema-migrate {opts.Path}{(opts.DryRun ? "  [DRY RUN — no writes]" : "")}\n" +
            $"  files={manifests.Count} " +
            $"spaces=[{string.Join(", ", managedSpaces.Order(StringComparer.Ordinal))}] actor={opts.Actor}");

        var tally = new Tally();

        // Shallowest folder first, so a parent declared by one file exists
        // before a child declared by another is created. Ties broken on the
        // path itself to keep the run order stable across filesystems.
        var ordered = manifests
            .OrderBy(m => m.FolderPath.Count(c => c == '/'))
            .ThenBy(m => m.FolderPath, StringComparer.Ordinal)
            .ToList();

        // A folder that will hold sub-folders must also admit them. Folders are
        // entries whose payload schema is `folder_rendering`, so a folder
        // restricted to content_schema_shortnames:["plan"] REJECTS its own
        // children once ENFORCE_FOLDER_CONTENT_POLICY is on (the default).
        // Precomputing which declared folders have declared descendants lets
        // the create widen only those. Index [0] stays the content schema, so
        // the identity check below is unaffected.
        var declaredPaths = ordered.Select(m => m.FolderPath).ToHashSet(StringComparer.Ordinal);
        var needsChildFolders = declaredPaths
            .Where(p => declaredPaths.Any(other =>
                other.Length > p.Length && other.StartsWith(p + "/", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        // Folder paths this run has already created or verified, so a folder
        // shared by several files is handled once. Without it a --dry-run
        // re-reports every ancestor for every descendant file — nothing is
        // written, so the existence probe keeps missing — and the summary
        // claims more folders than the run would actually create.
        // Keyed by the (space, path) PAIR rather than a concatenated string:
        // building the key by hand invites a separator mismatch between the two
        // places that add to this set, and that failure is invisible — the set
        // just silently holds both spellings and every dedupe misses.
        var ensured = new HashSet<(string Space, string Path)>();

        foreach (var m in ordered)
        {
            // Read-only, and BEFORE anything is written. A folder already bound
            // to a DIFFERENT content schema means this file is skipped outright
            // — seeding into it would mix schemas in a folder something else
            // owns — and "skipped" has to include its schema, or a rejected
            // file would still leave a half-applied entry behind.
            if (await OwnershipConflictAsync(m, services, ct) is { } conflict)
            {
                Fail(tally, conflict);
                continue;
            }

            await SyncSchemaAsync(m, services, opts, tally, ct);

            var folderOk = await EnsureFolderAsync(
                m, needsChildFolders.Contains(m.FolderPath), ensured, services, opts, tally, ct);
            if (!folderOk) continue;

            if (opts.Seed) await SeedFolderAsync(m, services, opts, tally, ct);
        }

        if (opts.Clean || opts.DeepClean)
            await CleanAsync(manifests, managedSpaces, services, opts, tally, ct);

        Report(tally, opts);
        return tally.Errors.Count > 0 ? 1 : 0;
    }

    // ---------------------------------------------------------------- manifests

    private static (List<Manifest>, List<string>) LoadManifests(string dir)
    {
        var manifests = new List<Manifest>();
        var errors = new List<string>();

        // Ordinal sort so the read order is stable across filesystems — macOS
        // and Linux enumerate directories differently.
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException ex) { errors.Add($"{file}: invalid JSON — {ex.Message}"); continue; }

            // `using` so the document is released on the validation-failure
            // paths too, not only the one that reaches the bottom of the loop.
            using var doc = parsed;
            var root = doc.RootElement;
            var name = Path.GetFileName(file);
            var shortname = Str(root, "shortname");
            var space = Str(root, "space");
            var subpath = Str(root, "subpath");

            if (string.IsNullOrWhiteSpace(shortname)) { errors.Add($"{name}: missing \"shortname\""); continue; }
            if (string.IsNullOrWhiteSpace(space)) { errors.Add($"{name}: missing \"space\""); continue; }
            if (string.IsNullOrWhiteSpace(subpath)) { errors.Add($"{name}: missing \"subpath\""); continue; }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{name}: missing \"schema\" object");
                continue;
            }
            // The last segment names the folder, so a subpath that is only
            // slashes leaves nothing to create. Content cannot live at a
            // space's root here — it needs a folder to be governed by.
            if (Locator.SplitParentFolder(subpath).FolderShortname.Length == 0)
            {
                errors.Add($"{name}: \"subpath\" ('{subpath}') does not name a folder — " +
                           "it must have at least one path segment, e.g. \"plans\" or \"plans/addons\"");
                continue;
            }

            // Clone: a JsonElement pointing into a disposed document throws on
            // access, and this one outlives the loop iteration.
            manifests.Add(new Manifest(file, space!, subpath!, shortname!, schema.Clone()));
        }

        // Two files claiming the same identity is always a mistake, and which
        // one wins would depend on enumeration order. Refuse rather than pick.
        foreach (var dup in manifests.GroupBy(m => (m.Space, m.Shortname)).Where(g => g.Count() > 1))
            errors.Add($"duplicate schema {dup.Key.Space}/{dup.Key.Shortname} in: " +
                       string.Join(", ", dup.Select(d => Path.GetFileName(d.Source))));
        // Two schemas sharing a folder cannot both own its
        // content_schema_shortnames[0], so this would fail at write time
        // anyway — catching it here names both files instead of one.
        foreach (var dup in manifests.GroupBy(m => (m.Space, m.FolderPath)).Where(g => g.Count() > 1))
            errors.Add($"folder {dup.Key.Space}{dup.Key.FolderPath} claimed by more than one schema: " +
                       string.Join(", ", dup.Select(d => $"{Path.GetFileName(d.Source)} ({d.Shortname})")));

        return (manifests, errors);
    }

    // ------------------------------------------------------------------ schemas

    private static async Task SyncSchemaAsync(
        Manifest m, CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        var existing = await svc.Entries.GetAsync(m.Space, SchemaSubpath, m.Shortname, ResourceType.Schema, ct);

        if (existing is null)
        {
            if (opts.DryRun) { Console.WriteLine($"  + schema {m.Space}/{m.Shortname}"); tally.SchemasCreated++; return; }

            var entry = new Entry
            {
                Shortname = m.Shortname,
                SpaceName = m.Space,
                Subpath = SchemaSubpath,
                Uuid = Guid.NewGuid().ToString(),
                IsActive = true,
                OwnerShortname = opts.Actor,
                ResourceType = ResourceType.Schema,
                CreatedAt = TimeUtils.Now(),
                UpdatedAt = TimeUtils.Now(),
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    SchemaShortname = MetaSchema,
                    Body = m.Schema,
                },
            };
            var res = await svc.Entries2.CreateAsync(entry, opts.Actor, ct);
            if (res.IsOk) { Console.WriteLine($"  + schema {m.Space}/{m.Shortname}"); tally.SchemasCreated++; }
            else Fail(tally, $"{Path.GetFileName(m.Source)}: create schema {m.Space}/{m.Shortname} — {res.ErrorMessage}");
            return;
        }

        // Structurally identical bodies are skipped so a re-run is quiet and
        // does not append a no-op history row per schema.
        if (existing.Payload?.Body is { } body && JsonEquivalent(body, m.Schema))
        {
            tally.SchemasUnchanged++;
            return;
        }

        if (opts.DryRun) { Console.WriteLine($"  ~ schema {m.Space}/{m.Shortname}"); tally.SchemasUpdated++; return; }

        var locator = new Locator(ResourceType.Schema, m.Space, SchemaSubpath, m.Shortname);
        var patch = PayloadPatch(MetaSchema, "json", ReplacementBody(existing.Payload?.Body, m.Schema));
        var upd = await svc.Entries2.UpdateAsync(locator, patch, opts.Actor, ct);
        if (upd.IsOk) { Console.WriteLine($"  ~ schema {m.Space}/{m.Shortname}"); tally.SchemasUpdated++; }
        else Fail(tally, $"{Path.GetFileName(m.Source)}: update schema {m.Space}/{m.Shortname} — {upd.ErrorMessage}");
    }

    // ------------------------------------------------------------------ folders

    /// <summary>
    /// Ensures the manifest's folder exists and belongs to this schema.
    /// Returns false when the file must be skipped.
    /// </summary>
    /// <remarks>
    /// An existing folder is NOT rewritten. Its body is the operator's — index
    /// attributes, sort order, view flags — and the only thing this command has
    /// an opinion about is which schema owns it. When that disagrees, the file
    /// is reported and skipped rather than reassigned: seeding into a folder
    /// another schema owns would mix schemas inside it.
    /// </remarks>
    private static async Task<bool> EnsureFolderAsync(
        Manifest m, bool admitsChildFolders, HashSet<(string Space, string Path)> ensured,
        CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        var (parentSubpath, folderShortname) = Locator.SplitParentFolder(m.Subpath);

        // Already handled this run. Only reachable when an EARLIER file
        // created this path as one of its ancestors, which means it was
        // created unrestricted and is a container — there is no owning schema
        // to disagree with, so there is nothing left to check.
        if (!ensured.Add((m.Space, m.FolderPath))) return true;

        var existing = await svc.Entries.GetAsync(m.Space, parentSubpath, folderShortname, ResourceType.Folder, ct);

        if (existing is not null)
        {
            // The caller already screened this (see RunAsync); re-checked here
            // so the invariant holds for any future call site rather than
            // depending on one.
            if (OwnershipConflict(m, existing) is { } conflict) { Fail(tally, conflict); return false; }
            tally.FoldersUnchanged++;
            return true;
        }

        // Ancestors first, so "plans/addons" can be declared without "plans"
        // being declared anywhere. They are created UNRESTRICTED (no
        // content_schema_shortnames) — an ancestor holds folders, not content,
        // and pinning it to a schema would reject the very child we are about
        // to put in it.
        foreach (var ancestor in MissingAncestors(m.FolderPath))
        {
            // Same dedupe as the leaf: a shared ancestor is created once per
            // run, not once per descendant file.
            if (!ensured.Add((m.Space, ancestor))) continue;
            var (aParent, aShortname) = Locator.SplitParentFolder(ancestor);
            if (await svc.Entries.GetAsync(m.Space, aParent, aShortname, ResourceType.Folder, ct) is not null)
                continue;
            if (opts.DryRun) { Console.WriteLine($"  + folder {m.Space}{ancestor} (ancestor)"); tally.FoldersCreated++; continue; }
            if (!await CreateFolderAsync(m.Space, aParent, aShortname, contentSchema: null,
                    admitsChildFolders: true, svc, opts, tally, ct))
                return false;
            Console.WriteLine($"  + folder {m.Space}{ancestor} (ancestor)");
            tally.FoldersCreated++;
        }

        if (opts.DryRun) { Console.WriteLine($"  + folder {m.Space}{m.FolderPath}"); tally.FoldersCreated++; return true; }

        if (!await CreateFolderAsync(m.Space, parentSubpath, folderShortname, m.Shortname,
                admitsChildFolders, svc, opts, tally, ct))
            return false;
        Console.WriteLine($"  + folder {m.Space}{m.FolderPath}");
        tally.FoldersCreated++;
        return true;
    }

    /// <summary>
    /// The error to report when the manifest's folder already exists but is
    /// owned by another schema; null when the folder is absent or is ours.
    /// </summary>
    /// <remarks>
    /// Read-only, so it can gate a file BEFORE its schema is written. Ownership
    /// is content_schema_shortnames[0] — a folder that also holds sub-folders
    /// carries `folder_rendering` after it (see CreateFolderAsync), and that
    /// bookkeeping entry must not read as a different owner.
    /// </remarks>
    private static async Task<string?> OwnershipConflictAsync(
        Manifest m, CliServices svc, CancellationToken ct)
    {
        var (parentSubpath, folderShortname) = Locator.SplitParentFolder(m.Subpath);
        var existing = await svc.Entries.GetAsync(
            m.Space, parentSubpath, folderShortname, ResourceType.Folder, ct);
        return existing is null ? null : OwnershipConflict(m, existing);
    }

    private static string? OwnershipConflict(Manifest m, Entry folder)
    {
        var owner = ContentSchemaOf(folder.Payload?.Body);
        if (string.Equals(owner, m.Shortname, StringComparison.Ordinal)) return null;
        return $"{Path.GetFileName(m.Source)}: folder {m.Space}{m.FolderPath} already exists with " +
               $"content_schema_shortnames[0]={(owner.Length == 0 ? "(unset)" : $"'{owner}'")}, " +
               $"not '{m.Shortname}' — skipping this file (fix the folder, or point the file at another subpath)";
    }

    /// <summary>Every ancestor folder path of <paramref name="folderPath"/>, outermost first.</summary>
    private static List<string> MissingAncestors(string folderPath)
    {
        var segments = folderPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ancestors = new List<string>(Math.Max(0, segments.Length - 1));
        for (var i = 1; i < segments.Length; i++)
            ancestors.Add("/" + string.Join('/', segments.Take(i)));
        return ancestors;
    }

    private static async Task<bool> CreateFolderAsync(
        string space, string parentSubpath, string shortname, string? contentSchema,
        bool admitsChildFolders, CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        // folder_rendering is additionalProperties:false and requires
        // index_attributes, so the body cannot be an arbitrary stub: it has to
        // carry that field and may carry nothing the schema does not define.
        var body = new JsonObject
        {
            ["index_attributes"] = new JsonArray(
                new JsonObject { ["key"] = "shortname", ["name"] = "shortname" }),
            ["shortname_title"] = shortname,
            ["allow_view"] = true,
            ["allow_create"] = true,
            ["allow_update"] = true,
            ["allow_delete"] = true,
        };
        if (contentSchema is not null)
        {
            // [0] is the content schema — the identity this command keys on.
            // `folder_rendering` is appended ONLY when this folder must also
            // hold sub-folders: a non-empty content_schema_shortnames is
            // enforced against every child entry, and a child folder's own
            // payload schema is folder_rendering, so without it the child
            // creation is rejected outright.
            var schemas = new JsonArray { (JsonNode?)JsonValue.Create(contentSchema) };
            if (admitsChildFolders) schemas.Add((JsonNode?)JsonValue.Create(FolderSchema));
            body["content_schema_shortnames"] = schemas;
        }

        var entry = new Entry
        {
            Shortname = shortname,
            SpaceName = space,
            Subpath = parentSubpath,
            Uuid = Guid.NewGuid().ToString(),
            IsActive = true,
            OwnerShortname = opts.Actor,
            ResourceType = ResourceType.Folder,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = FolderSchema,
                Body = JsonDocument.Parse(body.ToJsonString()).RootElement.Clone(),
            },
        };
        var res = await svc.Entries2.CreateAsync(entry, opts.Actor, ct);
        if (res.IsOk) return true;
        var path = parentSubpath == "/" ? "/" + shortname : parentSubpath + "/" + shortname;
        Fail(tally, $"create folder {space}{path} — {res.ErrorMessage}");
        return false;
    }

    // ---------------------------------------------------------------------- seed

    private static async Task SeedFolderAsync(
        Manifest m, CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        // Narrowed to this schema — see ContentInFolderAsync.
        var content = await ContentInFolderAsync(m.Space, m.FolderPath, m.Shortname, svc, ct);
        if (content.Count == 0)
            await GenerateRecordsAsync(m, svc, opts, tally, ct);
        else
            await RepairRecordsAsync(m, content, svc, opts, tally, ct);
    }

    private static async Task GenerateRecordsAsync(
        Manifest m, CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        // Counted, not assumed: reporting opts.SeedCount here claimed "+10
        // sample record(s)" even on a run where all ten were rejected by
        // schema validation, which is the one line an operator scanning the
        // output would take as confirmation it worked.
        var written = 0;
        for (var i = 0; i < opts.SeedCount; i++)
        {
            var shortname = $"{m.Shortname}_sample_{i + 1:D3}";
            // One generator per record, drawn in a FIXED order (body, then
            // displayname, then description) — it is a single seeded RNG, so
            // reordering these calls would change every value it produces.
            var gen = FakeDataGenerator.For(m.Shortname, i);
            var body = gen.Generate(m.Schema);
            if (body is null)
            {
                Fail(tally, $"could not generate a body for schema '{m.Shortname}'");
                return;
            }
            var (dnEn, dnAr, dnKu) = gen.DisplayName();
            var (dsEn, dsAr, dsKu) = gen.Description(m.Shortname);

            if (opts.DryRun) { tally.RecordsCreated++; written++; continue; }

            var entry = new Entry
            {
                Shortname = shortname,
                SpaceName = m.Space,
                Subpath = m.FolderPath,
                Uuid = Guid.NewGuid().ToString(),
                IsActive = true,
                OwnerShortname = opts.Actor,
                ResourceType = ResourceType.Content,
                CreatedAt = TimeUtils.Now(),
                UpdatedAt = TimeUtils.Now(),
                Tags = ["sample"],
                Displayname = new Translation(dnEn, dnAr, dnKu),
                Description = new Translation(dsEn, dsAr, dsKu),
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    SchemaShortname = m.Shortname,
                    Body = JsonDocument.Parse(body.ToJsonString()).RootElement.Clone(),
                },
            };
            var res = await svc.Entries2.CreateAsync(entry, opts.Actor, ct);
            if (res.IsOk) { tally.RecordsCreated++; written++; }
            else Fail(tally, $"seed {m.Space}{m.FolderPath}/{shortname} — {res.ErrorMessage}");
        }
        if (written > 0)
            Console.WriteLine($"    + {written} sample record(s) in {m.Space}{m.FolderPath} ({m.Shortname})");
    }

    // Brings existing records into line with the schema: drop properties the
    // schema does not define, add required ones that are missing. Values that
    // are present and allowed are never rewritten — this is a conformance fix,
    // not a re-seed, and clobbering real local test data would defeat the
    // purpose of keeping it.
    private static async Task RepairRecordsAsync(
        Manifest m, List<Entry> content, CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        var allowed = AllowedProperties(m.Schema);
        var required = RequiredProperties(m.Schema);
        // additionalProperties:true (or a schema with no `properties` at all)
        // means extra keys are legal, so stripping them would be wrong.
        var stripsExtras = allowed.Count > 0 && AdditionalPropertiesDisallowed(m.Schema);

        var repaired = 0;
        foreach (var entry in content)
        {
            if (entry.Payload?.Body is not { } body || body.ValueKind != JsonValueKind.Object) continue;
            // Belt-and-braces against the caller passing an unnarrowed list:
            // stripping a record against a schema it does not declare would
            // delete real fields. Cheap enough to check on every row.
            if (!string.Equals(entry.Payload.SchemaShortname, m.Shortname, StringComparison.Ordinal)) continue;
            if (JsonNode.Parse(body.GetRawText()) is not JsonObject node) continue;

            var removed = new List<string>();
            var added = new List<string>();

            if (stripsExtras)
            {
                foreach (var key in node.Select(kv => kv.Key).Where(k => !allowed.Contains(k)).ToList())
                {
                    node.Remove(key);
                    removed.Add(key);
                }
            }

            // Seeded from the record's own identity, not the loop counter: a
            // given record then gets the same fill value whether it is the
            // first or the fifth one repaired, so re-running after fixing an
            // unrelated record does not churn this one's values.
            var gen = FakeDataGenerator.For($"{m.Shortname}/{entry.Shortname}", 0);
            foreach (var req in required.Where(r => !node.ContainsKey(r)))
            {
                node[req] = gen.GenerateProperty(m.Schema, req);
                added.Add(req);
            }

            if (removed.Count == 0 && added.Count == 0) continue;

            var detail = (removed.Count > 0 ? $"-[{string.Join(",", removed)}] " : "")
                       + (added.Count > 0 ? $"+[{string.Join(",", added)}]" : "");

            if (opts.DryRun)
            {
                Console.WriteLine($"    ~ {m.Space}{m.FolderPath}/{entry.Shortname} {detail}");
                tally.RecordsRepaired++;
                repaired++;
                continue;
            }

            // A removed key must be spelled as an explicit null for
            // PayloadMerge.MergeBody to drop it — a key merely absent from the
            // patch is left untouched by the deep merge.
            foreach (var key in removed) node[key] = null;

            var patch = PayloadPatch(m.Shortname, "json", node);
            var locator = new Locator(entry.ResourceType, m.Space, m.FolderPath, entry.Shortname);
            var upd = await svc.Entries2.UpdateAsync(locator, patch, opts.Actor, ct);
            if (upd.IsOk)
            {
                Console.WriteLine($"    ~ {m.Space}{m.FolderPath}/{entry.Shortname} {detail}");
                tally.RecordsRepaired++;
                repaired++;
            }
            else Fail(tally, $"repair {m.Space}{m.FolderPath}/{entry.Shortname} — {upd.ErrorMessage}");
        }

        if (repaired == 0)
            Console.WriteLine($"    . {m.Space}{m.FolderPath}: {content.Count} record(s), all schema-conformant");
    }

    // --------------------------------------------------------------------- clean

    // --clean removes database schemas that no longer have a file on disk;
    // --deep-clean additionally removes the folders that pointed at them and
    // the content inside those folders.
    //
    // SCOPE. Only the spaces the manifests declare are scanned. Scanning every
    // space would mean a manifest covering `public` deletes the schemas in
    // `management` — including the meta_schema every other schema validates
    // against — because they have no file on disk. The protected set is a
    // second belt for the case where an operator does put a management schema
    // in the directory.
    private static async Task CleanAsync(
        List<Manifest> manifests, HashSet<string> spaces,
        CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        var onDisk = manifests.Select(m => (m.Space, m.Shortname)).ToHashSet();

        foreach (var space in spaces.Order(StringComparer.Ordinal))
        {
            var dbSchemas = await ReadAllAsync(svc, new Query
            {
                Type = QueryType.Subpath,
                SpaceName = space,
                Subpath = SchemaSubpath,
                ExactSubpath = true,
                FilterTypes = [ResourceType.Schema],
                FilterSchemaNames = [],
            }, ct);

            // Loaded ONCE per space rather than once per orphaned schema: the
            // set does not change under us (this loop is the only writer), and
            // re-reading it per schema turned one scan into one-per-deletion.
            // NOT ExactSubpath — folders nest, and a folder pointing at an
            // orphaned schema can sit at any depth in the space.
            List<Entry> spaceFolders = opts.DeepClean
                ? await ReadAllAsync(svc, new Query
                {
                    Type = QueryType.Subpath,
                    SpaceName = space,
                    Subpath = "/",
                    FilterTypes = [ResourceType.Folder],
                    FilterSchemaNames = [],
                }, ct)
                : [];

            foreach (var schema in dbSchemas)
            {
                if (onDisk.Contains((space, schema.Shortname))) continue;
                if (ProtectedSchemas.Contains(schema.Shortname))
                {
                    Console.WriteLine($"  . schema {space}/{schema.Shortname} (protected — not deleted)");
                    continue;
                }

                // Order matters: the folders and their content go first. A
                // folder whose content_schema_shortnames names a schema that
                // no longer exists is a dangling reference, and deleting the
                // schema first would leave that state behind if the folder
                // sweep then failed.
                if (opts.DeepClean)
                    await DeepCleanSchemaAsync(space, schema.Shortname, spaceFolders, svc, opts, tally, ct);

                if (opts.DryRun) { Console.WriteLine($"  - schema {space}/{schema.Shortname}"); tally.SchemasDeleted++; continue; }

                var locator = new Locator(ResourceType.Schema, space, SchemaSubpath, schema.Shortname);
                var res = await svc.Entries2.DeleteAsync(locator, opts.Actor, ct: ct);
                if (res.IsOk) { Console.WriteLine($"  - schema {space}/{schema.Shortname}"); tally.SchemasDeleted++; }
                else Fail(tally, $"delete schema {space}/{schema.Shortname} — {res.ErrorMessage}");
            }
        }
    }

    // Deletes every folder in the space whose content_schema_shortnames names
    // the orphaned schema, plus the content those folders hold.
    private static async Task DeepCleanSchemaAsync(
        string space, string schemaShortname, List<Entry> spaceFolders,
        CliServices svc, Options opts, Tally tally, CancellationToken ct)
    {
        foreach (var folder in spaceFolders)
        {
            if (!ContentSchemaOf(folder.Payload?.Body).Equals(schemaShortname, StringComparison.Ordinal)) continue;

            var folderPath = folder.Subpath == "/" ? "/" + folder.Shortname : folder.Subpath + "/" + folder.Shortname;

            // Content first, then the folder. EntryService.DeleteAsync does
            // cascade a folder's children, but doing it explicitly keeps the
            // per-record count honest in the report and keeps the behaviour
            // the same if that cascade is ever narrowed.
            foreach (var child in await ContentInFolderAsync(space, folderPath, null, svc, ct))
            {
                if (opts.DryRun) { tally.RecordsDeleted++; continue; }
                var childLocator = new Locator(child.ResourceType, space, folderPath, child.Shortname);
                var res = await svc.Entries2.DeleteAsync(childLocator, opts.Actor, ct: ct);
                if (res.IsOk) tally.RecordsDeleted++;
                else Fail(tally, $"delete content {space}{folderPath}/{child.Shortname} — {res.ErrorMessage}");
            }

            if (opts.DryRun) { Console.WriteLine($"  - folder {space}{folderPath} (schema {schemaShortname})"); tally.FoldersDeleted++; continue; }

            var locator = new Locator(ResourceType.Folder, space, folder.Subpath, folder.Shortname);
            var del = await svc.Entries2.DeleteAsync(locator, opts.Actor, force: true, ct: ct);
            if (del.IsOk) { Console.WriteLine($"  - folder {space}{folderPath} (schema {schemaShortname})"); tally.FoldersDeleted++; }
            else Fail(tally, $"delete folder {space}{folderPath} — {del.ErrorMessage}");
        }
    }

    // -------------------------------------------------------------------- helpers

    /// <summary>
    /// Content directly inside a folder. <paramref name="schemaShortname"/>
    /// narrows to records declaring that schema; null returns everything.
    /// </summary>
    /// <remarks>
    /// The seed path MUST narrow. A folder can hold records of more than one
    /// schema, and repairing a record against a schema it does not claim would
    /// strip every field that schema happens not to define — silently
    /// destroying unrelated data.
    ///
    /// The deep-clean path must NOT narrow: the folder itself is being removed,
    /// so everything inside it goes regardless of schema.
    /// </remarks>
    private static Task<List<Entry>> ContentInFolderAsync(
        string space, string folderPath, string? schemaShortname, CliServices svc, CancellationToken ct)
        => ReadAllAsync(svc, new Query
        {
            Type = QueryType.Subpath,
            SpaceName = space,
            Subpath = folderPath,
            ExactSubpath = true,
            FilterTypes = [ResourceType.Content, ResourceType.Ticket],
            FilterSchemaNames = schemaShortname is null ? [] : [schemaShortname],
        }, ct);

    private const int PageSize = 500;

    /// <summary>
    /// Reads every row a query matches, one page at a time.
    /// </summary>
    /// <remarks>
    /// `Limit = 0` does NOT mean "no limit" — QueryHelper.AppendOrderAndPaging
    /// emits <c>LIMIT Math.Max(1, q.Limit)</c>, so a zero limit silently returns
    /// exactly ONE row. Every caller here is deciding what to delete or repair
    /// against the full set, so a truncated read would quietly do a fraction of
    /// the work and report success.
    ///
    /// Paging is ordered by `shortname`. The default order is `updated_at DESC`,
    /// which is not a total order — rows sharing a timestamp (every row of a
    /// bulk import) can repeat or vanish across pages. Nothing in this command
    /// mutates a shortname mid-run, so the sort key is stable while it pages.
    /// </remarks>
    private static async Task<List<Entry>> ReadAllAsync(CliServices svc, Query q, CancellationToken ct)
    {
        var all = new List<Entry>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await svc.Entries.QueryAsync(q with
            {
                Limit = PageSize,
                Offset = offset,
                SortBy = "shortname",
                SortType = SortType.Ascending,
            }, ct);
            all.AddRange(page);
            if (page.Count < PageSize) return all;
        }
    }

    /// <summary>
    /// The schema a folder's content belongs to: content_schema_shortnames[0].
    /// Empty string when the folder does not declare one.
    /// </summary>
    /// <remarks>
    /// Index [0] specifically, because later entries can be bookkeeping — a
    /// folder that also holds sub-folders carries `folder_rendering` after the
    /// content schema (see CreateFolderAsync).
    /// </remarks>
    private static string ContentSchemaOf(JsonElement? bodyOrNull)
    {
        if (bodyOrNull is not { ValueKind: JsonValueKind.Object } body) return "";
        if (!body.TryGetProperty("content_schema_shortnames", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return "";
        foreach (var item in arr.EnumerateArray())
            return item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        return "";
    }

    // The file on disk is the source of truth, so a body it no longer contains
    // must not survive in the database. That does NOT come for free:
    // EntryService.ApplyPatch runs the payload through PayloadMerge.MergeBody,
    // which DEEP-MERGES — a key merely absent from the patch is left in place.
    // Deleting a property from a schema file and re-running would otherwise be
    // a silent no-op, which is exactly the drift this command exists to stop.
    //
    // ReplacementBody rewrites the patch so every key present in the stored
    // body but absent from the new one is an explicit JSON null, which is how
    // MergeBody spells "remove this key". Objects recurse; arrays and scalars
    // are already replaced wholesale by the merge, so they are copied as-is.
    private static JsonNode? ReplacementBody(JsonElement? stored, JsonElement fresh)
    {
        if (fresh.ValueKind != JsonValueKind.Object
            || stored is not { ValueKind: JsonValueKind.Object } old)
            return JsonNode.Parse(fresh.GetRawText());

        var patch = new JsonObject();
        foreach (var prop in fresh.EnumerateObject())
        {
            patch[prop.Name] = old.TryGetProperty(prop.Name, out var oldVal)
                ? ReplacementBody(oldVal, prop.Value)
                : JsonNode.Parse(prop.Value.GetRawText());
        }
        // Keys the new body dropped — spelled as null so the merge removes them.
        foreach (var prop in old.EnumerateObject())
            if (!fresh.TryGetProperty(prop.Name, out _)) patch[prop.Name] = null;

        return patch;
    }

    private static Dictionary<string, object> PayloadPatch(
        string schemaShortname, string contentType, JsonNode? body)
    {
        var payload = new JsonObject
        {
            ["content_type"] = contentType,
            ["schema_shortname"] = schemaShortname,
            ["body"] = body,
        };
        return new Dictionary<string, object>
        {
            ["payload"] = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
        };
    }

    private static HashSet<string> AllowedProperties(JsonElement schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    private static List<string> RequiredProperties(JsonElement schema)
    {
        var list = new List<string>();
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && r.GetString() is { } s) list.Add(s);
        return list;
    }

    private static bool AdditionalPropertiesDisallowed(JsonElement schema)
        => schema.TryGetProperty("additionalProperties", out var ap)
            && ap.ValueKind == JsonValueKind.False;

    // Structural comparison that ignores key ORDER and insignificant
    // whitespace, so a file reformatted by an editor is not reported as a
    // change. Raw-text comparison would flag every such reformat and append a
    // history row for it.
    private static bool JsonEquivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var bProps = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                if (aProps.Count != bProps.Count) return false;
                for (var i = 0; i < aProps.Count; i++)
                {
                    if (!string.Equals(aProps[i].Name, bProps[i].Name, StringComparison.Ordinal)) return false;
                    if (!JsonEquivalent(aProps[i].Value, bProps[i].Value)) return false;
                }
                return true;
            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                if (aItems.Count != bItems.Count) return false;
                for (var i = 0; i < aItems.Count; i++)
                    if (!JsonEquivalent(aItems[i], bItems[i])) return false;
                return true;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.GetRawText().Equals(b.GetRawText(), StringComparison.Ordinal);
            default:
                return true;   // true / false / null — ValueKind equality already settled it
        }
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static void Fail(Tally tally, string message)
    {
        Console.Error.WriteLine($"  ! {message}");
        tally.Errors.Add(message);
    }

    private static void Report(Tally t, Options opts)
    {
        Console.WriteLine($"""

            Summary {(opts.DryRun ? "(dry run — nothing was written)" : "")}
              schemas  created={t.SchemasCreated} updated={t.SchemasUpdated} unchanged={t.SchemasUnchanged} deleted={t.SchemasDeleted}
              folders  created={t.FoldersCreated} unchanged={t.FoldersUnchanged} deleted={t.FoldersDeleted}
              records  created={t.RecordsCreated} repaired={t.RecordsRepaired} deleted={t.RecordsDeleted}
            """.TrimEnd());
        if (t.Errors.Count > 0)
            Console.Error.WriteLine($"\n{t.Errors.Count} error(s) — see the '!' lines above.");
    }
}
