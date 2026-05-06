using Dmart.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for ChannelsRegistry — the loader for `.dmart/channels.json`.
// Verifies the JSON shape the user-facing example documents (name, keys,
// allowed_api_patterns) round-trips through the source-generated AOT
// deserializer, and that malformed input fails closed (empty list, not a
// crash at startup).
public class ChannelsRegistryTests : IDisposable
{
    private readonly string _tmpFile = Path.Combine(
        Path.GetTempPath(),
        $"dmart-channels-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tmpFile)) File.Delete(_tmpFile);
        GC.SuppressFinalize(this);
    }

    private ChannelsRegistry Build(string path)
    {
        var settings = new DmartSettings { ChannelsConfigPath = path };
        var opts = Options.Create(settings);
        return new ChannelsRegistry(opts, NullLogger<ChannelsRegistry>.Instance);
    }

    [Fact]
    public void Loads_Sample_Shape()
    {
        File.WriteAllText(_tmpFile, """
            [
              {
                "name": "mobileapp",
                "keys": ["aeCuD2ai-voo7PeeS-ooteeGh7"],
                "allowed_api_patterns": ["/public/.*"]
              }
            ]
            """);

        var reg = Build(_tmpFile);

        reg.Channels.Count.ShouldBe(1);
        var ch = reg.Channels[0];
        ch.Name.ShouldBe("mobileapp");
        ch.Keys.ShouldBe(new[] { "aeCuD2ai-voo7PeeS-ooteeGh7" });
        ch.AllowedApiPatterns.Length.ShouldBe(1);
        ch.AllowedApiPatterns[0].IsMatch("/public/foo").ShouldBeTrue();
        ch.AllowedApiPatterns[0].IsMatch("/managed/foo").ShouldBeFalse();
    }

    [Fact]
    public void Missing_File_Yields_Empty_List()
    {
        var reg = Build("/nonexistent/path/channels.json");
        reg.Channels.ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_Json_Yields_Empty_List()
    {
        File.WriteAllText(_tmpFile, "{ this is not valid json");
        var reg = Build(_tmpFile);
        reg.Channels.ShouldBeEmpty();
    }

    [Fact]
    public void Multiple_Channels_And_Patterns()
    {
        File.WriteAllText(_tmpFile, """
            [
              { "name": "a", "keys": ["k1"],         "allowed_api_patterns": ["/public/.*"] },
              { "name": "b", "keys": ["k2", "k3"],   "allowed_api_patterns": ["/info/.*", "^/qr/"] }
            ]
            """);
        var reg = Build(_tmpFile);

        reg.Channels.Count.ShouldBe(2);
        reg.Channels[1].Keys.Count.ShouldBe(2);
        reg.Channels[1].AllowedApiPatterns.Length.ShouldBe(2);
        reg.Channels[1].AllowedApiPatterns[1].IsMatch("/qr/abc").ShouldBeTrue();
    }
}
