using System.Globalization;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// GET /managed/directory-feed: what a directory replica pulls from its
// primary (docs/directory-replica.md). It hands out every user's password
// hash, so beyond the managed group's authentication it answers only the bot
// accounts listed in DIRECTORY_FEED_READERS, and nobody when that is empty.
//
//   ?mode=full[&after=<shortname>][&limit=n]
//   ?mode=changes&since=<time>[&after_time=<time>&after=<shortname>][&limit=n]
public static class DirectoryFeedHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/directory-feed", async (HttpContext http, DirectoryFeedService feed,
            ILogger<DirectoryFeedService> log, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (!feed.MayRead(actor))
                return Results.Json(Response.Fail(InternalErrorCode.NOT_ALLOWED,
                    "not a directory feed reader", ErrorTypes.Auth), DmartJsonContext.Default.Response, statusCode: 403);

            var q = http.Request.Query;
            var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, DirectoryFeedService.MaxPage) : 500;
            string? after = q["after"].FirstOrDefault() is { Length: > 0 } a ? a : null;

            DirectoryFeedPage page;
            switch (q["mode"].FirstOrDefault())
            {
                case "full":
                    page = await feed.FullAsync(after, limit, ct);
                    break;
                case "changes" when TryTime(q["since"], out var since):
                    DateTime? afterTime = TryTime(q["after_time"], out var at) ? at : null;
                    page = await feed.ChangesAsync(since, afterTime, after, limit, ct);
                    break;
                default:
                    return Results.Json(Response.Fail(InternalErrorCode.INVALID_DATA,
                        "mode=full, or mode=changes with since", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);
            }
            log.LogInformation("directory feed: {Count} user(s) to {Actor}", page.Users.Count, actor);
            return Results.Json(page, DmartJsonContext.Default.DirectoryFeedPage);
        });
    }

    // Times travel as dmart writes them: local wall clock, no offset.
    private static bool TryTime(Microsoft.Extensions.Primitives.StringValues raw, out DateTime value)
        => DateTime.TryParse(raw.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
