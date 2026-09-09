using System.Net;
using System.Net.Http.Headers;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /public/payload is the route the inline-rendering change is actually FOR: a
// payload URL pasted into a browser, or embedded in a page, carries no bearer
// token. It shares ServePayloadAsync with /managed/payload but pins the actor to
// "anonymous", and it had no coverage at all — every disposition test hit the
// managed route with a logged-in client, so a regression in the anonymous
// permission walk, or in the route wiring, would have shipped green.
[Collection(AnonymousWorldCollection.Name)]
public sealed class PublicPayloadAnonymousTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicPayloadAnonymousTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Anonymous_Gets_The_Media_Inline_With_No_Token()
        => await WithAnonymousMediaAsync("shot", "jpeg", ContentType.ImageJpeg, async (client, url) =>
        {
            var resp = await client.GetAsync(url);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"GET {url} -> {await resp.Content.ReadAsStringAsync()}");
            resp.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
            // Same same-origin framing relaxation as the managed route — an
            // embedded viewer is the whole point of the public one.
            resp.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("SAMEORIGIN");
            (await resp.Content.ReadAsByteArrayAsync()).ShouldBe(Bytes(64));
        });

    [FactIfPg]
    public async Task Anonymous_Can_Force_A_Download()
        => await WithAnonymousMediaAsync("shot", "jpeg", ContentType.ImageJpeg, async (client, url) =>
        {
            var resp = await client.GetAsync(url + "?download=true");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("shot.jpeg");
        });

    [FactIfPg]
    public async Task Anonymous_Can_Seek_Within_A_Video()
        => await WithAnonymousMediaAsync("clip", "mp4", ContentType.Video, async (client, url) =>
        {
            // The embedded-player case: <video> seeks with Range, and it does so
            // over the anonymous route as often as the authenticated one.
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(16, 47);
            var resp = await client.SendAsync(req);

            resp.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            (await resp.Content.ReadAsByteArrayAsync()).ShouldBe(Bytes(64)[16..48]);
        });

    [FactIfPg]
    public async Task A_NonAscii_Shortname_Survives_The_Disposition_Header()
        => await WithAnonymousMediaAsync("مرحبا", "jpeg", ContentType.ImageJpeg, async (client, url) =>
        {
            // SetHttpFileName is what writes the RFC 5987 filename*; the comment
            // in the handler claims it, and nothing checked. A raw non-ASCII byte
            // in a header value would throw on write rather than fail politely.
            var resp = await client.GetAsync(url);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var cd = resp.Content.Headers.ContentDisposition!;
            cd.DispositionType.ShouldBe("inline");
            cd.FileNameStar.ShouldBe("مرحبا.jpeg");
        });

    [FactIfPg]
    public async Task Without_A_World_Permission_Anonymous_Is_Refused()
        => await WithAnonymousMediaAsync("secret", "jpeg", ContentType.ImageJpeg, async (client, url) =>
        {
            var resp = await client.GetAsync(url);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
                "the anonymous actor must still go through the permission gate");
            // And it must refuse without having shipped the bytes to do so.
            (await resp.Content.ReadAsStringAsync()).ShouldNotContain("\\u0000");
        }, grantWorld: false);

    // ==================== helpers ====================

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xFF);
        return b;
    }

    // Seeds a media attachment plus (optionally) the reserved world permission
    // and anonymous role that let an unauthenticated caller read it, hands the
    // assert an UNAUTHENTICATED client and the /public/payload URL, then
    // restores whatever anonymous/world rows were there before.
    private async Task WithAnonymousMediaAsync(
        string shortname, string ext, ContentType contentType,
        Func<HttpClient, string, Task> assert, bool grantWorld = true)
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();

        const string anonUser = "anonymous";   // Python-reserved
        const string worldPerm = "world";      // Python-reserved
        var anonRole = $"itest_anon_pay_{Guid.NewGuid():N}"[..24];
        var space = $"itest_pubpay_{Guid.NewGuid():N}"[..24];
        const string subpath = "/bin";

        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);
        var mediaUuid = Guid.NewGuid();

        try
        {
            await attachments.UpsertAsync(new Attachment
            {
                Uuid = mediaUuid.ToString(),
                Shortname = shortname,
                SpaceName = space,
                Subpath = subpath,
                OwnerShortname = "dmart",
                ResourceType = ResourceType.Media,
                IsActive = true,
                Payload = new Payload { ContentType = contentType },
                Media = Bytes(64),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            if (grantWorld)
            {
                await WorldPermissionFixture.UpsertAsync(access, priorWorld,
                    subpaths: new() { [space] = new() { subpath } },
                    actions: new() { "view", "query" },
                    resourceTypes: new() { "media" },
                    conditions: new() { "is_active" });
                await access.UpsertRoleAsync(new Role
                {
                    Uuid = Guid.NewGuid().ToString(),
                    Shortname = anonRole,
                    SpaceName = "management",
                    Subpath = "roles",
                    OwnerShortname = "dmart",
                    IsActive = true,
                    Permissions = new() { worldPerm },
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = anonUser,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = anonUser,
                IsActive = true,
                Roles = grantWorld ? new() { anonRole } : new(),
                Type = UserType.Web,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await access.InvalidateAllCachesAsync();

            // No Authorization header — this is the whole point of the route.
            var client = _factory.CreateClient();
            var url = $"/public/payload/media/{space}{subpath}/{Uri.EscapeDataString(shortname)}.{ext}";
            await assert(client, url);
        }
        finally
        {
            try { await attachments.DeleteAsync(mediaUuid); } catch { }
            try { await users.DeleteAsync(anonUser); } catch { }
            try { await access.DeleteRoleAsync(anonRole); } catch { }
            try { await access.DeletePermissionAsync(worldPerm); } catch { }
            if (priorAnon is not null) await users.UpsertAsync(priorAnon);
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }
}
