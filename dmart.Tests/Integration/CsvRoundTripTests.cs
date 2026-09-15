using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Targeted coverage test for Services/CsvService — previously 27% covered, the
// single biggest uncovered file in Services/Api. Exercises:
//
//   * ImportAsync via POST /managed/resources_from_csv/{type}/{space}/{subpath}/{schema}
//     (CSV with header + several rows, one row containing a quoted comma, one
//     containing an escaped quote — hits ParseCsvLine's quote-handling branches
//     and ImportAsync's happy + mismatched-column-count + row-failure branches)
//
//   * ExportAsync via POST /managed/csv on entries whose payload.body contains
//     nested objects, arrays-of-scalars, arrays-of-objects, null values, booleans,
//     and strings with commas + embedded quotes — hits FlattenJsonElement's Object,
//     Array-scalar, Array-complex, String, True, False, Null, and default branches
//     plus EscapeField's quoting branch.
//
// Uses a dedicated space name so it doesn't collide with other integration tests.
public class CsvRoundTripTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public CsvRoundTripTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Csv_Import_Then_Export_Exercises_Flatten_And_Parse_Branches()
    {
        // Per-test user with super_admin role — see DmartFactory.CreateLoggedInUserAsync.
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            // ---- 1. create space + folder + schema ----------------------
            await CleanupAsync(client);

            (await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"space","subpath":"/","shortname":"itest_csv","attributes":{"hide_space":true,"is_active":true}}]}"""))
                .ShouldBeTrue("space create");

            (await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"folder","subpath":"/","shortname":"items","attributes":{"is_active":true}}]}"""))
                .ShouldBeTrue("folder create");

            // /schema may already exist (auto-created by resource_folders_creation plugin)
            await PostOk(client, "/managed/request",
                """{"space_name":"itest_csv","request_type":"create","records":[{"resource_type":"folder","subpath":"/","shortname":"schema","attributes":{"is_active":true}}]}""");

            // Permissive schema — additionalProperties:true with no required fields,
            // so every CSV row (all-string values) validates cleanly.
            await UploadSchemaAsync(client,
                shortname: "goods",
                schemaJson: """{"title":"goods","type":"object","additionalProperties":true}""");

            // ---- 2. import CSV via /managed/resources_from_csv -----------
            // Three data rows + one deliberately malformed row:
            //   apple   — plain values + a JSON-array `features` cell
            //   banana  — quoted field with embedded comma + JSON-array `features`
            //   cherry  — quoted field with escaped double-quote + a `features`
            //             cell that *looks* like an array but is malformed JSON
            //             (must fall back to the raw string, mirroring Python's
            //             always-on heuristic in api/managed/utils.py:1553-1557)
            //
            // Plus a malformed row with the wrong column count so ImportAsync's
            // "expected N fields" failure branch is exercised.
            var csv =
                "shortname,name,price,in_stock,features\r\n" +
                "apple,Red Apple,1.25,true,\"[\"\"crisp\"\",\"\"sweet\"\"]\"\r\n" +
                "banana,\"Cavendish, Ripe\",0.75,true,\"[\"\"yellow\"\"]\"\r\n" +
                "cherry,\"Cherry \"\"Bing\"\"\",2.50,false,[not json\r\n" +
                "malformed,only_two_fields\r\n";

            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: "itest_csv", subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            var importAttrs = importResp.Attributes!;
            // inserted + failed numbers come back as JsonElement since the dictionary
            // value type is object — handle both representations defensively.
            ExtractInt(importAttrs["inserted"]).ShouldBe(3);
            ExtractInt(importAttrs["failed_count"]).ShouldBe(1);

            // ---- 3. create a rich entry so the export path exercises
            //         CsvService's Flatten/Escape branches. EntryMapper.ToRecord
            //         populates Record.Attributes with TYPED values (bool for
            //         is_active, Translation for displayname, string[] for tags,
            //         Payload for payload) — not raw JsonElements — so the
            //         interesting branches to hit are:
            //           * case Translation t  (displayname with en/ar filled in)
            //           * case string[]       (tags joined with "|")
            //           * case Payload p      (payload serialized whole)
            //           * EscapeField quoting (a tag containing a comma/quote/newline)
            var richCreateJson =
                "{\"space_name\":\"itest_csv\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"rich_row\"," +
                "\"attributes\":{" +
                    "\"displayname\":{\"en\":\"Rich Row EN\",\"ar\":\"Rich Row AR\"}," +
                    // Three tags: plain, one with a comma, one with an embedded double-quote.
                    // After string[] → "plain|has,comma|has\"quote",
                    // EscapeField sees both a comma and a quote → wraps + doubles the quote:
                    //   "plain|has,comma|has""quote"
                    "\"tags\":[\"plain\",\"has,comma\",\"has\\\"quote\"]," +
                    "\"payload\":{\"content_type\":\"json\",\"body\":{\"k\":\"v\"}}" +
                "}}]}";
            (await PostOk(client, "/managed/request", richCreateJson))
                .ShouldBeTrue("rich entry create");

            // ---- 4. export items folder as CSV -------------------------
            var exportResp = await client.PostAsync("/managed/csv", new StringContent(
                """{"space_name":"itest_csv","subpath":"items","type":"subpath","filter_schema_names":[],"retrieve_json_payload":true,"limit":50}""",
                Encoding.UTF8, "application/json"));
            exportResp.IsSuccessStatusCode.ShouldBeTrue();
            var exportedCsv = await exportResp.Content.ReadAsStringAsync();

            // Header + at least 4 data rows (apple, banana, cherry, rich_row; the
            // malformed row is skipped, the auto-shortname row adds a 5th).
            var lines = exportedCsv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBeGreaterThanOrEqualTo(5);

            var header = lines[0];
            // Canonical columns always present in this order.
            header.ShouldStartWith("resource_type,shortname,subpath,uuid");
            // Translation branch emits `displayname.en` and `displayname.ar` sub-keys.
            header.ShouldContain("displayname.en");
            header.ShouldContain("displayname.ar");
            // Tags are a top-level attribute column.
            header.ShouldContain("tags");

            // The rich_row's tags value is `plain|has,comma|has"quote` which contains
            // both a comma and a double-quote, so EscapeField wraps it in quotes and
            // doubles the embedded quote: "plain|has,comma|has""quote".
            exportedCsv.ShouldContain("\"plain|has,comma|has\"\"quote\"");

            // And the displayname Translation should have emitted the EN/AR values.
            exportedCsv.ShouldContain("Rich Row EN");
            exportedCsv.ShouldContain("Rich Row AR");

            // ---- 5. round-trip verification: query the space ------------
            var queryResp = await PostJson(client, "/managed/query",
                """{"space_name":"itest_csv","type":"subpath","subpath":"items","filter_schema_names":[],"retrieve_json_payload":true,"limit":50}""");
            queryResp.Status.ShouldBe(Status.Success);
            // apple + banana + cherry (from CSV import) + rich_row = at least 4
            (queryResp.Records?.Count ?? 0).ShouldBeGreaterThanOrEqualTo(4);
            queryResp.Records!.Any(r => r.Shortname == "apple").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "banana").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "cherry").ShouldBeTrue();
            queryResp.Records!.Any(r => r.Shortname == "rich_row").ShouldBeTrue();

            // ---- 6. JSON-array heuristic ---------------------------------
            // apple's `features` cell was `["crisp","sweet"]`. The always-on
            // heuristic in CsvService.ParseCellValue must have lifted it into a
            // real JSON array — not a quoted string — so payload.body.features
            // round-trips as JsonValueKind.Array.
            var appleBody = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "apple"));
            var appleFeatures = appleBody.GetProperty("features");
            appleFeatures.ValueKind.ShouldBe(JsonValueKind.Array);
            appleFeatures.EnumerateArray().Select(e => e.GetString()).ToArray()
                .ShouldBe(new[] { "crisp", "sweet" });

            // cherry's `features` cell was `[not json` — invalid JSON that the
            // heuristic must fall back from, storing the raw string (Python parity).
            var cherryFeatures = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "cherry"))
                .GetProperty("features");
            cherryFeatures.ValueKind.ShouldBe(JsonValueKind.String);
            cherryFeatures.GetString().ShouldBe("[not json");
        }
        finally
        {
            await CleanupAsync(client);
        }
    }

    // CSV header lookup for the `shortname` column is OrdinalIgnoreCase by design —
    // `Shortname` and `SHORTNAME` are accepted as aliases of `shortname`. Pins that
    // contract so a future refactor doesn't silently revert to case-sensitive matching
    // (which is what the dictionary-based lookup used pre-#24).
    [FactIfPg]
    public async Task Csv_Import_UppercaseShortnameHeader_Works()
    {
        const string space = "itest_csv_upper";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Capital-S header — the row's shortname cell `peach` must end up as the
            // resulting record's shortname, not an auto-generated `row-…` value.
            var csv = "Shortname,name,price\r\npeach,Yellow Peach,3.50\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));
            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);

            var queryBody = "{\"space_name\":\"" + space +
                "\",\"type\":\"subpath\",\"subpath\":\"items\",\"filter_schema_names\":[],\"limit\":50}";
            var queryResp = await PostJson(client, "/managed/query", queryBody);
            queryResp.Status.ShouldBe(Status.Success);
            queryResp.Records!.Any(r => r.Shortname == "peach").ShouldBeTrue(
                "row's shortname cell should win — case-insensitive header match must read the raw cell");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // ImportAsync auto-generates an 8-hex shortname when the shortname cell is
    // empty — Guid.NewGuid().ToString("N")[..8], the same convention
    // RequestHandler.ResolveAutoShortname uses on the /request path. The older
    // `row-<8hex>` format contained a `-`, which the shortname regex rejects, so
    // entries created that way passed CreateAsync and then failed validation on
    // every later operation. #24 dropped the prior fixture row covering this
    // branch; re-add explicit coverage.
    [FactIfPg]
    public async Task Csv_Import_EmptyShortnameCell_AutoGenerates()
    {
        const string space = "itest_csv_auto";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Two rows, both with empty shortname cells. Each must get its own
            // unique auto-generated shortname so the inserts don't collide.
            var csv =
                "shortname,name,price\r\n" +
                ",Plum,1.00\r\n" +
                ",Apricot,2.00\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));
            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2);

            var queryBody = "{\"space_name\":\"" + space +
                "\",\"type\":\"subpath\",\"subpath\":\"items\",\"filter_schema_names\":[],\"limit\":50}";
            var queryResp = await PostJson(client, "/managed/query", queryBody);
            queryResp.Status.ShouldBe(Status.Success);
            // ImportAsync builds Guid.NewGuid().ToString("N")[..8] — same convention as
            // RequestHandler.ResolveAutoShortname on the /request path — since the
            // shortname regex rejects "-", so "Plum" and "Apricot" are the only
            // fixed values to match on; every shortname here is auto-generated.
            var autoNames = queryResp.Records!.Select(r => r.Shortname).ToList();
            autoNames.Count.ShouldBe(2);
            foreach (var n in autoNames)
                System.Text.RegularExpressions.Regex.IsMatch(n, "^[0-9a-f]{8}$")
                    .ShouldBeTrue($"auto-generated shortname '{n}' must match <8hex>");
            autoNames.Distinct().Count().ShouldBe(2, "auto-generated shortnames must be unique");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // `?is_update=true` switches the route from per-row create to per-row
    // UpdateAsync, deep-merging the CSV's columns into the existing entry's
    // payload.body. Pre-existing fields not mentioned in the CSV row must
    // survive; conflicting fields win in favor of the CSV value.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_DeepMerges_PayloadBody()
    {
        const string space = "itest_csv_upd";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Seed two entries with full payload.body. The CSV update below
            // only sends a subset of keys per row — the keys it omits must
            // remain unchanged after the deep merge.
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"alpha\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"goods\"," +
                "\"body\":{\"name\":\"Alpha original\",\"price\":1.00,\"in_stock\":true}}}}]}"))
                .ShouldBeTrue("alpha seed");
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"beta\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"goods\"," +
                "\"body\":{\"name\":\"Beta original\",\"price\":2.00,\"in_stock\":true}}}}]}"))
                .ShouldBeTrue("beta seed");

            // Update CSV touches only `price` (overwrite) and adds `color` (new key).
            // `name` and `in_stock` columns are omitted — they must survive untouched.
            var csv =
                "shortname,price,color\r\n" +
                "alpha,9.99,red\r\n" +
                "beta,8.88,blue\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);

            // Verify the merge: original `name`/`in_stock` survive, `price` is
            // overwritten, `color` is added.
            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            queryResp.Status.ShouldBe(Status.Success);

            var alpha = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "alpha"));
            alpha.GetProperty("name").GetString().ShouldBe("Alpha original");
            alpha.GetProperty("in_stock").GetBoolean().ShouldBeTrue();
            // The `goods` schema declares no properties at all, so nothing tells
            // the importer `price` is a number and the cell stays a string.
            // Coercion is schema-driven: see the *_SchemaAware_* tests for the
            // declared-type behaviour.
            alpha.GetProperty("price").GetString().ShouldBe("9.99");
            alpha.GetProperty("color").GetString().ShouldBe("red");

            var beta = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "beta"));
            beta.GetProperty("name").GetString().ShouldBe("Beta original");
            beta.GetProperty("in_stock").GetBoolean().ShouldBeTrue();
            beta.GetProperty("price").GetString().ShouldBe("8.88");
            beta.GetProperty("color").GetString().ShouldBe("blue");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Update mode against a CSV row whose shortname doesn't exist: the
    // surrounding request must still return Status.Success (it's a partial-batch
    // contract, not a fail-the-whole-import contract) but the missing row lands
    // in `failed` with EntryService's OBJECT_NOT_FOUND. Symmetric with the
    // create branch's SHORTNAME_ALREADY_EXIST handling.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_MissingShortname_Returns_ObjectNotFound_In_Failed_List()
    {
        const string space = "itest_csv_upd_miss";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Single row pointing at a shortname that was never created.
            var csv = "shortname,name\r\nghost,Nothing here\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            // `failed` round-trips as a JsonElement array via the source-gen
            // dict<string,object> path. Inspect the single entry's code +
            // shortname so a regression that returns a different InternalErrorCode
            // (e.g. NOT_ALLOWED for a permission miss) is caught explicitly.
            var failedEl = (JsonElement)importResp.Attributes!["failed"];
            failedEl.GetArrayLength().ShouldBe(1);
            var row0 = failedEl[0];
            row0.GetProperty("shortname").GetString().ShouldBe("ghost");
            // Either int (raw) or string (round-tripped via JsonElement.GetRawText)
            // depending on path — accept both shapes defensively.
            var code = row0.GetProperty("code");
            var codeAsInt = code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : int.Parse(code.GetString()!);
            codeAsInt.ShouldBe(Dmart.Models.Api.InternalErrorCode.OBJECT_NOT_FOUND,
                "missing-shortname update row should map to OBJECT_NOT_FOUND");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // In create mode an empty shortname cell auto-generates `row-<8hex>`. In
    // update mode the same auto-generation happens but no entry with that
    // freshly-minted shortname exists yet → every empty-shortname row 404s
    // into the failed list. Pin that contract so a future change that decides
    // to (e.g.) silently skip empty-shortname rows under update mode is loud.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_EmptyShortnameCell_Falls_Into_Failed_List()
    {
        const string space = "itest_csv_upd_empty";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Both rows have empty shortname cells; update mode auto-generates
            // names that don't exist anywhere → both fail.
            var csv =
                "shortname,name\r\n" +
                ",Orphan A\r\n" +
                ",Orphan B\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(2);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // A row that fails schema validation must not just echo the raw validation
    // message — the failed entry also names the offending `key` (the field the
    // schema flagged, as a JSON Pointer) and the `value` from that CSV cell, so an
    // operator can pinpoint the bad column without parsing the pointer out of the
    // message string.
    [FactIfPg]
    public async Task Csv_Import_SchemaValidationFailure_Reports_Key_And_Value_In_Failed_List()
    {
        const string space = "itest_csv_schema_kv";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            // Restrictive schema: is_used is constrained to an enum.
            await UploadSchemaAsync(client,
                shortname: "strict",
                schemaJson: """{"title":"strict","type":"object","additionalProperties":true,"properties":{"is_used":{"enum":["yes","no"]}}}""",
                space: space);

            // is_used="maybe" is outside the enum → the row lands in `failed`.
            var csv = "shortname,is_used\r\n11DE4,maybe\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "strict",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var failedEl = (JsonElement)importResp.Attributes!["failed"];
            failedEl.GetArrayLength().ShouldBe(1);
            var row0 = failedEl[0];
            row0.GetProperty("shortname").GetString().ShouldBe("11DE4");
            row0.GetProperty("error").GetString()!.ShouldContain("schema validation");
            // The enum failure is on /is_used; key drops the pointer slash, value
            // echoes the offending cell.
            row0.GetProperty("key").GetString().ShouldBe("is_used");
            row0.GetProperty("value").GetString().ShouldBe("maybe");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // When the FIRST schema error is a root-level one (e.g. `required`, whose
    // instance location is the document root, so there is no field to name),
    // the enrichment must not give up: the first error that DOES name a field
    // (here /is_used) still supplies `key`/`value`.
    [FactIfPg]
    public async Task Csv_Import_RootLevelError_Still_Reports_First_Field_Level_Key_And_Value()
    {
        const string space = "itest_csv_rooterr";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "strict",
                schemaJson: """{"title":"strict","type":"object","additionalProperties":true,"required":["name"],"properties":{"is_used":{"enum":["yes","no"]}}}""",
                space: space);

            // No `name` column → root-level `required` error comes FIRST in the
            // validator output; the enum failure on /is_used follows it.
            var csv = "shortname,is_used\r\nR1,maybe\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "strict",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var row0 = ((JsonElement)importResp.Attributes!["failed"])[0];
            row0.GetProperty("error").GetString()!.ShouldContain("required");
            row0.GetProperty("key").GetString().ShouldBe("is_used");
            row0.GetProperty("value").GetString().ShouldBe("maybe");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // An empty CSV cell is the most common way to trip minLength/pattern — and
    // `""` is exactly what JsonStripEmptiesMiddleware normally deletes from
    // responses. The failed list is diagnostics: value:"" must survive to the
    // client so the operator sees WHICH cell was empty.
    [FactIfPg]
    public async Task Csv_Import_EmptyCellSchemaFailure_Value_Survives_Response_Stripping()
    {
        const string space = "itest_csv_emptyval";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "strict",
                schemaJson: """{"title":"strict","type":"object","additionalProperties":true,"properties":{"sku":{"minLength":1}}}""",
                space: space);

            // Empty sku cell → "" fails minLength.
            var csv = "shortname,sku\r\nE1,\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "strict",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var row0 = ((JsonElement)importResp.Attributes!["failed"])[0];
            row0.GetProperty("key").GetString().ShouldBe("sku");
            row0.TryGetProperty("value", out var val)
                .ShouldBeTrue("empty-string value must not be stripped from the response");
            val.GetString().ShouldBe("");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Update mode: EntryService validates the MERGED entry (stored body deep-
    // merged with the CSV patch), so the offending field can live in the stored
    // entry rather than the CSV row. The reported `value` must be the value the
    // validator actually rejected — the merged one — not a (missing) CSV cell.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_SchemaFailure_Reports_Merged_Value_Not_Patch_Value()
    {
        const string space = "itest_csv_updval";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            // Create the entry BEFORE its schema exists (missing schemas pass
            // through — dmart's lenient first-write behavior) so we can store a
            // value that the later, tighter schema rejects.
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"legacy\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"strict\"," +
                "\"body\":{\"is_used\":\"maybe\",\"note\":\"orig\"}}}}]}"))
                .ShouldBeTrue("legacy entry seed");
            await UploadSchemaAsync(client,
                shortname: "strict",
                schemaJson: """{"title":"strict","type":"object","additionalProperties":true,"properties":{"is_used":{"enum":["yes","no"]}}}""",
                space: space);

            // The CSV patch touches only `note`; the merged body still carries the
            // stored is_used="maybe", which now fails the enum.
            var csv = "shortname,note\r\nlegacy,updated\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "strict",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var row0 = ((JsonElement)importResp.Attributes!["failed"])[0];
            row0.GetProperty("key").GetString().ShouldBe("is_used");
            row0.GetProperty("value").GetString().ShouldBe("maybe",
                "value must come from the merged body the validator rejected, not the CSV patch");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // CSV headers become body property names verbatim, and RFC 6901 pointers
    // don't escape ": " — the reported `key` must carry the full property name
    // instead of being truncated at the first ": " (which is what happens when
    // the pointer is re-parsed out of the display message).
    [FactIfPg]
    public async Task Csv_Import_SchemaFailure_On_Column_Containing_ColonSpace_Reports_Full_Key()
    {
        const string space = "itest_csv_colonkey";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "strict",
                schemaJson: """{"title":"strict","type":"object","additionalProperties":true,"properties":{"price: usd":{"enum":["1","2"]}}}""",
                space: space);

            var csv = "shortname,price: usd\r\nC1,999\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "strict",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var row0 = ((JsonElement)importResp.Attributes!["failed"])[0];
            row0.GetProperty("key").GetString().ShouldBe("price: usd");
            row0.GetProperty("value").GetString().ShouldBe("999");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // A schema property declared number/integer/boolean coerces its CSV cell to
    // that JSON kind. Declared-string and undeclared columns stay plain text.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_CoercesDeclaredNumberIntegerBooleanColumns()
    {
        const string space = "itest_csv_typed";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "typed",
                schemaJson: """
                {"title":"typed","type":"object","additionalProperties":true,
                 "properties":{"name":{"type":"string"},"price":{"type":"number"},
                               "qty":{"type":"integer"},"in_stock":{"type":"boolean"}}}
                """,
                space: space);

            var csv = "shortname,name,price,qty,in_stock,note\r\n" +
                      "T1,Widget,19.99,5,true,plain text\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "typed",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            queryResp.Status.ShouldBe(Status.Success);

            var body = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "T1"));
            body.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Number);
            body.GetProperty("price").GetDouble().ShouldBe(19.99);
            body.GetProperty("qty").ValueKind.ShouldBe(JsonValueKind.Number);
            body.GetProperty("qty").GetInt32().ShouldBe(5);
            body.GetProperty("in_stock").ValueKind.ShouldBe(JsonValueKind.True);
            body.GetProperty("name").ValueKind.ShouldBe(JsonValueKind.String);
            body.GetProperty("name").GetString().ShouldBe("Widget");
            body.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.String);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Import keeps CSV headers flat (Python's csv.DictReader does, and the
    // failed-row `key` the API reports is the header text), so a DOTTED header
    // is matched against a literally-dotted schema property name — not against
    // a nested {"stats":{"properties":{"views":...}}}, which could never apply
    // to a flat "stats.views" key anyway.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_LiteralDottedPropertyName_IsCoerced()
    {
        const string space = "itest_csv_dotted";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "dotted",
                schemaJson: """
                {"title":"dotted","type":"object","additionalProperties":true,
                 "properties":{"stats.views":{"type":"integer"}}}
                """,
                space: space);

            var csv = "shortname,stats.views\r\nN1,42\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "dotted",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            queryResp.Status.ShouldBe(Status.Success);

            var body = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "N1"));
            body.GetProperty("stats.views").ValueKind.ShouldBe(JsonValueKind.Number);
            body.GetProperty("stats.views").GetInt32().ShouldBe(42);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // A boolean sub-schema (`{"properties":{"x":true}}`) is legal JSON Schema and
    // stores fine, but JsonElement.TryGetProperty throws InvalidOperationException
    // on a non-object — which is NOT a JsonException and so was caught nowhere,
    // taking the whole import down with a 500 instead of importing the file.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_BooleanSubSchema_DoesNotFailTheImport()
    {
        const string space = "itest_csv_boolsub";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "boolsub",
                schemaJson: """
                {"title":"boolsub","type":"object","additionalProperties":true,
                 "properties":{"anything":true,"qty":{"type":"integer"}}}
                """,
                space: space);

            var csv = "shortname,anything,qty\r\nB1,free text,7\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "boolsub",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            var body = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "B1"));
            // The sibling declared type still took effect — the walk didn't abort.
            body.GetProperty("qty").ValueKind.ShouldBe(JsonValueKind.Number);
            body.GetProperty("anything").ValueKind.ShouldBe(JsonValueKind.String);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Declared types reached through allOf + a local $ref into $defs must coerce
    // too. A composed schema has no top-level "properties", so walking only that
    // key silently dropped coercion for the entire document.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_ComposedSchema_AllOfAndRef_IsCoerced()
    {
        const string space = "itest_csv_allof";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "composed",
                schemaJson: """
                {"title":"composed","type":"object","additionalProperties":true,
                 "$defs":{"pricing":{"type":"object","properties":{"price":{"type":"number"}}}},
                 "allOf":[{"$ref":"#/$defs/pricing"},
                          {"type":"object","properties":{"qty":{"type":"integer"}}}]}
                """,
                space: space);

            var csv = "shortname,price,qty\r\nA1,19.99,5\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "composed",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            var body = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "A1"));
            body.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Number);  // via $ref
            body.GetProperty("qty").ValueKind.ShouldBe(JsonValueKind.Number);    // via allOf member
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // Spreadsheets do not write JSON booleans. TRUE/FALSE (Excel, LibreOffice),
    // yes/no and 1/0 must all reach a declared-boolean column as a real boolean;
    // anything else stays a string so the schema can reject it rather than
    // silently becoming `false`.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_SpreadsheetBooleanSpellings_AreCoerced()
    {
        const string space = "itest_csv_bools";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "flags",
                schemaJson: """
                {"title":"flags","type":"object","additionalProperties":true,
                 "properties":{"flag":{"type":"boolean"}}}
                """,
                space: space);

            var csv = "shortname,flag\r\n" +
                      "up,TRUE\r\ndown,False\r\nyep,yes\r\nnope,no\r\none,1\r\nzero,0\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "flags",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(6);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            JsonValueKind FlagOf(string sn) =>
                GetPayloadBody(queryResp.Records!.First(r => r.Shortname == sn))
                    .GetProperty("flag").ValueKind;

            FlagOf("up").ShouldBe(JsonValueKind.True);
            FlagOf("down").ShouldBe(JsonValueKind.False);
            FlagOf("yep").ShouldBe(JsonValueKind.True);
            FlagOf("nope").ShouldBe(JsonValueKind.False);
            FlagOf("one").ShouldBe(JsonValueKind.True);
            FlagOf("zero").ShouldBe(JsonValueKind.False);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // A blank cell in a declared number/integer/boolean column means "no value".
    // Storing "" there put a string where the schema requires a number and
    // failed the row — which is exactly what re-importing a sparse export did,
    // since the exporter writes "" for a null/absent attribute.
    [FactIfPg]
    public async Task Csv_Import_SchemaAware_EmptyTypedCell_IsOmittedNotEmptyString()
    {
        const string space = "itest_csv_sparse";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            await UploadSchemaAsync(client,
                shortname: "sparse",
                schemaJson: """
                {"title":"sparse","type":"object","additionalProperties":true,
                 "properties":{"price":{"type":"number"},"in_stock":{"type":"boolean"},
                               "note":{"type":"string"}}}
                """,
                space: space);

            // S1 has price + in_stock; S2 leaves both blank and must still import.
            var csv = "shortname,price,in_stock,note\r\n" +
                      "S1,19.99,true,first\r\n" +
                      "S2,,,second\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "sparse",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(0,
                "a blank cell in a typed column must not fail schema validation");
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2);

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");

            var s2 = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "S2"));
            s2.TryGetProperty("price", out _).ShouldBeFalse("blank typed cell must be absent, not \"\"");
            s2.TryGetProperty("in_stock", out _).ShouldBeFalse();
            // An UNdeclared column keeps its verbatim (empty) text — unchanged behaviour.
            s2.GetProperty("note").GetString().ShouldBe("second");

            var s1 = GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "S1"));
            s1.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Number);
            s1.GetProperty("in_stock").ValueKind.ShouldBe(JsonValueKind.True);
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // On the update path the shortname is the row's ADDRESS, so a blank cell is
    // a malformed row. It used to be handed a freshly-minted random 8-hex name
    // and come back as OBJECT_NOT_FOUND naming a string the operator never
    // wrote; it now fails the row saying what is actually wrong.
    [FactIfPg]
    public async Task Csv_Import_UpdateMode_BlankShortname_ReportsMissingShortname()
    {
        const string space = "itest_csv_updblank";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);
            (await PostOk(client, "/managed/request",
                "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
                "{\"resource_type\":\"content\",\"subpath\":\"items\",\"shortname\":\"kept\"," +
                "\"attributes\":{\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"goods\"," +
                "\"body\":{\"name\":\"Original\"}}}}]}"))
                .ShouldBeTrue("seed");

            var csv = "shortname,name\r\n,Orphan\r\nkept,Updated\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv), isUpdate: true);

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(1, "the addressable row still applies");
            ExtractInt(importResp.Attributes!["failed_count"]).ShouldBe(1);

            var row0 = ((JsonElement)importResp.Attributes!["failed"])[0];
            row0.GetProperty("shortname").GetString().ShouldBe("",
                "the failure names the blank cell, not an invented shortname");
            row0.GetProperty("error").GetString()!.ShouldContain("shortname is required");

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":50}");
            queryResp.Records!.Count.ShouldBe(1, "no stray entry was created by the blank row");
            GetPayloadBody(queryResp.Records!.First(r => r.Shortname == "kept"))
                .GetProperty("name").GetString().ShouldBe("Updated");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // `auto` on the CREATE path still mints a fresh 8-hex name, as before.
    [FactIfPg]
    public async Task Csv_Import_AutoShortnameCell_MintsGeneratedName()
    {
        const string space = "itest_csv_autocreate";
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        try
        {
            await CleanupAsync(client, space);
            await SeedSpaceAsync(client, space);

            var csv = "shortname,name\r\nauto,First\r\nAUTO,Second\r\n";
            var importResp = await UploadCsvAsync(client,
                resourceType: "content", space: space, subpath: "items", schema: "goods",
                csvBytes: Encoding.UTF8.GetBytes(csv));

            importResp.Status.ShouldBe(Status.Success);
            ExtractInt(importResp.Attributes!["inserted"]).ShouldBe(2,
                "both rows mint their own name instead of colliding on the text `auto`");

            var queryResp = await PostJson(client, "/managed/query",
                "{\"space_name\":\"" + space + "\",\"type\":\"subpath\"," +
                "\"subpath\":\"items\",\"filter_schema_names\":[],\"limit\":50}");
            var names = queryResp.Records!.Select(r => r.Shortname).ToList();
            names.Count.ShouldBe(2);
            names.ShouldNotContain("auto");
            foreach (var n in names)
                System.Text.RegularExpressions.Regex.IsMatch(n, "^[0-9a-f]{8}$")
                    .ShouldBeTrue($"auto-generated shortname '{n}' must match <8hex>");
        }
        finally
        {
            await CleanupAsync(client, space);
        }
    }

    // ---------------- helpers ----------------

    // Sets up space + items folder + schema folder + a permissive `goods` schema
    // for the per-test isolated spaces used by the case-sensitivity / auto-shortname
    // tests. The original Csv_Import_Then_Export test inlines this against `itest_csv`
    // and isn't refactored to use this helper.
    private static async Task SeedSpaceAsync(HttpClient client, string space)
    {
        (await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"" + space +
            "\",\"attributes\":{\"hide_space\":true,\"is_active\":true}}]}"))
            .ShouldBeTrue("space create");
        (await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"items\"," +
            "\"attributes\":{\"is_active\":true}}]}"))
            .ShouldBeTrue("folder create");
        // schema/ may already exist (auto-created by resource_folders_creation plugin)
        await PostOk(client, "/managed/request",
            "{\"space_name\":\"" + space + "\",\"request_type\":\"create\",\"records\":[" +
            "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"schema\"," +
            "\"attributes\":{\"is_active\":true}}]}");
        await UploadSchemaAsync(client,
            shortname: "goods",
            schemaJson: """{"title":"goods","type":"object","additionalProperties":true}""",
            space: space);
    }

    private static async Task CleanupAsync(HttpClient client, string space = "itest_csv")
    {
        // Fire-and-forget — ok if the space doesn't exist yet.
        var body = "{\"space_name\":\"" + space + "\",\"request_type\":\"delete\",\"records\":[" +
                   "{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"" + space + "\",\"attributes\":{}}]}";
        using var _ = await client.PostAsync("/managed/request", new StringContent(
            body, Encoding.UTF8, "application/json"));
    }

    private static async Task<bool> PostOk(HttpClient client, string url, string body)
    {
        var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!.Status == Status.Success;
    }

    private static async Task<Response> PostJson(HttpClient client, string url, string body)
    {
        var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!;
    }

    private static async Task UploadSchemaAsync(HttpClient client, string shortname, string schemaJson,
        string space = "itest_csv")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(space), "space_name");

        var recordJson =
            "{\"resource_type\":\"schema\",\"subpath\":\"schema\",\"shortname\":\"" + shortname +
            "\",\"attributes\":{\"payload\":{\"content_type\":\"json\",\"body\":\"" + shortname + ".json\"}}}";
        var recordPart = new ByteArrayContent(Encoding.UTF8.GetBytes(recordJson));
        recordPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordPart, "request_record", "request_record.json");

        var payloadPart = new ByteArrayContent(Encoding.UTF8.GetBytes(schemaJson));
        payloadPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(payloadPart, "payload_file", shortname + ".json");

        var resp = await client.PostAsync("/managed/resource_with_payload", form);
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        parsed!.Status.ShouldBe(Status.Success, $"schema upload for {shortname}");
    }

    private static async Task<Response> UploadCsvAsync(
        HttpClient client, string resourceType, string space, string subpath, string schema, byte[] csvBytes,
        bool isUpdate = false)
    {
        using var form = new MultipartFormDataContent();
        var csvPart = new ByteArrayContent(csvBytes);
        csvPart.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(csvPart, "resources_file", "rows.csv");

        var url = $"/managed/resources_from_csv/{resourceType}/{space}/{subpath}/{schema}";
        if (isUpdate) url += "?is_update=true";
        var resp = await client.PostAsync(url, form);
        var parsed = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return parsed!;
    }

    // Reads an int out of a Dictionary<string,object> value that may arrive as a
    // boxed JsonElement or as a raw int/long (defensive — System.Text.Json source-gen
    // round-trips this field as JsonElement from a Response deserialization).
    private static int ExtractInt(object value) => value switch
    {
        JsonElement el => el.ValueKind == JsonValueKind.Number ? el.GetInt32() : int.Parse(el.ToString()!),
        int i          => i,
        long l         => (int)l,
        _              => int.Parse(value.ToString()!),
    };

    // After /managed/query with retrieve_json_payload=true, record.Attributes["payload"]
    // is a JsonElement (System.Text.Json source-gen materializes `object` values as
    // JsonElement on the deserialize side). Drill in one level to payload.body.
    private static JsonElement GetPayloadBody(Record record)
    {
        record.Attributes.ShouldNotBeNull();
        var payload = (JsonElement)record.Attributes!["payload"];
        return payload.GetProperty("body");
    }
}
