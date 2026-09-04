using System;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// snake_case is the convention for dmart payload fields — every shipped schema
// follows it (`end_point`, `request_body`, `schema_shortname`). What these pin
// is WHERE that convention lives, because the answer decides whether a client
// is allowed to rewrite the caller's keys on their behalf.
//
// It lives in the space's JSON Schema, not in the serializer. The server has
// never transformed a dictionary key: it stores what it is given, and the
// schema rejects — by name, on write — anything it does not declare. The .NET
// client used to snake_case keys before sending them, which papered over that
// enforcement and silently renamed keys a caller had chosen deliberately, with
// no way to opt out. v1.3.1 (net8.0+) and v1.3.3 (netstandard2.1) removed it.
//
// If either half of this ever changes — the server starting to transform, or
// the schema stopping enforcing — that decision should be made deliberately,
// not discovered by a caller whose data went somewhere they did not put it.
public sealed class DictionaryKeyConventionTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public DictionaryKeyConventionTests(DmartFactory factory) => _factory = factory;

    // Mirrors the shape of dmart's own schemas: snake_case, and closed.
    private const string SnakeSchema =
        "{\"type\":\"object\",\"properties\":{\"end_point\":{\"type\":\"string\"}}," +
        "\"required\":[\"end_point\"],\"additionalProperties\":false}";

    // The convention is enforced here, and it says so out loud: a camelCase
    // spelling of a field the schema declares in snake_case is refused on
    // write, naming both the property that is missing and the one that is not
    // allowed. This is what makes a client-side rewrite unnecessary.
    [FactIfPg]
    public async Task A_CamelCase_Key_Is_Refused_By_A_SnakeCase_Schema()
    {
        var schema = await SeedSchemaAsync();
        var schemas = _factory.Services.GetRequiredService<SchemaValidator>();

        var camel = Payload(schema, "{\"endPoint\":\"/x\"}");
        var error = await schemas.ValidatePayloadAsync("management", ResourceType.User, camel);

        error.ShouldNotBeNull("the schema is the enforcement point for the naming convention");
        error!.ShouldContain("payload failed schema validation");
        error.ShouldContain("end_point", Case.Sensitive,
            "the error has to name the spelling the schema expects, or it is not actionable");
    }

    [FactIfPg]
    public async Task The_SnakeCase_Spelling_The_Schema_Declares_Is_Accepted()
    {
        var schema = await SeedSchemaAsync();
        var schemas = _factory.Services.GetRequiredService<SchemaValidator>();

        var snake = Payload(schema, "{\"end_point\":\"/x\"}");
        (await schemas.ValidatePayloadAsync("management", ResourceType.User, snake))
            .ShouldBeNull("the declared spelling must pass");
    }

    // The other half: with no schema there is nothing to enforce, so the key is
    // stored exactly as written. A client that snake_cased on the way out would
    // put this entry's data under a name the caller never chose and cannot read
    // back — which is the whole argument for the client not doing it.
    [FactIfPg]
    public async Task Without_A_Schema_A_Key_Round_Trips_Verbatim()
    {
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var shortname = $"dkc{Guid.NewGuid():N}"[..16];
        try
        {
            await entries.UpsertAsync(new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname, SpaceName = "management", Subpath = "/dkc",
                ResourceType = ResourceType.Content, OwnerShortname = "dmart", IsActive = true,
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    Body = JsonDocument.Parse("{\"myKey\":\"v\",\"other_key\":\"w\"}").RootElement.Clone(),
                },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

            var read = await entries.GetAsync("management", "/dkc", shortname, ResourceType.Content);
            var body = read.ShouldNotBeNull().Payload!.Body!.Value;

            body.TryGetProperty("myKey", out var mine).ShouldBeTrue(
                "the key must come back under the name it was written with");
            mine.GetString().ShouldBe("v");
            body.TryGetProperty("my_key", out _).ShouldBeFalse(
                "nothing in the server may rewrite a caller's key");
            body.TryGetProperty("other_key", out var theirs).ShouldBeTrue();
            theirs.GetString().ShouldBe("w");
        }
        finally
        {
            try { await entries.DeleteAsync("management", "/dkc", shortname, ResourceType.Content); }
            catch { /* best-effort */ }
        }
    }

    private static Payload Payload(string schema, string bodyJson) => new()
    {
        ContentType = ContentType.Json,
        SchemaShortname = schema,
        Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
    };

    private async Task<string> SeedSchemaAsync()
    {
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var shortname = $"dkcschema{Guid.NewGuid():N}"[..20];
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/schema",
            ResourceType = ResourceType.Schema, OwnerShortname = "dmart", IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(SnakeSchema).RootElement.Clone(),
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }
}
