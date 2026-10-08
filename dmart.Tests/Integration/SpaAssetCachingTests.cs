using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The embedded SPAs' static files carried no Cache-Control at all, so every
// visit re-validated ~40 hashed assets. Vite names everything under assets/
// by content hash — those are immutable for a year — while index.html and
// config.json are the entry points that must stay fresh. When the build ships
// a pre-compressed `.br`/`.gz` next to an asset it is served as-is instead of
// being re-compressed on every request.
//
// Like RecentParityTests, a bundle that was not built when the server was
// compiled is "not built", not a failure: the test returns early.
public sealed class SpaAssetCachingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SpaAssetCachingTests(DmartFactory factory) => _factory = factory;

    private static readonly Regex AssetRef = new(@"(?:src|href)=""([^""]*assets/[^""]+\.(?:js|css))""", RegexOptions.Compiled);

    [Theory]
    [InlineData("/cxb")]
    [InlineData("/cat")]
    public async Task Hashed_Assets_Are_Immutable_And_Entry_Points_Are_NoCache(string prefix)
    {
        var client = _factory.CreateClient();
        var index = await client.GetAsync($"{prefix}/index.html");
        if (index.StatusCode == HttpStatusCode.NotFound) return; // SPA bundle not built into this binary

        index.StatusCode.ShouldBe(HttpStatusCode.OK);
        index.Headers.CacheControl?.NoCache.ShouldBe(true, $"{prefix}/index.html must revalidate on every load");

        var config = await client.GetAsync($"{prefix}/config.json");
        config.Headers.CacheControl?.NoCache.ShouldBe(true);

        var html = await index.Content.ReadAsStringAsync();
        var m = AssetRef.Match(html);
        m.Success.ShouldBeTrue($"no hashed asset reference found in {prefix}/index.html");
        var assetUrl = m.Groups[1].Value;
        if (!assetUrl.StartsWith('/')) assetUrl = $"{prefix}/{assetUrl}";

        var asset = await client.GetAsync(assetUrl);
        asset.StatusCode.ShouldBe(HttpStatusCode.OK, assetUrl);
        asset.Headers.CacheControl.ShouldNotBeNull(assetUrl);
        asset.Headers.CacheControl!.Public.ShouldBe(true);
        asset.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        asset.Headers.CacheControl.ToString().ShouldContain("immutable");

        // A pre-compressed variant is optional (it depends on the build), but
        // when it is served it must be a complete, decodable file.
        using var req = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        req.Headers.AcceptEncoding.ParseAdd("br");
        var compressed = await client.SendAsync(req);
        compressed.StatusCode.ShouldBe(HttpStatusCode.OK);
        if (compressed.Content.Headers.ContentEncoding.Contains("br"))
        {
            compressed.Headers.CacheControl!.ToString().ShouldContain("immutable");
            compressed.Headers.Vary.ShouldContain("Accept-Encoding");
            await using var body = await compressed.Content.ReadAsStreamAsync();
            await using var brotli = new BrotliStream(body, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await brotli.CopyToAsync(ms);
            ms.Length.ShouldBeGreaterThan(0);
            var plain = await (await client.GetAsync(assetUrl)).Content.ReadAsByteArrayAsync();
            ms.ToArray().ShouldBe(plain, "the pre-compressed variant must decode to the plain asset");
        }
    }
}
