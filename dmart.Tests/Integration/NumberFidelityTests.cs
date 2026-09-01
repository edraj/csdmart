using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the other half of the "the .NET SDK parses integers as decimal" report:
// whatever the client does, the SERVER must never reformat a number it was
// handed. An integer written through /managed/request has to come back out of
// create, update AND patch as the same integer — no fractional tail, no
// precision loss past 2^53 — because a consumer mapping the field onto `int`
// gets a hard JsonException from System.Text.Json the moment a "1000.0" shows
// up ("The JSON value could not be converted to System.Int32").
//
// Note the inverse is data, not a bug: a value STORED as 1000.0 is returned as
// 1000.0, faithfully. Only a decimal keeps trailing-zero scale through
// System.Text.Json (1000.0m -> "1000.0"), so a producer modelling an integral
// field as decimal is what puts that form in the store to begin with.
public sealed class NumberFidelityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public NumberFidelityTests(DmartFactory factory) => _factory = factory;

    private const string Subpath = "number_fidelity";

    private static string Envelope(string requestType, string shortname, string body) =>
        "{\"space_name\":\"management\",\"request_type\":\"" + requestType + "\",\"records\":[" +
        "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"" + Subpath + "\"," +
        "\"attributes\":{\"is_active\":true,\"payload\":{\"content_type\":\"json\",\"body\":" + body + "}}}]}";

    // 9007199254740993 is 2^53 + 1 — the first integer a double cannot hold. It
    // catches any layer that round-trips numbers through floating point.
    private const string Body =
        "{\"qty\":7,\"big\":9007199254740993,\"ratio\":2.5,\"amount\":1000.0,\"neg\":-3,\"zero\":0}";

    [FactIfPg]
    public async Task Integers_Survive_Create_Update_And_Patch_Unchanged()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var shortname = $"numfid_{Guid.NewGuid():N}"[..18];

        async Task<string> PostAsync(string payload)
        {
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            return await resp.Content.ReadAsStringAsync();
        }

        async Task<string> ReadBackAsync()
        {
            var resp = await admin.Client.GetAsync(
                $"/managed/entry/content/management/{Subpath}/{shortname}?retrieve_json_payload=true");
            return await resp.Content.ReadAsStringAsync();
        }

        try
        {
            var created = await PostAsync(Envelope("create", shortname, Body));
            created.ShouldContain("success", Case.Insensitive, created);

            var afterCreate = await ReadBackAsync();
            // Integral in, integral out — never "7.0".
            afterCreate.ShouldContain("\"qty\":7,", customMessage: afterCreate);
            afterCreate.ShouldNotContain("\"qty\":7.0");
            // Past 2^53 the value must still be exact.
            afterCreate.ShouldContain("\"big\":9007199254740993", customMessage: afterCreate);
            // A genuine fraction stays a fraction...
            afterCreate.ShouldContain("\"ratio\":2.5", customMessage: afterCreate);
            // ...and a stored trailing-zero form is echoed verbatim rather than
            // "helpfully" normalised, which is what makes it a data question.
            afterCreate.ShouldContain("\"amount\":1000.0", customMessage: afterCreate);

            var updated = await PostAsync(Envelope("update", shortname,
                "{\"qty\":11,\"big\":9007199254740993,\"amount\":2000}"));
            updated.ShouldContain("success", Case.Insensitive, updated);
            var afterUpdate = await ReadBackAsync();
            afterUpdate.ShouldContain("\"qty\":11,", customMessage: afterUpdate);
            afterUpdate.ShouldContain("\"amount\":2000", customMessage: afterUpdate);
            afterUpdate.ShouldNotContain("\"amount\":2000.0");
            afterUpdate.ShouldContain("\"big\":9007199254740993", customMessage: afterUpdate);

            // The patch path rebuilds the body through a CLR dictionary, so it
            // gets its own assertion rather than riding on update's.
            var patched = await PostAsync(
                "{\"space_name\":\"management\",\"request_type\":\"patch\",\"records\":[" +
                "{\"resource_type\":\"content\",\"shortname\":\"" + shortname + "\",\"subpath\":\"" + Subpath + "\"," +
                "\"attributes\":{\"payload\":{\"body\":{\"qty\":13}}}}]}");
            patched.ShouldContain("success", Case.Insensitive, patched);
            var afterPatch = await ReadBackAsync();
            afterPatch.ShouldContain("\"qty\":13,", customMessage: afterPatch);
            afterPatch.ShouldNotContain("\"qty\":13.0");

            // And the query projection must agree with the entry endpoint.
            var queryResp = await admin.Client.PostAsync("/managed/query", new StringContent(
                "{\"type\":\"search\",\"space_name\":\"management\",\"subpath\":\"" + Subpath +
                "\",\"search\":\"@shortname:" + shortname + "\",\"retrieve_json_payload\":true,\"limit\":5}",
                Encoding.UTF8, "application/json"));
            var queryRaw = await queryResp.Content.ReadAsStringAsync();
            queryRaw.ShouldContain("\"qty\":13,", customMessage: queryRaw);
            queryRaw.ShouldContain("\"big\":9007199254740993", customMessage: queryRaw);
        }
        finally
        {
            try { await entries.DeleteAsync("management", Subpath, shortname, ResourceType.Content); } catch { }
            await admin.Cleanup();
        }
    }
}
