using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmart.Api.Mcp;

// Wire-format types for Model Context Protocol over JSON-RPC 2.0.
//
// MCP spec version pinned: 2025-03-26 (Streamable HTTP transport).
// https://modelcontextprotocol.io/specification/2025-03-26
//
// Property names on the wire are camelCase (MCP convention), NOT snake_case
// like the rest of dmart. Serialization goes through McpJsonContext, which
// sets JsonKnownNamingPolicy.CamelCase for just these types.

// ---- JSON-RPC 2.0 envelope ----

// `Id` is `JsonElement?` because JSON-RPC allows number, string, or null.
// We echo it back verbatim on the response.
public sealed record McpRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; init; }
    public string Method { get; set; } = "";
    public JsonElement? Params { get; init; }
}

public sealed record McpResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; init; }
    public JsonElement? Result { get; init; }
    public McpError? Error { get; init; }
}

// Standard JSON-RPC error codes plus MCP-specific extensions:
//   -32700 parse error, -32600 invalid request, -32601 method not found,
//   -32602 invalid params, -32603 internal error
//   -32002 unauthenticated (MCP extension)
public sealed record McpError(int Code, string Message, JsonElement? Data = null);

// ---- initialize method ----

public sealed record InitializeParams
{
    public string ProtocolVersion { get; set; } = "";
    public JsonElement? Capabilities { get; init; }
    public ClientInfo? ClientInfo { get; init; }
}

public sealed record ClientInfo(string Name, string Version);

public sealed record InitializeResult
{
    public string ProtocolVersion { get; set; } = "";
    public ServerCapabilities Capabilities { get; set; } = new();
    public ServerInfo ServerInfo { get; set; } = new("dmart", "0.1.0");
}

public sealed record ServerInfo(string Name, string Version);

public sealed record ServerCapabilities
{
    // v0.1: tools only; resources is a stub with list support, no subscription;
    // prompts/logging/completion deferred.
    public ToolsCapability? Tools { get; set; } = new();
    public ResourcesCapability? Resources { get; set; } = new();
}

public sealed record ToolsCapability
{
    // We don't notify on tools list changes yet.
    public bool? ListChanged { get; set; } = false;
}

public sealed record ResourcesCapability
{
    public bool? ListChanged { get; set; } = false;
    public bool? Subscribe { get; set; } = false;
}

// ---- tools/list method ----

public sealed record ToolsListResult(IReadOnlyList<McpTool> Tools);

public sealed record McpTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonElement InputSchema { get; init; }
}

// ---- tools/call method ----

public sealed record ToolsCallParams
{
    public string Name { get; set; } = "";
    public JsonElement? Arguments { get; init; }
}

public sealed record ToolsCallResult(IReadOnlyList<ToolContent> Content, bool IsError = false);

public sealed record ToolContent(string Type, string Text);

// ---- resources/list + resources/read (Phase 3 scaffolding, partly used in Phase 1) ----

public sealed record ResourcesListResult(IReadOnlyList<McpResource> Resources);

public sealed record McpResource
{
    public string Uri { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; init; }
    public string? MimeType { get; init; }
}

public sealed record ResourcesReadParams
{
    public string Uri { get; set; } = "";
}

public sealed record ResourcesReadResult(IReadOnlyList<ResourceContents> Contents);

public sealed record ResourceContents
{
    public string Uri { get; set; } = "";
    public string? MimeType { get; init; }
    public string? Text { get; init; }
}
