using Dmart.Api;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Public;

public static class PayloadHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/payload/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   AttachmentRepository attachments, EntryService entries, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt)) return Results.BadRequest();
                var parts = RouteParts.SplitPayloadParts(rest);
                if (parts is null) return Results.BadRequest();
                var (subpath, shortname, _schema, ext) = parts.Value;
                return await Dmart.Api.Managed.PayloadHandler.ServePayloadAsync(
                    rt, space, subpath, shortname, ext, attachments, entries, ct);
            });
}
