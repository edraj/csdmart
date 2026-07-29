using System.Text.Json.Serialization;
using Dmart.Models.Api;

namespace Dmart.Api.Mcp.Sdk;

// SPIKE. The SDK serializes tool arguments and results through its own
// resolver chain (McpJsonUtilities + AIJsonUtilities). Under AOT there is no
// reflection fallback, so every type that crosses that boundary must be in a
// source-generated context the chain can see — otherwise the server throws at
// STARTUP, while building tool schemas, not on first call.
//
// This is the shape of the real migration cost: one context entry per tool
// argument and result type. dmart already works this way everywhere else
// (DmartJsonContext), so it is idiomatic here rather than a new burden.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SdkQueryTool.QueryArgs))]
[JsonSerializable(typeof(Response))]
public partial class McpSdkJsonContext : JsonSerializerContext;
