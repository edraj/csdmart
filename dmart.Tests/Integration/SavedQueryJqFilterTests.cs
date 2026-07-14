using System.Net;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A top-level `jq_filter` stored in a saved-query entry must take effect when
// the query is run via POST /managed/execute/query/{space}, exactly as it does
// on the direct POST /managed/query path.
//
// The top-level jq_filter is applied by JqEnvelope.WriteAsync at the HTTP
// handler layer (Api/Managed/QueryHandler.cs), NOT inside QueryService.Execute-
// Async (which only consumes jq_filter for join sub-queries). The saved-query
// execute path (ExecuteTaskHandler) must therefore route its successful
// Response through JqEnvelope too, using the jq_filter resolved from the stored
// Query.
//
// This class pins both paths:
//   1. Direct_Query_Applies_Top_Level_JqFilter — positive control on /managed/query.
//   2. Saved_Query_Applies_Top_Level_JqFilter  — the saved-query execute path
//      applies the SAME filter and returns reshaped records.
public sealed class SavedQueryJqFilterTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SavedQueryJqFilterTests(DmartFactory factory) => _factory = factory;

    // A top-level jq_filter that collapses every record down to ONLY its
    // shortname key. Observable proof it ran: `shortname` survives, everything
    // else (resource_type, subpath, attributes, ...) is gone.
    private const string JqFilter = "{shortname}";

    // The Query the saved-query entry stores and the direct call sends — same
    // filter, same target, so the only variable is the execution path.
    private static string QueryJson(bool withJqFilter) => $$"""
    {
      "type": "search",
      "space_name": "management",
      "subpath": "/users",
      "filter_types": ["user"],
      "filter_schema_names": [],
      "search": "@shortname:dmart",
      "limit": 5{{(withJqFilter ? $",\n      \"jq_filter\": \"{JqFilter}\"" : "")}}
    }
    """;

    [FactIfPg]
    public async Task Direct_Query_Applies_Top_Level_JqFilter()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            var body = new StringContent(QueryJson(withJqFilter: true), Encoding.UTF8, "application/json");
            var resp = await user.Client.PostAsync("/managed/query", body);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("status").GetString().ShouldBe("success");

            var records = root.GetProperty("records");
            records.GetArrayLength().ShouldBeGreaterThan(0);

            var shortnames = ShortnamesOf(records);
            shortnames.ShouldContain("dmart");

            // Every returned record must have been collapsed to {shortname}:
            // shortname present, resource_type stripped. That only happens if
            // the top-level jq_filter actually executed.
            foreach (var rec in records.EnumerateArray())
            {
                rec.TryGetProperty("shortname", out _).ShouldBeTrue();
                rec.TryGetProperty("resource_type", out _).ShouldBeFalse(
                    "direct /managed/query must apply the top-level jq_filter and strip non-shortname keys");
            }
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Saved_Query_Applies_Top_Level_JqFilter()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var taskShortname = $"itest_jqrep_{Guid.NewGuid():N}"[..24];

        // Store the jq_filter-bearing Query as a saved-query entry (schema
        // "query" → ExecuteTaskHandler treats the payload body as the Query).
        var queryBody = JsonDocument.Parse(QueryJson(withJqFilter: true)).RootElement.Clone();
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = taskShortname,
            SpaceName = "management",
            Subpath = "/reports",
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = "query",
                Body = queryBody,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            var body = new StringContent($$"""
            {
              "resource_type": "content",
              "subpath": "/reports",
              "shortname": "{{taskShortname}}"
            }
            """, Encoding.UTF8, "application/json");

            var resp = await user.Client.PostAsync("/managed/execute/query/management", body);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("status").GetString().ShouldBe("success");

            var records = root.GetProperty("records");
            records.GetArrayLength().ShouldBeGreaterThan(0);

            var shortnames = ShortnamesOf(records);
            shortnames.ShouldContain("dmart");

            // The saved-query execute path must run the stored top-level
            // jq_filter through JqEnvelope just like /managed/query does: every
            // record is collapsed to {shortname}, so resource_type is gone.
            foreach (var rec in records.EnumerateArray())
            {
                rec.TryGetProperty("shortname", out _).ShouldBeTrue();
                rec.TryGetProperty("resource_type", out _).ShouldBeFalse(
                    "saved-query execute path must apply the stored top-level jq_filter and strip non-shortname keys");
            }
        }
        finally
        {
            try { await entries.DeleteAsync("management", "/reports", taskShortname, ResourceType.Content); } catch { }
            await user.Cleanup();
        }
    }

    // Regression guard for the refactor that routes the execute endpoint's
    // result through JqEnvelope on success: FAILURES must still be mapped to
    // their proper HTTP status by FailedResponseFilter (here 404), not
    // flattened to 200 by the direct-write path.
    [FactIfPg]
    public async Task Saved_Query_NotFound_Still_Maps_To_404()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            var body = new StringContent($$"""
            {
              "resource_type": "content",
              "subpath": "/reports",
              "shortname": "itest_missing_{{Guid.NewGuid():N}}"
            }
            """, Encoding.UTF8, "application/json");

            var resp = await user.Client.PostAsync("/managed/execute/query/management", body);
            resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally { await user.Cleanup(); }
    }

    private static List<string?> ShortnamesOf(JsonElement records) =>
        records.EnumerateArray()
            .Select(r => r.TryGetProperty("shortname", out var sn) ? sn.GetString() : null)
            .ToList();
}
