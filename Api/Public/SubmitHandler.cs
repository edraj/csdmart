using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Public;

public static class SubmitHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // POST /public/submit/{space}/{schema}/{subpath} — implicit content
        g.MapPost("/submit/{space}/{schema}/{subpath}",
            async (string space, string schema, string subpath, HttpRequest req, EntryService entries, CancellationToken ct) =>
                await SubmitAsync(space, ResourceType.Content, schema, subpath, workflow: null, req, entries, ct));

        // POST /public/submit/{space}/{resource_type}/{schema}/{subpath}
        g.MapPost("/submit/{space}/{resource_type}/{schema}/{subpath}",
            async (string space, string resource_type, string schema, string subpath, HttpRequest req, EntryService entries, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Response.Fail("bad_type", "unknown resource type");
                return await SubmitAsync(space, rt, schema, subpath, workflow: null, req, entries, ct);
            });

        // POST /public/submit/{space}/{resource_type}/{workflow}/{schema}/{subpath}
        g.MapPost("/submit/{space}/{resource_type}/{workflow}/{schema}/{subpath}",
            async (string space, string resource_type, string workflow, string schema, string subpath, HttpRequest req, EntryService entries, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Response.Fail("bad_type", "unknown resource type");
                return await SubmitAsync(space, rt, schema, subpath, workflow, req, entries, ct);
            });
    }

    private static async Task<Response> SubmitAsync(
        string space, ResourceType rt, string schema, string subpath, string? workflow,
        HttpRequest req, EntryService entries, CancellationToken ct)
    {
        // Read the body as a raw JsonElement so we can carry it into Payload.Body losslessly.
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var body = doc.RootElement.Clone();
        string shortname = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("shortname", out var sn) && sn.ValueKind == JsonValueKind.String
            ? sn.GetString()!
            : Guid.NewGuid().ToString("n")[..8];

        var entry = new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = "/" + subpath.TrimStart('/'),
            ResourceType = rt,
            OwnerShortname = "anonymous",
            WorkflowShortname = workflow,
            State = workflow is null ? null : "submitted",
            IsOpen = workflow is null ? null : true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = schema,
                Body = body,
            },
        };
        // Public submit deliberately bypasses the actor permission check.
        var result = await entries.CreateAsync(entry, actor: "anonymous", ct);
        return result.IsOk
            ? Response.Ok(attributes: new() { ["uuid"] = result.Value!.Uuid, ["shortname"] = result.Value.Shortname })
            : Response.Fail(result.ErrorCode!, result.ErrorMessage!);
    }
}
