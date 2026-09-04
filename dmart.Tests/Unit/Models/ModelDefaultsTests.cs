using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// What a property defaults to when the JSON omits it is part of the wire
// contract, and for a long while it was not the value the model declared.
//
// The source-generated deserializer, on meeting an init-only property, stops
// using the parameterless constructor and switches to
// `ObjectWithParameterizedConstructorCreator` — which assigns every such
// property from an args array and passes `default(T)` for whatever the payload
// left out. The initialisers ran and were overwritten. The damage was only
// visible where the declared default differs from `default(T)`, which hid it:
// `Response.Status = Status.Success` looked fine because Success is the zero
// member, while `Space.ResourceType = ResourceType.Space` silently became
// `ResourceType.User` for the same reason in reverse.
//
// Making those properties `set` restores the constructor. Types with `required`
// members keep the parameterized creator, but it then carries only the required
// members — which the payload must supply anyway — so the rest keep their
// declared defaults.
public class ModelDefaultsTests
{
    // ---------------------------------------------------------------- generic
    // Covers every registered type that can be built with `new T()` and parsed
    // from `{}`, so a model added later is checked without touching this file.
    // Types with `required` members cannot do either and are covered explicitly
    // below.
    public static TheoryData<string> SimpleRegisteredTypes()
    {
        var data = new TheoryData<string>();
        foreach (var t in RegisteredTypes())
            if (IsSimplyConstructible(t))
                data.Add(t.AssemblyQualifiedName!);
        return data;
    }

    [Theory]
    [MemberData(nameof(SimpleRegisteredTypes))]
    public void An_Empty_Payload_Deserializes_To_The_Declared_Defaults(string typeName)
    {
        var type = Type.GetType(typeName)!;
        var info = TypeInfoFor(type)!;

        var fresh = Activator.CreateInstance(type)!;
        var parsed = JsonSerializer.Deserialize("{}", info);
        parsed.ShouldNotBeNull();

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            var expected = p.GetValue(fresh);
            var actual = p.GetValue(parsed);

            if (expected is IEnumerable and not string)
            {
                // Collections compare by emptiness: the bug turned `= new()`
                // into null, which is the distinction that matters here.
                (actual is null).ShouldBe(expected is null,
                    $"{type.Name}.{p.Name}: declared {(expected is null ? "null" : "a collection")} but parsed {(actual is null ? "null" : "a collection")}");
                continue;
            }
            actual.ShouldBe(expected, $"{type.Name}.{p.Name} lost its declared default on deserialization");
        }
    }

    // ------------------------------------------------------------- explicit
    // The types the generic case cannot reach, and the values that were
    // actually wrong. Each of these failed before the fix.
    private static string Ident(string shortname, string extra = "") =>
        $$"""
        {"shortname":"{{shortname}}","space_name":"s","subpath":"/",
         "uuid":"11111111-1111-1111-1111-111111111111","owner_shortname":"o"{{extra}}}
        """;

    [Fact]
    public void Space_Keeps_Its_Own_ResourceType_Rather_Than_The_Zero_Member()
    {
        // ResourceType.User is the enum's zero value, so a Space, Role, Group
        // or Permission parsed from JSON used to come back claiming to be a
        // user — the single most misleading form this bug took.
        var space = JsonSerializer.Deserialize(Ident("s"), DmartJsonContext.Default.Space)!;
        space.ResourceType.ShouldBe(ResourceType.Space);
        space.Tags.ShouldNotBeNull();
        space.Icon.ShouldBe("");
        space.PrimaryWebsite.ShouldBe("");
    }

    [Fact]
    public void User_Defaults_To_English_Not_Arabic()
    {
        // Language.Ar is the zero member and `= Language.En` was discarded, so
        // a user parsed from JSON without a language came back Arabic.
        var user = JsonSerializer.Deserialize(Ident("u"), DmartJsonContext.Default.User)!;
        user.Language.ShouldBe(Language.En);
        user.Type.ShouldBe(UserType.Web);
        user.ResourceType.ShouldBe(ResourceType.User);
        user.Roles.ShouldNotBeNull();
        user.Groups.ShouldNotBeNull();
    }

    [Fact]
    public void Query_Keeps_Its_Declared_Limit_And_Schema_Filter()
    {
        // Query is deserialized straight from request bodies by CsvHandler,
        // ImportExportHandler, ExecuteTaskHandler and AlterationHandler.
        var q = JsonSerializer.Deserialize(
            """{"type":"search","space_name":"s","subpath":"/"}""", DmartJsonContext.Default.Query)!;
        q.Limit.ShouldBe(10);
        q.FilterSchemaNames.ShouldBe(new List<string> { "meta" });
    }

    [Fact]
    public void Entry_Tags_Parse_To_An_Empty_List_Not_Null()
    {
        var e = JsonSerializer.Deserialize(
            Ident("e", ",\"resource_type\":\"content\""), DmartJsonContext.Default.Entry)!;
        e.Tags.ShouldNotBeNull();
        e.Tags.ShouldBeEmpty();
    }

    [Fact]
    public void An_Explicit_Null_Still_Arrives_As_Null()
    {
        // The other half, and why SpaceRepository's `?? ""` coercions stay:
        // restoring the initialisers fixes OMITTED fields, but a payload that
        // spells out `"icon": null` still lands a null in a non-nullable
        // string — System.Text.Json does not enforce nullability at runtime,
        // and the DB columns are NOT NULL.
        var space = JsonSerializer.Deserialize(
            Ident("s", ",\"icon\":null,\"primary_website\":null"), DmartJsonContext.Default.Space)!;
        space.Icon.ShouldBeNull();
        space.PrimaryWebsite.ShouldBeNull();
    }

    // ---------------------------------------------------------------- helpers
    private static IEnumerable<Type> RegisteredTypes()
        => typeof(DmartJsonContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(JsonTypeInfo).IsAssignableFrom(p.PropertyType)
                        && p.PropertyType.IsGenericType)
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Where(t => t.IsClass && t != typeof(string) && !t.IsAbstract)
            .Distinct();

    private static JsonTypeInfo? TypeInfoFor(Type t)
        => typeof(DmartJsonContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericArguments()[0] == t)
            .Select(p => p.GetValue(DmartJsonContext.Default) as JsonTypeInfo)
            .FirstOrDefault(i => i is not null);

    // Buildable with `new T()` and parseable from `{}` — i.e. no required
    // members and no constructor arguments.
    private static bool IsSimplyConstructible(Type t)
    {
        if (t.GetConstructor(Type.EmptyTypes) is null) return false;
        if (typeof(IEnumerable).IsAssignableFrom(t)) return false;
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            if (m.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute"))
                return false;
        return t.GetCustomAttributes().All(a => a.GetType().Name != "RequiredMemberAttribute");
    }
}
