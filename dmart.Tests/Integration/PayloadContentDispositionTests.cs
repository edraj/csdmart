using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A payload URL pasted into a browser used to download instead of render: the
// handler passed a fileDownloadName to Results.File unconditionally, which makes
// ASP.NET emit `Content-Disposition: attachment`. Python's dmart sends no
// Content-Disposition at all (StreamingResponse with only a media_type), so
// every payload rendered inline there.
//
// Serving EVERYTHING inline is not the fix. Attachments are user-uploaded, the
// API authenticates by the `auth_token` cookie, and html/svg served inline are
// documents that execute script on the API's own origin — stored XSS. The
// global CSP only attaches to text/html responses, so svg would carry no policy
// at all. So: inline for the media types a browser renders passively, attachment
// for html/svg and anything opaque, and an explicit ?download flag to force a
// save for callers that want one.
public class PayloadContentDispositionTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PayloadContentDispositionTests(DmartFactory factory) => _factory = factory;

    // ==================== inline: types a browser renders ====================

    [FactIfPg]
    public async Task Image_Payload_Is_Served_Inline_With_Its_Filename()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
            // Inline still names the file so the browser's "Save as" is sane.
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("shot.jpeg");
        });

    [FactIfPg]
    public async Task Pdf_Payload_Is_Served_Inline()
        => await WithUploadedMediaAsync("doc.pdf", "application/pdf", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        });

    [FactIfPg]
    public async Task Text_Payload_Is_Served_Inline()
        => await WithUploadedMediaAsync("notes.txt", "text/plain", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        });

    // ==================== attachment: script-capable documents ====================

    // An uploaded .html rendered inline runs its own <script> on the API origin,
    // with the victim's auth_token cookie attached. Forcing a download is what
    // stops that, so this must stay attachment even though the browser could
    // display it.
    [FactIfPg]
    public async Task Html_Payload_Stays_An_Attachment()
        => await WithUploadedMediaAsync("page.html", "text/html", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    // SVG is the sharper edge of the same problem: it is an image by MIME but a
    // document to the browser, and the global CSP only covers text/html — an
    // inline svg would execute with no policy at all.
    [FactIfPg]
    public async Task Svg_Payload_Stays_An_Attachment()
        => await WithUploadedMediaAsync("logo.svg", "image/svg+xml", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    // Nothing a browser can render — saving it is the only sensible outcome.
    // (Note .bin would NOT work here: InferContentType maps an unrecognised
    // extension to ContentType.Json, so it is served as application/json.)
    [FactIfPg]
    public async Task Apk_Payload_Stays_An_Attachment()
        => await WithUploadedMediaAsync("app.apk", "application/vnd.android.package-archive",
            Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    // ==================== ?download override ====================

    [FactIfPg]
    public async Task Download_Flag_Forces_Attachment_On_An_Inline_Type()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url + "?download=1");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("shot.jpeg");
        });

    // Guards the obvious wrong implementation: presence-of-key rather than
    // value. `?download=0` asks NOT to download.
    [FactIfPg]
    public async Task Download_Flag_Set_False_Leaves_The_Payload_Inline()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            var resp = await OkAsync(client, url + "?download=0");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        });

    // The flag is documented as working for any type, so the JSON branch —
    // which returns before the disposition logic — has to honour it too.
    [FactIfPg]
    public async Task Download_Flag_Forces_Attachment_On_Json()
        => await WithUploadedMediaAsync("data.json", "application/json",
            Encoding.UTF8.GetBytes("{\"a\":1}"), async (client, url) =>
        {
            var resp = await OkAsync(client, url + "?download=1");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("data.json");
        });

    // ==================== range requests ====================

    // <audio>/<video> can only seek when the server answers ranges; without it
    // the element plays from the start and the scrub bar is dead.
    [FactIfPg]
    public async Task Range_Request_On_Inline_Video_Returns_Partial_Content()
        => await WithUploadedMediaAsync("clip.mp4", "video/mp4", Bytes(4096), async (client, url) =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 99);
            var resp = await client.SendAsync(req);

            resp.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            resp.Content.Headers.ContentRange!.From.ShouldBe(0);
            resp.Content.Headers.ContentRange!.To.ShouldBe(99);
            (await resp.Content.ReadAsByteArrayAsync()).Length.ShouldBe(100);
        });

    // ==================== JSON is unchanged ====================

    // JSON already came back inline before this change (callers read the body
    // straight off the response); it must not regress into an attachment.
    [FactIfPg]
    public async Task Json_Payload_Is_Still_Served_Inline()
        => await WithUploadedMediaAsync("data.json", "application/json",
            Encoding.UTF8.GetBytes("{\"a\":1}"), async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            (resp.Content.Headers.ContentDisposition?.DispositionType ?? "inline").ShouldBe("inline");
        });

    // ==================== the response a browser actually gets ====================

    [FactIfPg]
    public async Task Inline_Payload_Allows_A_SameOrigin_Frame()
        => await WithUploadedMediaAsync("doc.pdf", "application/pdf", Bytes(64), async (client, url) =>
        {
            // The global X-Frame-Options: DENY applies to <iframe>/<embed>/<object>
            // as surely as to a cross-origin frame, so it blocked the in-app PDF
            // viewers — cxb and catalog both embed the payload same-origin — and
            // the user got the "PDF is not rendered here properly" fallback. The
            // header this endpoint went inline for has to permit that one case.
            var resp = await OkAsync(client, url);
            resp.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem()
                .ShouldBe("SAMEORIGIN");
        });

    [FactIfPg]
    public async Task Attachment_Payload_Keeps_The_Strict_Frame_Policy()
        => await WithUploadedMediaAsync("page.html", "text/html", Bytes(64), async (client, url) =>
        {
            // Only the inline branch relaxes it. An attachment is never framed,
            // so it keeps the site-wide default — and html is exactly the type
            // that must not become a document on this origin.
            var resp = await OkAsync(client, url);
            resp.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        });

    [FactIfPg]
    public async Task Inline_Text_Declares_Utf8()
        => await WithUploadedMediaAsync("notes.txt", "text/plain",
            Encoding.UTF8.GetBytes("مرحبا"), async (client, url) =>
        {
            // nosniff is set globally, so a bare text/plain leaves the browser on
            // its locale default and Arabic renders as mojibake.
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentType!.CharSet.ShouldBe("utf-8");
            (await resp.Content.ReadAsStringAsync()).ShouldBe("مرحبا");
        });

    [FactIfPg]
    public async Task An_Opaque_Upload_Downloads_Under_Its_Own_Name()
        => await WithUploadedMediaAsync("report.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Bytes(64), async (client, url) =>
        {
            // InferContentType cannot place a .docx, and its fallback is `json`
            // — so this used to come back as application/json with no
            // disposition at all, dumping binary into the tab.
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentType!.MediaType.ShouldNotBe("application/json");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("report.docx");
        });

    // ==================== ranges ====================

    [FactIfPg]
    public async Task A_Range_Request_Returns_Only_The_Requested_Bytes()
        => await WithUploadedMediaAsync("clip.mp4", "video/mp4", Bytes(4096), async (client, url) =>
        {
            // What lets <video> seek. Also the assertion that the lazy media
            // stream slices correctly rather than returning the whole blob.
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(100, 199);
            var resp = await client.SendAsync(req);

            resp.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            resp.Content.Headers.ContentRange!.From.ShouldBe(100);
            resp.Content.Headers.ContentRange!.To.ShouldBe(199);
            var body = await resp.Content.ReadAsByteArrayAsync();
            body.Length.ShouldBe(100);
            body.ShouldBe(Bytes(4096)[100..200]);
        });

    [FactIfPg]
    public async Task A_Range_Spanning_The_Stream_Buffer_Is_Contiguous()
        => await WithUploadedMediaAsync("clip.mp4", "video/mp4", Bytes(300_000), async (client, url) =>
        {
            // The media stream reads in chunks, so a range has to survive the
            // seam between two of them — an off-by-one in the refill would show
            // up here as a shifted or truncated body, not as an error.
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(1000, 250_999);
            var resp = await client.SendAsync(req);

            resp.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            var body = await resp.Content.ReadAsByteArrayAsync();
            body.Length.ShouldBe(250_000);
            body.ShouldBe(Bytes(300_000)[1000..251_000]);
        });

    [FactIfPg]
    public async Task A_Whole_Payload_Still_Round_Trips_Byte_For_Byte()
        => await WithUploadedMediaAsync("clip.mp4", "video/mp4", Bytes(300_000), async (client, url) =>
        {
            // The stream is chunked; a full read must still reassemble exactly.
            var resp = await OkAsync(client, url);
            (await resp.Content.ReadAsByteArrayAsync()).ShouldBe(Bytes(300_000));
        });

    [FactIfPg]
    public async Task A_Download_Can_Be_Resumed()
        => await WithUploadedMediaAsync("clip.mp4", "video/mp4", Bytes(4096), async (client, url) =>
        {
            // Resumability is orthogonal to disposition: a ?download=1 that drops
            // at 90% should resume, not restart. Range support used to be enabled
            // on the inline branch only.
            var resp = await OkAsync(client, url + "?download=1");
            resp.Headers.AcceptRanges.ShouldContain("bytes");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    [FactIfPg]
    public async Task An_Unsatisfiable_Range_Does_Not_Claim_An_Inline_Body()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            // The disposition used to be stamped on the response before the file
            // result ran, so a 416 came back still advertising an inline body it
            // does not have — a header/body mismatch that confuses caches.
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(99_999, null);
            var resp = await client.SendAsync(req);

            resp.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
            resp.Content.Headers.ContentDisposition.ShouldBeNull();
        });

    // ==================== ?download ====================

    [FactIfPg]
    public async Task A_Repeated_Download_Param_Still_Forces_A_Download()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            // StringValues.ToString() joins repeats with a comma, so "1,1"
            // matched nothing and the save-to-disk silently became a render.
            var resp = await OkAsync(client, url + "?download=1&download=1");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    [FactIfPg]
    public async Task Download_Also_Works_Spelled_As_A_Word()
        => await WithUploadedMediaAsync("shot.jpeg", "image/jpeg", Bytes(64), async (client, url) =>
        {
            // WantsDownload accepts =true, and nothing exercised it end to end.
            var resp = await OkAsync(client, url + "?download=true");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        });

    // ==================== entry-flavor payloads ====================

    [FactIfPg]
    public async Task An_Entry_Payload_Renders_Inline_By_Default()
        => await WithEntryPayloadAsync(async (client, url) =>
        {
            var resp = await OkAsync(client, url);
            resp.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            resp.Content.Headers.ContentDisposition.ShouldBeNull();
        });

    [FactIfPg]
    public async Task An_Entry_Payload_Honours_Download()
        => await WithEntryPayloadAsync(async (client, url) =>
        {
            // WantsDownload's contract is "for any type", but this branch returns
            // Results.Content, which never emits a disposition — so the flag was
            // accepted and silently ignored, and the JSON rendered in the tab.
            var resp = await OkAsync(client, url + "?download=1");
            resp.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            resp.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldEndWith(".json");
        });

    // ==================== helpers ====================

    // GET that fails loudly: a non-200 reports status + body, instead of the
    // assertions below tripping over a null Content-Disposition.
    private static async Task<HttpResponseMessage> OkAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"GET {url} -> {await resp.Content.ReadAsStringAsync()}");
        return resp;
    }

    // Deterministic filler — content never matters here, only the headers.
    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xFF);
        return b;
    }

    // Stands up a space + folder, uploads one media attachment named `fileName`
    // with `mime`, hands the assert the payload URL, then tears the space down.
    private async Task WithUploadedMediaAsync(
        string fileName, string mime, byte[] bytes, Func<HttpClient, string, Task> assert)
    {
        var space = $"itest_cd_{Guid.NewGuid():N}"[..20];
        var shortname = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).TrimStart('.');
        var user = await _factory.CreateLoggedInUserAsync();
        var client = user.Client;

        try
        {
            (await CreateAsync(client, space,
                $"{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{\"is_active\":true}}}}"))
                .Status.ShouldBe(Status.Success, "create space");

            (await CreateAsync(client, space,
                "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"bin\",\"attributes\":{\"is_active\":true}}"))
                .Status.ShouldBe(Status.Success, "create folder");

            (await UploadAsync(client, space,
                $"{{\"resource_type\":\"media\",\"subpath\":\"bin\",\"shortname\":\"{shortname}\",\"attributes\":{{\"is_active\":true}}}}",
                bytes, fileName, mime))
                .Status.ShouldBe(Status.Success, "upload media");

            await assert(client, $"/managed/payload/media/{space}/bin/{shortname}.{ext}");
        }
        finally
        {
            try
            {
                await client.PostAsync("/managed/request",
                    JsonContent(
                        $"{{\"space_name\":\"{space}\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{}}}}]}}"));
            }
            catch { /* best effort */ }
            await user.Cleanup();
        }
    }

    // Stands up a space + folder + one content entry carrying a JSON payload,
    // then hands the assert its /payload URL. The entry branch reads
    // entries.payload.body rather than attachments.media, so it needs its own
    // fixture — and had none, which is how ?download went unimplemented there.
    private async Task WithEntryPayloadAsync(Func<HttpClient, string, Task> assert)
    {
        var space = $"itest_cd_{Guid.NewGuid():N}"[..20];
        const string shortname = "note";
        var user = await _factory.CreateLoggedInUserAsync();
        var client = user.Client;
        try
        {
            (await CreateAsync(client, space,
                $"{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{\"is_active\":true}}}}"))
                .Status.ShouldBe(Status.Success, "create space");
            (await CreateAsync(client, space,
                "{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"bin\",\"attributes\":{\"is_active\":true}}"))
                .Status.ShouldBe(Status.Success, "create folder");
            (await CreateAsync(client, space,
                $"{{\"resource_type\":\"content\",\"subpath\":\"bin\",\"shortname\":\"{shortname}\",\"attributes\":{{\"is_active\":true,"
                + "\"payload\":{\"content_type\":\"json\",\"body\":{\"hello\":\"world\"}}}}"))
                .Status.ShouldBe(Status.Success, "create content");

            await assert(client, $"/managed/payload/content/{space}/bin/{shortname}.json");
        }
        finally
        {
            try
            {
                await client.PostAsync("/managed/request",
                    JsonContent(
                        $"{{\"space_name\":\"{space}\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{space}\",\"attributes\":{{}}}}]}}"));
            }
            catch { /* best effort */ }
            await user.Cleanup();
        }
    }

    private static StringContent JsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<Response> CreateAsync(HttpClient client, string space, string record)
    {
        var body = $"{{\"space_name\":\"{space}\",\"request_type\":\"create\",\"records\":[{record}]}}";
        var resp = await client.PostAsync("/managed/request", JsonContent(body));
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }

    private static async Task<Response> UploadAsync(
        HttpClient client, string space, string record, byte[] payloadBytes,
        string payloadFileName, string payloadMime)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(space), "space_name");

        var recordPart = new ByteArrayContent(Encoding.UTF8.GetBytes(record));
        recordPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(recordPart, "request_record", "request_record.json");

        var payloadPart = new ByteArrayContent(payloadBytes);
        payloadPart.Headers.ContentType = new MediaTypeHeaderValue(payloadMime);
        form.Add(payloadPart, "payload_file", payloadFileName);

        var resp = await client.PostAsync("/managed/resource_with_payload", form);
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }
}
