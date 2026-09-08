using System.Text.Json;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// A `schema` entry's body is not checked against a schema — it IS one, so it
// used to be stored without any check at all. That was not merely untidy:
//
//   {"$id":"https://x/s","allOf":[{"$ref":"https://x/s"}]}
//
// compiles fine and only recurses when something is evaluated against it. On
// JsonSchema.Net 9.1.4 — what dmart shipped — that recursed until the stack
// gave out, and a StackOverflowException cannot be caught, so the process died
// and took every in-flight request with it. Reaching it needed nothing exotic:
// store that schema, then write one entry whose payload references it.
//
// 9.4.0 turns it into a catchable JsonSchemaException. These tests pin the
// guard rather than the library version, because "the library survives it" is
// a weaker property than "we never stored it" — and the guard is what puts the
// error on the schema's author instead of on whoever writes the next entry.
public class SchemaDocumentGuardTests
{
    private static SchemaValidator Validator() =>
        // EntryRepository is only touched when resolving a schema BY NAME;
        // ValidateSchemaDocument works on the document it is handed, so the
        // repository is never reached and null is honest about that.
        new(null!, NullLogger<SchemaValidator>.Instance);

    private static JsonElement Doc(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_Self_Referential_Schema_That_Used_To_Kill_The_Process_Is_Rejected()
    {
        var errors = Validator().ValidateSchemaDocument(
            Doc("""{"$id":"https://x/s","allOf":[{"$ref":"https://x/s"}]}"""));

        errors.ShouldNotBeNull();
        errors!.Count.ShouldBe(1);
        errors[0].Keyword.ShouldBe("schema");
        errors[0].Message.ShouldContain("not a usable JSON Schema");
    }

    [Fact]
    public void Compiling_Alone_Would_Not_Have_Caught_It()
    {
        // The reason the guard evaluates instead of just compiling. If this
        // ever starts throwing, FromText has become strict enough on its own
        // and the trial evaluation could be reconsidered — but not before.
        var act = () => global::Json.Schema.JsonSchema.FromText(
            """{"$id":"https://dmart.test/compile-only","allOf":[{"$ref":"https://dmart.test/compile-only"}]}""",
            new global::Json.Schema.BuildOptions
            {
                SchemaRegistry = new global::Json.Schema.SchemaRegistry(),
            });
        act.ShouldNotThrow();
    }

    [Fact]
    public void A_Schema_That_Merely_Fails_Against_The_Trial_Instance_Is_Accepted()
    {
        // `required` makes this invalid for `{}`, which is not the same thing
        // as being an unusable schema. Rejecting it would break every schema
        // with a required field — i.e. most of them.
        Validator().ValidateSchemaDocument(
            Doc("""{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}}"""))
            .ShouldBeNull();
    }

    [Fact]
    public void Legal_Recursion_Through_Defs_Is_Accepted()
    {
        // Self-reference is not the problem; an unresolvable CYCLE is. A tree
        // node referencing itself for nesting is ordinary and must keep working.
        Validator().ValidateSchemaDocument(
            Doc("""{"$defs":{"n":{"type":"object","properties":{"c":{"$ref":"#/$defs/n"}}}},"$ref":"#/$defs/n"}"""))
            .ShouldBeNull();
    }

    [Fact]
    public void A_Body_That_Is_Not_A_Schema_At_All_Is_Rejected_Not_Crashed()
    {
        Validator().ValidateSchemaDocument(Doc("""{"type":12345}""")).ShouldNotBeNull();
    }

    [Fact]
    public void Two_Schemas_Sharing_An_Id_Do_Not_Collide()
    {
        // The same failure from the other direction: two different spaces are
        // free to store schemas carrying the same $id, and with a shared
        // registry the second one compiled would be refused.
        var v = Validator();
        v.ValidateSchemaDocument(Doc("""{"$id":"https://dmart.test/shared","type":"object"}""")).ShouldBeNull();
        v.ValidateSchemaDocument(Doc("""{"$id":"https://dmart.test/shared","type":"array"}""")).ShouldBeNull();
    }

    [Fact]
    public void Validating_The_Same_Schema_Twice_Is_Accepted()
    {
        // Updating a schema entry re-runs the guard on a document carrying the
        // same $id. JsonSchema.FromText registers $id process-globally and
        // refuses to overwrite, so a guard that shared that registry would
        // reject every schema UPDATE while accepting the create.
        var v = Validator();
        var doc = Doc("""{"$id":"https://dmart.test/twice","type":"object"}""");
        v.ValidateSchemaDocument(doc).ShouldBeNull();
        v.ValidateSchemaDocument(doc).ShouldBeNull("re-validating the same $id must not be treated as a bad schema");
    }

    [Fact]
    public void An_Empty_Schema_Is_Accepted()
    {
        // `{}` is a valid JSON Schema meaning "anything". Rejecting it would be
        // wrong, and it is the obvious thing an over-eager guard gets wrong.
        Validator().ValidateSchemaDocument(Doc("{}")).ShouldBeNull();
    }
}
