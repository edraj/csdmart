using System.Text.Json;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Regression: the import validator resolved a schema's externalized body by
// walking THREE levels up from {space}/schema/.dm/{sn}/, which lands in the
// space root rather than in schema/. It worked only because a second candidate
// (the explicit schema/ path) was tried when the first did not exist.
//
// So a space holding both schema/{x}.json and {x}.json compiled the wrong file.
// That is not a contrived shape: a folder's own folder_rendering body IS such an
// {x}.json, so any space with a folder named after one of its schemas — a
// `cases` folder of `case` entries, an `equipment` folder of `equipment`
// entries — validated NOTHING. The compile threw on the folder body, the
// warning went to the log, null was cached, and every row imported unvalidated.
public class ImportSchemaBodyPathTests
{
    private const string Space = "pk";
    private const string SchemaName = "equipment";

    // A real JSON Schema: `n` must be an integer.
    private const string RealSchema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": { "n": { "type": "integer" } },
      "required": ["n"]
    }
    """;

    // The decoy that used to win: a folder_rendering body, which is not a
    // schema at all. `shortname_title` is an unknown keyword, so compiling it
    // throws rather than quietly producing a permissive schema.
    private const string FolderBody = """
    { "shortname_title": "Shortname", "index_attributes": [], "allow_view": true }
    """;

    private static string BuildTree(bool withDecoy)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dmart-schemapath-{Guid.NewGuid():N}");
        var metaDir = Path.Combine(root, Space, "schema", ".dm", SchemaName);
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "meta.schema.json"), $$"""
        {
          "uuid": "{{Guid.NewGuid()}}",
          "shortname": "{{SchemaName}}",
          "is_active": true,
          "owner_shortname": "dmart",
          "payload": { "content_type": "json", "schema_shortname": "meta_schema",
                        "body": "{{SchemaName}}.json" }
        }
        """);
        // The schema's real body, in the schema folder.
        File.WriteAllText(Path.Combine(root, Space, "schema", $"{SchemaName}.json"), RealSchema);
        if (withDecoy)
        {
            // The folder named `equipment`, whose folder_rendering body sits at
            // {space}/equipment.json — exactly where the old code looked first.
            File.WriteAllText(Path.Combine(root, Space, $"{SchemaName}.json"), FolderBody);
        }
        return root;
    }

    private static async Task<List<string>?> ValidateAsync(string root, object body)
    {
        // The context disposes the sink it was handed, so this must not also
        // own it — disposing twice throws ObjectDisposedException.
        var sink = new ImportIssueSink(Path.Combine(root, $"issues-{Guid.NewGuid():N}.jsonl"));
        await using var ctx = new ImportValidationContext(root, sink, NullLogger.Instance);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return await ctx.ValidateBodyAsync(Space, SchemaName, doc.RootElement, default);
    }

    [Theory]
    [InlineData(true)]   // the collision case — this is the regression
    [InlineData(false)]  // no collision, must keep working
    public async Task Externalized_Schema_Body_Resolves_From_Schema_Folder(bool withDecoy)
    {
        var root = BuildTree(withDecoy);
        try
        {
            // A body that violates the real schema must be reported. Before the
            // fix, the decoy tree returned null here — "no schema found" — and
            // the bad row sailed through.
            var bad = await ValidateAsync(root, new { n = "not-an-integer" });
            bad.ShouldNotBeNull(
                "the schema in schema/ must win, so an invalid body is caught");
            bad!.ShouldNotBeEmpty();

            // And a valid body must still pass, so the test cannot be satisfied
            // by a schema that rejects everything.
            var good = await ValidateAsync(root, new { n = 7 });
            good.ShouldBeNull("a body matching the schema must validate clean");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
