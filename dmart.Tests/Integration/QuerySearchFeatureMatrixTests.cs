using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Row-level behaviour of the ENTIRE `query.search` grammar against a real
// PostgreSQL instance — one fixture, one assertion style: "this expression
// selects exactly this set of shortnames".
//
// SearchExpressionParserTests pins the SQL TEXT the parser emits; that catches
// emission drift but cannot catch SQL that is well-formed and wrong (a double
// negation, a three-valued-logic hole, a BETWEEN with reversed bounds). Those
// only show up as a wrong row set, which is what these tests assert.
//
// Coverage map (docs/query.md):
//   value forms    plain · quoted · boolean · numeric · existence · glob ·
//                  alternation · range · comparison · null
//   column kinds   scalar text · boolean · timestamp · jsonb array ·
//                  payload path · payload array iteration · subtree wildcard
//   operators      negation · AND (whitespace / `and`) · OR keyword · parens
//
// Permission-scoped searching lives in QuerySearchPermissionsTests.
public class QuerySearchFeatureMatrixTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QuerySearchFeatureMatrixTests(DmartFactory factory) => _factory = factory;

    private (QueryService query, EntryRepository entries, SpaceRepository spaces) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    // ── Fixture ───────────────────────────────────────────────────────────
    // Four entries chosen so that every predicate below has at least one row
    // on each side of it. Meta columns and payload deliberately disagree in
    // places (meta `tags` vs `payload.body.tags`) so a selector that reads the
    // wrong one produces a visibly wrong set rather than a coincidental match.
    //
    //  shortname   meta.tags    is_active  slug        payload.body
    //  ─────────────────────────────────────────────────────────────────────
    //  it_alpha    [red,hot]    true       slug_alpha  name "alpha one"   price 10
    //                                                  env prod  enabled true
    //                                                  tags [a,b]  note "hello world"
    //                                                  items [S1 50 active localhost]
    //  it_beta     [blue]       true       (null)      name "beta two"    price 100
    //                                                  env staging  enabled false
    //                                                  tags [b,c]  note JSON null
    //                                                  items [S2 150 archived remote]
    //  it_gamma    [red]        false      slug_gamma  name "gamma three" price 250
    //                                                  env prod  enabled true
    //                                                  tags [archived]  note ABSENT
    //                                                  items [S3 5 active localhost,
    //                                                         S4 500 pending -]
    //  it_delta    []           true       slug_delta  name "delta four"  price 10
    //                                                  env dev  enabled false
    //                                                  tags []  note "another note"
    //                                                  items []
    private const string Alpha = "it_alpha";
    private const string Beta = "it_beta";
    private const string Gamma = "it_gamma";
    private const string Delta = "it_delta";

    private async Task<string> SeedFixture(EntryRepository entries, SpaceRepository spaces)
    {
        var spaceName = $"sp_{Guid.NewGuid():N}".Substring(0, 12);
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });

        await Seed(entries, spaceName, Alpha, isActive: true, slug: "slug_alpha",
            tags: new() { "red", "hot" },
            body: """
            {"name":"alpha one","price":10,"env":"prod","enabled":true,
             "tags":["a","b"],"note":"hello world",
             "items":[{"sku":"S1","price":50,"status":"active","config":{"host":"localhost"}}]}
            """);

        await Seed(entries, spaceName, Beta, isActive: true, slug: null,
            tags: new() { "blue" },
            body: """
            {"name":"beta two","price":100,"env":"staging","enabled":false,
             "tags":["b","c"],"note":null,
             "items":[{"sku":"S2","price":150,"status":"archived","config":{"host":"remote"}}]}
            """);

        await Seed(entries, spaceName, Gamma, isActive: false, slug: "slug_gamma",
            tags: new() { "red" },
            body: """
            {"name":"gamma three","price":250,"env":"prod","enabled":true,
             "tags":["archived"],
             "items":[{"sku":"S3","price":5,"status":"active","config":{"host":"localhost"}},
                      {"sku":"S4","price":500,"status":"pending"}]}
            """);

        await Seed(entries, spaceName, Delta, isActive: true, slug: "slug_delta",
            tags: new(),
            body: """
            {"name":"delta four","price":10,"env":"dev","enabled":false,
             "tags":[],"note":"another note","items":[]}
            """);

        return spaceName;
    }

    private static async Task Seed(EntryRepository entries, string spaceName, string shortname,
        bool isActive, string? slug, List<string> tags, string body)
    {
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = spaceName,
            Subpath = "/items",
            ResourceType = ResourceType.Content,
            IsActive = isActive,
            Slug = slug,
            Tags = tags,
            OwnerShortname = "dmart",
            // TimeUtils.Now(), NOT DateTime.UtcNow: dmart timestamps are naive
            // LOCAL wall clock end to end (see TimeUtils). A UTC stamp here is
            // off by the host's UTC offset; PostgreSQL happens to mask that
            // through the timestamp↔timestamptz session-timezone coercion, but
            // SQLite's lexicographic text comparison exposes it — the
            // @created_at:[ms ms] assertions failed on any non-UTC machine.
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(body).RootElement.Clone(),
            },
        });
    }

    private static async Task<string[]> RunSearch(QueryService query, string spaceName, string search)
    {
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = spaceName,
            Subpath = "items",
            Limit = 100,
            RetrieveJsonPayload = true,
            Search = search,
        }, "dmart");
        resp.Status.ShouldBe(Status.Success);
        resp.Records.ShouldNotBeNull();
        return resp.Records!.Select(r => r.Shortname).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    // Every case is (search expression, exactly these shortnames). Asserting
    // the EXACT set — not just "contains" — is what makes an over-broad
    // predicate fail; a widened filter still contains the expected rows.
    private async Task AssertSelects(string search, params string[] expected)
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var hits = await RunSearch(query, sn, search);
            hits.ShouldBe(expected.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                $"search: {search}");
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    // ── Free text ─────────────────────────────────────────────────────────

    [TheoryIfPg]
    // A bare word scans shortname + payload/displayname/description/tags text.
    [InlineData("alpha", new[] { Alpha })]
    [InlineData("staging", new[] { Beta })]
    // Two words AND together.
    [InlineData("gamma three", new[] { Gamma })]
    [InlineData("alpha staging", new string[0])]
    // Case-insensitive.
    [InlineData("ALPHA", new[] { Alpha })]
    // Meta tags are part of the free-text fan-out.
    [InlineData("blue", new[] { Beta })]
    public Task FreeText(string search, string[] expected) => AssertSelects(search, expected);

    // ── Scalar text columns ───────────────────────────────────────────────

    [TheoryIfPg]
    [InlineData("@shortname:it_alpha", new[] { Alpha })]
    [InlineData("-@shortname:it_alpha", new[] { Beta, Gamma, Delta })]
    [InlineData("@shortname:it_alpha|it_beta", new[] { Alpha, Beta })]
    [InlineData("-@shortname:it_alpha|it_beta", new[] { Gamma, Delta })]
    // Glob: `*` inside a scalar value is ILIKE, not the existence sentinel.
    [InlineData("@shortname:it_a*", new[] { Alpha })]
    [InlineData("@shortname:*ta", new[] { Beta, Delta })]
    [InlineData("@shortname:*mm*", new[] { Gamma })]
    // Existence / absence on a nullable column.
    [InlineData("@slug:*", new[] { Alpha, Gamma, Delta })]
    [InlineData("-@slug:*", new[] { Beta })]
    // Lexical range over text.
    [InlineData("@shortname:[it_a it_c]", new[] { Alpha, Beta })]
    [InlineData("-@shortname:[it_a it_c]", new[] { Gamma, Delta })]
    // Reversed bounds are normalised, so this must not return the empty set.
    [InlineData("@shortname:[it_c it_a]", new[] { Alpha, Beta })]
    public Task ScalarColumns(string search, string[] expected) => AssertSelects(search, expected);

    // ── Boolean column ────────────────────────────────────────────────────

    [TheoryIfPg]
    [InlineData("@is_active:true", new[] { Alpha, Beta, Delta })]
    [InlineData("@is_active:false", new[] { Gamma })]
    [InlineData("-@is_active:true", new[] { Gamma })]
    [InlineData("@is_active:!true", new[] { Gamma })]
    [InlineData("@is_active:true|false", new[] { Alpha, Beta, Gamma, Delta })]
    public Task BooleanColumn(string search, string[] expected) => AssertSelects(search, expected);

    // ── JSONB array column (meta tags) ────────────────────────────────────

    [TheoryIfPg]
    [InlineData("@tags:red", new[] { Alpha, Gamma })]
    [InlineData("-@tags:red", new[] { Beta, Delta })]
    [InlineData("@tags:red|blue", new[] { Alpha, Beta, Gamma })]
    // De Morgan: "neither red nor hot", not "not (red and hot)".
    [InlineData("-@tags:red|hot", new[] { Beta, Delta })]
    // Same field twice accumulates with AND — both tags must be present.
    [InlineData("@tags:red @tags:hot", new[] { Alpha })]
    [InlineData("@tags:red and @tags:blue", new string[0])]
    public Task JsonbArrayColumn(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload paths: plain / alternation / negation ─────────────────────

    [TheoryIfPg]
    [InlineData("@payload.body.env:prod", new[] { Alpha, Gamma })]
    [InlineData("@payload.body.env:prod|dev", new[] { Alpha, Gamma, Delta })]
    [InlineData("-@payload.body.env:prod", new[] { Beta, Delta })]
    [InlineData("-@payload.body.env:prod|staging", new[] { Delta })]
    // Quoted value keeps the space; unquoted would tokenize as two terms.
    [InlineData("@payload.body.name:\"alpha one\"", new[] { Alpha })]
    // Containment is exact — a substring must NOT match without a wildcard.
    [InlineData("@payload.body.name:alpha", new string[0])]
    public Task PayloadValues(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload numerics: equality, comparison, range ─────────────────────

    [TheoryIfPg]
    [InlineData("@payload.body.price:100", new[] { Beta })]
    [InlineData("-@payload.body.price:100", new[] { Alpha, Gamma, Delta })]
    [InlineData("@payload.body.price:>100", new[] { Gamma })]
    [InlineData("@payload.body.price:>=100", new[] { Beta, Gamma })]
    [InlineData("@payload.body.price:<100", new[] { Alpha, Delta })]
    [InlineData("@payload.body.price:<=10", new[] { Alpha, Delta })]
    [InlineData("@payload.body.price:!10", new[] { Beta, Gamma })]
    [InlineData("@payload.body.price:[10 100]", new[] { Alpha, Beta, Delta })]
    [InlineData("@payload.body.price:[100,10]", new[] { Alpha, Beta, Delta })]
    [InlineData("-@payload.body.price:[10 100]", new[] { Gamma })]
    public Task PayloadNumerics(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload booleans ──────────────────────────────────────────────────

    [TheoryIfPg]
    [InlineData("@payload.body.enabled:true", new[] { Alpha, Gamma })]
    [InlineData("@payload.body.enabled:false", new[] { Beta, Delta })]
    [InlineData("-@payload.body.enabled:true", new[] { Beta, Delta })]
    public Task PayloadBooleans(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload null / existence ──────────────────────────────────────────

    [TheoryIfPg]
    // `:null` covers BOTH an explicit JSON null and a missing key.
    [InlineData("@payload.body.note:null", new[] { Beta, Gamma })]
    [InlineData("-@payload.body.note:null", new[] { Alpha, Delta })]
    // The existence sentinel is a SQL-NULL test on the extracted path, so a
    // stored JSON null still counts as "present" — the distinction between
    // `:*` and `:null` that trips people up.
    [InlineData("@payload.body.note:*", new[] { Alpha, Beta, Delta })]
    [InlineData("-@payload.body.note:*", new[] { Gamma })]
    public Task PayloadNullAndExistence(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload wildcards ─────────────────────────────────────────────────

    [TheoryIfPg]
    [InlineData("@payload.body.name:*two*", new[] { Beta })]
    [InlineData("@payload.body.name:alpha*", new[] { Alpha })]
    [InlineData("@payload.body.name:*four", new[] { Delta })]
    [InlineData("@payload.body.name:*a*", new[] { Alpha, Beta, Gamma, Delta })]
    // Negated wildcard keeps rows where the field is missing or non-string.
    [InlineData("-@payload.body.note:*note*", new[] { Alpha, Beta, Gamma })]
    // Subtree wildcard scans the rendered JSON at that depth, keys included.
    [InlineData("@payload.body.*:staging", new[] { Beta })]
    [InlineData("@payload.*:localhost", new[] { Alpha, Gamma })]
    public Task PayloadWildcards(string search, string[] expected) => AssertSelects(search, expected);

    // ── Payload array iteration ───────────────────────────────────────────

    [TheoryIfPg]
    // Primitive arrays.
    [InlineData("@payload.body.tags[]:b", new[] { Alpha, Beta })]
    [InlineData("@payload.body.tags[]:a|c", new[] { Alpha, Beta })]
    // REGRESSION: `-@arr[]:v` means "does not contain v". Before the
    // double-negation fix this emitted NOT EXISTS(e != v) — "EVERY element is
    // v" — and returned {gamma, delta} instead of everything but gamma.
    [InlineData("-@payload.body.tags[]:archived", new[] { Alpha, Beta, Delta })]
    [InlineData("-@payload.body.tags[]:a|b", new[] { Gamma, Delta })]
    // Object arrays with a sub-path.
    [InlineData("@payload.body.items[].sku:S1", new[] { Alpha })]
    [InlineData("@payload.body.items[].status:active", new[] { Alpha, Gamma })]
    [InlineData("@payload.body.items[].status:active|pending", new[] { Alpha, Gamma })]
    [InlineData("-@payload.body.items[].status:archived", new[] { Alpha, Gamma, Delta })]
    // Comparison / range over elements: "ANY element satisfies".
    [InlineData("@payload.body.items[].price:>100", new[] { Beta, Gamma })]
    [InlineData("@payload.body.items[].price:[10 100]", new[] { Alpha })]
    [InlineData("@payload.body.items[].price:5", new[] { Gamma })]
    // Nested sub-path inside the element.
    [InlineData("@payload.body.items[].config.host:localhost", new[] { Alpha, Gamma })]
    [InlineData("-@payload.body.items[].config.host:localhost", new[] { Beta, Delta })]
    public Task PayloadArrayIteration(string search, string[] expected) => AssertSelects(search, expected);

    // ── Boolean operators and grouping ────────────────────────────────────

    [TheoryIfPg]
    // Whitespace = AND.
    [InlineData("@is_active:true @payload.body.env:prod", new[] { Alpha })]
    // `and` is a no-op synonym for whitespace.
    [InlineData("@is_active:true and @payload.body.env:prod", new[] { Alpha })]
    // `or` unions.
    [InlineData("@payload.body.env:staging or @payload.body.env:dev", new[] { Beta, Delta })]
    // AND binds tighter than OR: (active AND prod) OR staging.
    [InlineData("@is_active:true @payload.body.env:prod or @payload.body.env:staging",
        new[] { Alpha, Beta })]
    // Parens override that: (prod OR staging) AND active.
    [InlineData("(@payload.body.env:prod or @payload.body.env:staging) @is_active:true",
        new[] { Alpha, Beta })]
    // Whitespace between groups is AND, not OR (2026-06-20 change).
    [InlineData("(@payload.body.env:prod) (@is_active:true)", new[] { Alpha })]
    // Nested groups.
    [InlineData("((@payload.body.env:prod or @payload.body.env:dev) @is_active:true) or @tags:blue",
        new[] { Alpha, Beta, Delta })]
    // Free text composes with selectors on both sides of an `or`.
    [InlineData("alpha or @payload.body.env:dev", new[] { Alpha, Delta })]
    // Mixed free text AND selector.
    [InlineData("three @payload.body.env:prod", new[] { Gamma })]
    // Lenient recovery: stray/unbalanced parens must not change the meaning.
    [InlineData("@payload.body.env:prod) or @payload.body.env:dev", new[] { Alpha, Gamma, Delta })]
    [InlineData("(@payload.body.env:prod or @payload.body.env:dev", new[] { Alpha, Gamma, Delta })]
    public Task BooleanOperators(string search, string[] expected) => AssertSelects(search, expected);

    // ── Degenerate input ──────────────────────────────────────────────────

    [TheoryIfPg]
    // An expression that contributes no clause must not narrow the page.
    [InlineData("", new[] { Alpha, Beta, Gamma, Delta })]
    [InlineData("   ", new[] { Alpha, Beta, Gamma, Delta })]
    [InlineData("()", new[] { Alpha, Beta, Gamma, Delta })]
    [InlineData("or", new[] { Alpha, Beta, Gamma, Delta })]
    [InlineData("and", new[] { Alpha, Beta, Gamma, Delta })]
    // A selector the parser rejects (bad identifier) is dropped, not fatal.
    [InlineData("@BadName:x", new[] { Alpha, Beta, Gamma, Delta })]
    // Dangling operators reduce to the non-empty operand.
    [InlineData("@payload.body.env:prod or", new[] { Alpha, Gamma })]
    [InlineData("or @payload.body.env:prod", new[] { Alpha, Gamma })]
    public Task DegenerateInput(string search, string[] expected) => AssertSelects(search, expected);

    // ── Timestamps ────────────────────────────────────────────────────────
    // Every fixture row is written "now", so absolute bounds are computed
    // from the clock rather than hard-coded.

    [FactIfPg]
    public async Task Timestamp_Range_And_Comparison_Select_By_Created_At()
    {
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var all = new[] { Alpha, Beta, Gamma, Delta }.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var hourAgo = nowMs - 3_600_000;
            var hourAhead = nowMs + 3_600_000;

            // Numeric values on a timestamp column are Unix MILLISECONDS.
            (await RunSearch(query, sn, $"@created_at:[{hourAgo} {hourAhead}]")).ShouldBe(all);
            (await RunSearch(query, sn, $"@created_at:>{hourAgo}")).ShouldBe(all);
            (await RunSearch(query, sn, $"@created_at:<{hourAgo}")).ShouldBeEmpty();
            (await RunSearch(query, sn, $"-@created_at:[{hourAgo} {hourAhead}]")).ShouldBeEmpty();

            // ISO strings cast to timestamptz instead.
            //
            // Bounds come from DateTime.Now, NOT UtcNow: rows are stamped by
            // TimeUtils.Now(), which is DateTime.Now, because dmart is
            // timezone-less end to end and stores local wall-clock verbatim.
            // Building the range from UtcNow made this fail every night in the
            // window between local midnight and UTC midnight (three hours at
            // +03): a row stamped 2026-09-04T00:52 local fell past an upper
            // bound of "2026-09-04" derived from a UTC date still reading
            // 2026-09-03, and the range returned nothing.
            //
            // Widened to yesterday..tomorrow as well, so a local midnight
            // crossing BETWEEN seeding the rows and running the query cannot
            // reintroduce the same flake from a different direction. The
            // assertion is about ISO strings casting to timestamptz at all,
            // not about single-day precision.
            var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var tomorrow = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd");
            (await RunSearch(query, sn, $"@created_at:[{yesterday},{tomorrow}]")).ShouldBe(all);
            (await RunSearch(query, sn, $"@updated_at:[{yesterday},{tomorrow}]")).ShouldBe(all);

            // FOOTGUN, pinned deliberately. Comparison operators only engage
            // for NUMERIC values (ComparisonRegex requires it), so
            // `>2026-01-01` stays the literal value ">2026-01-01" and the
            // emitted SQL is an EQUALITY: `updated_at = '>2026-01-01'::
            // timestamptz`. PostgreSQL then parses that literal leniently —
            // it ignores the leading '>' and yields midnight — so the query
            // neither errors nor compares: it asks for rows stamped exactly
            // at 00:00:00 on that date, and matches nothing.
            //
            // Use epoch millis for `>`/`<` on a timestamp column, or a range
            // for ISO bounds. If this ever starts returning rows, the
            // comparison path changed and the docs need updating with it.
            (await RunSearch(query, sn, $"@updated_at:>{today}")).ShouldBeEmpty();
            (await RunSearch(query, sn, $"@updated_at:>{tomorrow}")).ShouldBeEmpty();
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    // ── Fail-closed ───────────────────────────────────────────────────────

    [FactIfPg]
    public async Task Over_Length_Expression_Returns_Nothing_Rather_Than_Everything()
    {
        // The parser answers FALSE above MaxExpressionLength. End-to-end that
        // has to mean an empty page: silently dropping the expression would
        // also drop the permission clause folded into the same string.
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var huge = "@payload.body.env:" + new string('p', 64 * 1024 + 1);
            (await RunSearch(query, sn, huge)).ShouldBeEmpty();
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }

    // ── Search applies to the other query types too ───────────────────────

    [FactIfPg]
    public async Task Search_Filters_Search_Aggregation_Counters_And_Tags_Query_Types()
    {
        // docs/query.md lists which `type`s consult `search`. Subpath is
        // covered by every case above; these are the remaining ones whose row
        // pool the same expression has to narrow.
        var (query, entries, spaces) = Resolve();
        var sn = await SeedFixture(entries, spaces);
        try
        {
            var search = "@payload.body.env:prod";

            var asSearch = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search,
                SpaceName = sn,
                Subpath = "items",
                Limit = 100,
                Search = search,
            }, "dmart");
            asSearch.Status.ShouldBe(Status.Success);
            asSearch.Records!.Select(r => r.Shortname)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ShouldBe(new[] { Alpha, Gamma });

            var counters = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Counters,
                SpaceName = sn,
                Subpath = "items",
                Search = search,
            }, "dmart");
            counters.Status.ShouldBe(Status.Success);
            counters.Attributes.ShouldNotBeNull();
            Convert.ToInt32(counters.Attributes!["total"]).ShouldBe(2);

            // `tags` counts the meta tags of the rows the search selected —
            // alpha [red,hot] + gamma [red] ⇒ red twice, hot once.
            var tagsResp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Tags,
                SpaceName = sn,
                Subpath = "items",
                Search = search,
            }, "dmart");
            tagsResp.Status.ShouldBe(Status.Success);
            var json = JsonSerializer.Serialize(tagsResp.Records!.Select(r => r.Attributes));
            json.ShouldContain("red");
            json.ShouldNotContain("blue");   // beta was filtered out
        }
        finally { try { await spaces.DeleteAsync(sn); } catch { } }
    }
}
