using System.ComponentModel;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using ModelContextProtocol.Server;

namespace Dmart.Api.Mcp.Sdk;

// SPIKE — not a migration. One tool (dmart_query) expressed the way the
// official MCP C# SDK 2.0 wants it, mounted alongside the hand-rolled server
// rather than replacing it, to answer one question: does the SDK survive
// dmart's NativeAOT publish, where ILC warnings are errors?
//
// dmart_query is the representative case on purpose. It has everything that
// would break AOT if anything would: nested collections and [EnumMember] enums
// in the input (so the SDK must build a JSON schema for them), a DI-resolved
// service, actor-derived authorization, and dmart's own Response envelope as
// the return type (so the SDK must serialize a type our source-gen context
// owns).
//
// What this deliberately does NOT settle — see the review notes on the SDK:
//   * Authorization. The hand-rolled server calls RequireActor(http) and lets
//     the caller's JWT flow into every service call. Here the actor comes from
//     IHttpContextAccessor, which works but is ambient: the real migration has
//     to decide how MCP requests carry identity under a transport that is
//     "stateless by default" in 2.0.
//   * Session binding. McpEndpoint.ResolveOwnedSession ties Mcp-Session-Id to
//     the authenticated caller (the #133 fix). The SDK has no equivalent
//     notion; reproducing it is the hard part of a real port, not this.
[McpServerToolType]
public sealed class SdkQueryTool
{
    // Typed arguments instead of hand-parsed JsonElement — the SDK derives the
    // tool's input schema from this record, which is most of the reason to
    // adopt it. Compare McpTools.QueryAsync, which spends ~40 lines pulling the
    // same fields out by hand.
    public sealed record QueryArgs(
        [property: Description("Space to query, e.g. \"management\"")] string SpaceName,
        [property: Description("Subpath within the space; defaults to \"/\"")] string? Subpath,
        [property: Description("RediSearch-style expression")] string? Search,
        [property: Description("Query type; defaults to search")] QueryType? Type,
        [property: Description("Restrict to these resource types")] List<ResourceType>? ResourceTypes,
        [property: Description("Restrict to these shortnames")] List<string>? FilterShortnames,
        [property: Description("Max records to return (1-100)")] int? Limit);

    private const int MaxQueryLimit = 100;

    [McpServerTool(Name = "dmart_query_sdk")]
    [Description("Query dmart entries. Mirrors dmart_query on the hand-rolled server.")]
    public static async Task<Response> QueryAsync(
        QueryArgs args,
        QueryService queries,
        IHttpContextAccessor http,
        CancellationToken ct)
    {
        // Same gate as the hand-rolled tool: no ambient service identity, the
        // caller's own actor or nothing.
        var actor = http.HttpContext?.Actor()
            ?? throw new UnauthorizedAccessException("login required");

        var q = new Query
        {
            Type = args.Type ?? QueryType.Search,
            SpaceName = args.SpaceName,
            Subpath = args.Subpath ?? "/",
            Search = args.Search,
            FilterTypes = args.ResourceTypes,
            FilterShortnames = args.FilterShortnames,
            Limit = Math.Min(Math.Max(1, args.Limit ?? 20), MaxQueryLimit),
        };

        return await queries.ExecuteAsync(q, actor, ct);
    }
}
