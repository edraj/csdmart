using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.Native;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The `get_media_attachment` callback is the only one that moves raw bytes, so
// it is the only one whose wire shape is not just "some JSON document": the
// dispatcher base64-encodes the blob. That encode/round-trip is what this
// covers — a plugin has no other way to reach attachment bytes, so a silent
// change here (truncation, double-encoding, a miss reported as success) would
// corrupt data with nothing else to catch it.
//
// [Collection] join: the dispatcher path reads PluginInvocationContext, which
// is ThreadStatic — see PluginInvocationContextCollection.
[Collection(PluginInvocationContextCollection.Name)]
public sealed class PluginMediaCallbackTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PluginMediaCallbackTests(DmartFactory factory) => _factory = factory;

    // Deliberately not valid UTF-8 and not text: media is a BYTEA and must
    // survive as bytes, not as a string that happened to round-trip.
    private static readonly byte[] Blob =
        [0x00, 0xFF, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xC3, 0x28, 0x00];

    private static JsonElement Handle(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        return JsonDocument.Parse(
            PluginCallbackDispatcher.Handle(doc.RootElement, ambientActor: null)).RootElement;
    }

    [FactIfPg]
    public async Task Media_Bytes_Round_Trip_Through_The_Callback_As_Base64()
    {
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var sn = "media_" + Guid.NewGuid().ToString("N")[..8];
        const string space = "management";
        const string subpath = "/mediacbtest";

        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn, SpaceName = space, Subpath = subpath,
            ResourceType = ResourceType.Media,
            OwnerShortname = "dmart", IsActive = true,
            Tags = new(), Media = Blob,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            // $$$ so the frame's trailing }} reads as literal braces rather
            // than a closing interpolation hole.
            var res = Handle($$$"""
                {"type":"callback","id":1,"op":"get_media_attachment",
                 "args":{"space":"{{{space}}}","subpath":"{{{subpath}}}","shortname":"{{{sn}}}"}}
                """);

            res.GetProperty("ok").GetBoolean().ShouldBeTrue();
            var result = res.GetProperty("result");

            // `length` is the byte count, so a plugin can size a buffer or
            // sanity-check without decoding first.
            result.GetProperty("length").GetInt32().ShouldBe(Blob.Length);
            Convert.FromBase64String(result.GetProperty("media_b64").GetString()!)
                .ShouldBe(Blob);
        }
        finally
        {
            var existing = await attachments.GetAsync(space, subpath, sn);
            if (existing is not null) await attachments.DeleteAsync(Guid.Parse(existing.Uuid));
        }
    }

    [FactIfPg]
    public async Task A_Miss_Is_Reported_As_Null_Media_Not_As_An_Empty_Blob()
    {
        // An absent attachment and a zero-byte one must not look the same to a
        // plugin: "" decodes to an empty array, which a caller could easily
        // treat as a real (empty) file.
        var res = Handle("""
            {"type":"callback","id":1,"op":"get_media_attachment",
             "args":{"space":"management","subpath":"/nope","shortname":"nothing_here"}}
            """);

        res.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var result = res.GetProperty("result");
        result.GetProperty("media").ValueKind.ShouldBe(JsonValueKind.Null);
        result.TryGetProperty("media_b64", out _).ShouldBeFalse();

        await Task.CompletedTask;
    }
}
