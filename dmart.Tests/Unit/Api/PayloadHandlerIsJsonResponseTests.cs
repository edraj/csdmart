using Dmart.Api.Managed;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Api;

// Pins the rule that decides whether the /payload/{resource_type}/{space}/{**rest}
// endpoint serves a response inline (no Content-Disposition: attachment) or
// as a downloadable attachment. Inline applies whenever the mime is
// application/json OR the URL ext is "json" (case-insensitive on both).
public class PayloadHandlerIsJsonResponseTests
{
    [Theory]
    [InlineData("application/json", "json")]
    [InlineData("application/json", "JSON")]
    [InlineData("application/json", "png")]      // mime wins even if ext isn't json
    [InlineData("APPLICATION/JSON", "png")]      // mime check is case-insensitive
    [InlineData("image/png", "json")]            // ext wins even if mime isn't json
    [InlineData("image/png", "Json")]            // ext check is case-insensitive
    public void ReturnsTrue_For_JsonMime_Or_JsonExt(string mime, string ext)
    {
        PayloadHandler.IsJsonResponse(mime, ext).ShouldBeTrue();
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("text/plain", "txt")]
    [InlineData("application/octet-stream", "bin")]
    [InlineData("text/html", "html")]
    [InlineData("application/jsonlines", "jsonl")]   // jsonl is not json
    public void ReturnsFalse_For_Other_MimeAndExt(string mime, string ext)
    {
        PayloadHandler.IsJsonResponse(mime, ext).ShouldBeFalse();
    }
}
