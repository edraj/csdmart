using System.Text.Json;
using Dmart.Models.Enums;

namespace Dmart.Models.Api;

// Mirrors dmart/backend/models/api.py::Query field-for-field. Defaults match dmart's
// Pydantic defaults so omitted fields produce the same wire/behaviour.
public sealed record Query
{
    public required QueryType Type { get; init; }
    public required string SpaceName { get; init; }

    // dmart's DB stores subpaths with a leading slash. Wire callers may send either
    // form (stripped, like "api", or with slash, like "/api"); we normalize so SQL
    // WHERE clauses always query the canonical leading-slash form.
    private readonly string _subpath = "/";
    public required string Subpath
    {
        get => _subpath;
        init => _subpath = Dmart.Models.Core.Locator.NormalizeSubpath(value);
    }
    public bool ExactSubpath { get; init; }
    public List<ResourceType>? FilterTypes { get; init; }
    public List<string> FilterSchemaNames { get; init; } = new() { "meta" };
    public List<string>? FilterShortnames { get; init; } = new();
    public List<string>? FilterTags { get; init; }
    // Length is bounded by the parser, not here: QueryService rewrites Search
    // (join narrowing terms, permission filter-fields-values) before it reaches
    // SQL, so a cap on the wire value alone would not cover what actually gets
    // parsed. See SearchExpressionParser.MaxExpressionLength.
    public string? Search { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public List<string>? ExcludeFields { get; init; }
    public List<string>? IncludeFields { get; init; }
    public Dictionary<string, string> HighlightFields { get; init; } = new();
    public string? SortBy { get; init; }
    public SortType? SortType { get; init; }
    public bool RetrieveJsonPayload { get; init; }
    public bool RetrieveAttachments { get; init; }
    // Nullable on purpose: System.Text.Json source-gen does NOT apply C# property
    // initializers when a key is missing from the incoming JSON, so `= true`
    // would flip to false on any request that omits the field. Python defaults
    // retrieve_total to true, so missing → null → interpreted as true by
    // QueryService. Only an explicit `retrieve_total: false` skips the count.
    public bool? RetrieveTotal { get; init; }

    // Server-set, never from the wire (hence JsonIgnore): the largest exact
    // `total` the count is allowed to compute. 0 means unlimited, which is the
    // Python-parity behaviour and the default.
    //
    // WHY: `total` is a pagination count, and counting is O(matching rows) no
    // matter what indexes exist. On the production instance one subpath holds
    // 2.59M rows, and every page request re-counted all of them — 558,866
    // buffer hits and ~2.4s warm, which under load became ~17s and a wall of
    // client cancellations. Above the cap the count stops early and `total`
    // is reported as a lower bound (see QueryService), which is the difference
    // between scanning 2.59M rows and scanning cap+1 of them.
    //
    // QueryHelper.RunCountAsync returns cap+1 to mean "at least cap"; that
    // sentinel is what QueryService keys the lower-bound flag off.
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalCap { get; init; }
    public bool ValidateSchema { get; init; } = true;
    public bool RetrieveLockStatus { get; init; }
    public string? JqFilter { get; init; }
    public int Limit { get; init; } = 10;
    public int Offset { get; init; }
    public RedisAggregate? AggregationData { get; init; }
    public List<JoinQuery>? Join { get; init; }
}

// Mirrors dmart's models/api.py::JoinQuery exactly.
public sealed record JoinQuery
{
    public required string JoinOn { get; init; }
    public required string Alias { get; init; }
    public JsonElement? Query { get; init; }
    // Nullable so an explicit `"type": null` from a client that emits all
    // fields doesn't get rejected with "expected enum string". A missing
    // key and an explicit null both mean "left join" — QueryService treats
    // anything that isn't Inner/Right/Outer as left.
    public JoinType? Type { get; init; }
}

// Mirrors dmart's models/api.py::RedisAggregate.
public sealed record RedisAggregate
{
    public List<string> GroupBy { get; init; } = new();
    public List<RedisReducer> Reducers { get; init; } = new();
    public List<string> Load { get; init; } = new();
}

// Mirrors dmart's models/api.py::RedisReducer.
public sealed record RedisReducer
{
    public required string ReducerName { get; init; }
    public string? Alias { get; init; }
    public List<string> Args { get; init; } = new();
}
