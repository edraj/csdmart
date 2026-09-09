using Dmart.Api.Managed;
using Dmart.Models.Enums;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit;

// The stored ContentType is coarser than the formats it covers, and that only
// started to matter when the payload endpoint began asking browsers to RENDER
// the bytes rather than save them. A wrong label used to be invisible — the file
// downloaded and the OS picked a decoder — and is now the difference between a
// working player and a black box.
public class PayloadMimeTests
{
    // ---- one enum value, many real formats ----

    [Theory]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("wav", "audio/wav")]
    [InlineData("ogg", "audio/ogg")]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("flac", "audio/flac")]
    public void Audio_Is_Labelled_By_Extension(string ext, string expected)
        => PayloadHandler.MimeFor(ContentType.Audio, ext).ShouldBe(expected);

    [Theory]
    [InlineData("mp4", "video/mp4")]
    [InlineData("webm", "video/webm")]
    [InlineData("mov", "video/quicktime")]
    [InlineData("ogv", "video/ogg")]
    public void Video_Is_Labelled_By_Extension(string ext, string expected)
        => PayloadHandler.MimeFor(ContentType.Video, ext).ShouldBe(expected);

    [Fact]
    public void A_Webm_Is_Not_Announced_As_Mp4()
    {
        // The regression in one line: InferContentType folds webm/mov into
        // ContentType.Video, so before this every one of them was served as
        // video/mp4. Inline, that hands WebM bytes to the MP4 demuxer — Firefox
        // refuses the format outright, Chrome shows a black player.
        PayloadHandler.MimeFor(ContentType.Video, "webm").ShouldNotBe("video/mp4");
        PayloadHandler.MimeFor(ContentType.Audio, "wav").ShouldNotBe("audio/mpeg");
    }

    [Fact]
    public void An_Empty_Extension_Keeps_The_Historical_Default()
    {
        // McpTools calls MimeFor with "" — it has no URL to read an extension
        // from. That path must not change behaviour.
        PayloadHandler.MimeFor(ContentType.Audio, "").ShouldBe("audio/mpeg");
        PayloadHandler.MimeFor(ContentType.Video, "").ShouldBe("video/mp4");
        PayloadHandler.MimeFor(ContentType.Json, "").ShouldBe("application/json");
    }

    // ---- the catch-all ----

    [Theory]
    [InlineData("docx")]
    [InlineData("zip")]
    [InlineData("xlsx")]
    [InlineData("bin")]
    public void An_Unrecognised_Upload_Is_Never_Announced_As_Json(string ext)
    {
        // InferContentType's fallback for anything it cannot place is
        // ContentType.Json — not octet-stream. So a .docx was stored as `json`,
        // served as application/json, and (being JSON) got no disposition at
        // all: opening the URL dumped binary into the tab.
        var mime = PayloadHandler.MimeFor(ContentType.Json, ext);
        mime.ShouldNotBe("application/json");
        PayloadHandler.IsJsonResponse(mime, ext).ShouldBeFalse();
        // Whatever it resolves to must not be something a browser renders, so
        // the response carries a filename and downloads.
        PayloadHandler.RendersInline(mime).ShouldBeFalse();
    }

    [Fact]
    public void A_Real_Json_Payload_Is_Still_Json()
    {
        PayloadHandler.MimeFor(ContentType.Json, "json").ShouldBe("application/json");
        PayloadHandler.IsJsonResponse("application/json", "json").ShouldBeTrue();
    }

    // ---- the enum values that had no case at all ----

    [Fact]
    public void The_Generic_Image_Type_Renders_Inline()
    {
        // ContentType.Image is reachable two ways — InferContentType maps a bare
        // "image" MIME to it, and ParseContentType accepts "image" off the wire —
        // but MimeFor had no case, so it fell to octet-stream and downloaded.
        // Exactly the attachment this endpoint went inline for.
        PayloadHandler.MimeFor(ContentType.Image, "png").ShouldBe("image/png");
        PayloadHandler.MimeFor(ContentType.Image, "jpg").ShouldBe("image/jpeg");
        PayloadHandler.RendersInline(PayloadHandler.MimeFor(ContentType.Image, "png")).ShouldBeTrue();
        // Python's dmart resolves a bare "image" to image_jpeg; match that when
        // the extension says nothing useful.
        PayloadHandler.MimeFor(ContentType.Image, "").ShouldBe("image/jpeg");
    }

    [Fact]
    public void Comment_And_Reaction_Bodies_Are_Json()
        => PayloadHandler.MimeFor(ContentType.Comment, "json")
            .ShouldBe(PayloadHandler.MimeFor(ContentType.Reaction, "json"));

    // ---- charset ----

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/markdown")]
    [InlineData("text/csv")]
    [InlineData("application/json")]
    public void Text_Carries_An_Explicit_Utf8_Charset(string mime)
    {
        // X-Content-Type-Options: nosniff is set globally, so a browser cannot
        // recover the encoding from the bytes: a bare `text/plain` falls back to
        // the locale default and UTF-8 Arabic renders as mojibake. It never
        // mattered while these types downloaded.
        PayloadHandler.ContentTypeHeaderFor(mime).ShouldBe($"{mime}; charset=utf-8");
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/pdf")]
    [InlineData("video/webm")]
    public void Binary_Types_Get_No_Charset(string mime)
        => PayloadHandler.ContentTypeHeaderFor(mime).ShouldBe(mime);

    [Fact]
    public void The_Charset_Does_Not_Break_The_Inline_Decision()
    {
        // RendersInline and IsJsonResponse compare bare media types, which is why
        // MimeFor stays parameter-free and the charset is appended only on the
        // way out. If the two were merged, every text type would stop matching
        // and silently go back to downloading.
        PayloadHandler.RendersInline(PayloadHandler.MimeFor(ContentType.Text, "txt")).ShouldBeTrue();
    }

    // ---- ?download ----

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void Download_Reads_The_Value_Not_The_Key(string value, bool expected)
        => WantsDownload($"?download={value}").ShouldBe(expected);

    [Fact]
    public void A_Repeated_Download_Param_Still_Forces_A_Download()
    {
        // StringValues.ToString() JOINS repeats with a comma, so "1,1" matched
        // neither "1" nor "true" and the user's save-to-disk silently became a
        // render. Trivially produced by a client appending the param to a URL
        // that already carries it.
        WantsDownload("?download=1&download=1").ShouldBeTrue();
        WantsDownload("?download=true&download=true").ShouldBeTrue();
    }

    [Fact]
    public void Surrounding_Whitespace_Does_Not_Defeat_It()
        => WantsDownload("?download=%201%20").ShouldBeTrue();

    [Fact]
    public void An_Absent_Param_Is_Not_A_Download()
        => WantsDownload("").ShouldBeFalse();

    private static bool WantsDownload(string queryString)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(queryString);
        return PayloadHandler.WantsDownload(ctx.Request);
    }
}
