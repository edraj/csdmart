using Dmart.DataAdapters.Parquet;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Parquet;

// The row-to-column mapping, round-tripped without a database.
//
// This is where a backup quietly corrupts. The encoder is verified against
// pyarrow and the reader against pyarrow-written files, so the FORMAT is
// covered; what is not covered by any of that is whether an Entry survives
// being taken apart into 25 columns and put back together. A column mapped to
// the wrong index, a null conflated with an empty list, a timestamp handed back
// in a different kind — each writes cleanly, reads cleanly, and restores wrong.
public class EntryParquetTableTests
{
    private static Entry Minimal(string shortname) => new()
    {
        Shortname = shortname,
        SpaceName = "myspace",
        Subpath = "/",
        Uuid = Guid.NewGuid().ToString(),
        OwnerShortname = "dmart",
        ResourceType = ResourceType.Content,
    };

    private static List<Entry> RoundTrip(params Entry[] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-ept-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(EntryParquetTable.Schema);
            using (var fs = File.Create(path))
                writer.Write(fs, EntryParquetTable.BuildPages(rows), rows.Length);
            return EntryParquetTable.FromTable(ParquetFileReader.ReadFile(path), "myspace");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Every column populated, so a mapping that reads the wrong index shows up
    // as a value in the wrong field rather than as a null nobody checks.
    [Fact]
    public void A_Fully_Populated_Entry_Survives_The_Round_Trip()
    {
        var original = Minimal("full") with
        {
            Subpath = "/deep/nested",
            IsActive = true,
            Slug = "the-slug",
            Displayname = new Translation { En = "Hello", Ar = "مرحبا" },
            Description = new Translation { En = "Described" },
            Tags = ["alpha", "beta"],
            CreatedAt = new DateTime(2026, 3, 1, 9, 30, 0),
            UpdatedAt = new DateTime(2026, 8, 13, 17, 4, 5),
            OwnerGroupShortname = "editors",
            LastChecksumHistory = "sha256:abc",
            ResourceType = ResourceType.Ticket,
            State = "open",
            IsOpen = true,
            WorkflowShortname = "wf",
            Collaborators = new() { ["reviewer"] = "alice" },
            ResolutionReason = "done",
            QueryPolicies = ["myspace:/deep:content"],
        };

        var back = RoundTrip(original).Single();

        back.Shortname.ShouldBe("full");
        back.SpaceName.ShouldBe("myspace");
        back.Subpath.ShouldBe("/deep/nested");
        back.Uuid.ShouldBe(original.Uuid);
        back.IsActive.ShouldBeTrue();
        back.Slug.ShouldBe("the-slug");
        back.Displayname!.En.ShouldBe("Hello");
        back.Displayname.Ar.ShouldBe("مرحبا");
        back.Description!.En.ShouldBe("Described");
        back.Tags.ShouldBe(["alpha", "beta"]);
        back.CreatedAt.ShouldBe(original.CreatedAt);
        back.UpdatedAt.ShouldBe(original.UpdatedAt);
        back.OwnerShortname.ShouldBe("dmart");
        back.OwnerGroupShortname.ShouldBe("editors");
        back.LastChecksumHistory.ShouldBe("sha256:abc");
        back.ResourceType.ShouldBe(ResourceType.Ticket);
        back.State.ShouldBe("open");
        back.IsOpen.ShouldBe(true);
        back.WorkflowShortname.ShouldBe("wf");
        back.Collaborators!["reviewer"].ShouldBe("alice");
        back.ResolutionReason.ShouldBe("done");
        back.QueryPolicies.ShouldBe(["myspace:/deep:content"]);
    }

    // The mirror case: everything optional left null must come back null, not
    // as an empty string or a default-constructed object.
    [Fact]
    public void An_Entry_With_Every_Optional_Field_Null_Survives()
    {
        var back = RoundTrip(Minimal("bare")).Single();

        back.Slug.ShouldBeNull();
        back.Displayname.ShouldBeNull();
        back.Description.ShouldBeNull();
        back.OwnerGroupShortname.ShouldBeNull();
        back.Acl.ShouldBeNull();
        back.Payload.ShouldBeNull();
        back.Relationships.ShouldBeNull();
        back.LastChecksumHistory.ShouldBeNull();
        back.State.ShouldBeNull();
        back.IsOpen.ShouldBeNull();
        back.Reporter.ShouldBeNull();
        back.WorkflowShortname.ShouldBeNull();
        back.Collaborators.ShouldBeNull();
        back.ResolutionReason.ShouldBeNull();
        back.QueryPolicies.ShouldBeNull();

        // Tags is non-nullable in the model, so "no tags" must come back as an
        // empty list. Writing it as an optional column would make this null and
        // NullReference somewhere downstream instead.
        back.Tags.ShouldNotBeNull();
        back.Tags.ShouldBeEmpty();
    }

    // is_open is the only nullable BOOLEAN, and false-vs-null is exactly the
    // distinction a definition-level bug erases.
    [Fact]
    public void Nullable_Booleans_Distinguish_False_From_Null()
    {
        var back = RoundTrip(
            Minimal("a") with { IsOpen = true },
            Minimal("b") with { IsOpen = false },
            Minimal("c") with { IsOpen = null });

        back[0].IsOpen.ShouldBe(true);
        back[1].IsOpen.ShouldBe(false, "false must not be stored as absent");
        back[2].IsOpen.ShouldBeNull();
    }

    // Timestamps go out as UTC micros and must come back as the local-naive
    // DateTime the DB column holds. Round-tripping the INSTANT but returning a
    // UTC-kind value would change what every consumer reads.
    [Fact]
    public void Timestamps_Return_As_Local_Naive_Not_Utc()
    {
        var created = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Unspecified);
        var back = RoundTrip(Minimal("t") with { CreatedAt = created }).Single();

        back.CreatedAt.ShouldBe(created);
        back.CreatedAt.Kind.ShouldBe(DateTimeKind.Unspecified,
            "the DB column is timestamp-without-tz; handing back UTC changes the value read");
    }

    // JSON columns are opaque strings by design (§2.2), which only works if the
    // nesting survives. A payload is the deepest structure an entry carries.
    [Fact]
    public void Json_Columns_Keep_Their_Structure()
    {
        var original = Minimal("j") with
        {
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                SchemaShortname = "post",
                Body = System.Text.Json.JsonDocument
                    .Parse("""{"title":"Hi","count":3,"nested":{"deep":[1,2,3]}}""").RootElement,
            },
            Acl = [new AclEntry { UserShortname = "alice", AllowedActions = ["view", "update"] }],
        };

        var back = RoundTrip(original).Single();

        back.Payload!.ContentType.ShouldBe(ContentType.Json);
        back.Payload.SchemaShortname.ShouldBe("post");
        back.Acl!.Single().UserShortname.ShouldBe("alice");
        back.Acl.Single().AllowedActions.ShouldBe(["view", "update"]);

        // The nested structure is the point: an opaque JSON string is only
        // lossless if depth survives, not just the top-level keys.
        back.Payload.Body!.Value.GetProperty("nested").GetProperty("deep")
            .EnumerateArray().Select(x => x.GetInt32()).ShouldBe([1, 2, 3]);
    }

    // Values are written densely and nulls re-inserted from the definition
    // levels, so a mixed null pattern across MANY columns at once is where a
    // per-column offset error surfaces as values landing on the wrong rows.
    [Fact]
    public void Mixed_Null_Patterns_Across_Rows_Keep_Values_On_Their_Own_Row()
    {
        var back = RoundTrip(
            Minimal("r0") with { Slug = "s0", State = null,       IsOpen = true },
            Minimal("r1") with { Slug = null, State = "reviewing", IsOpen = null },
            Minimal("r2") with { Slug = "s2", State = null,       IsOpen = false },
            Minimal("r3") with { Slug = null, State = "closed",    IsOpen = null });

        back.Select(e => e.Shortname).ShouldBe(["r0", "r1", "r2", "r3"]);
        back.Select(e => e.Slug).ShouldBe(["s0", null, "s2", null]);
        back.Select(e => e.State).ShouldBe([null, "reviewing", null, "closed"]);
        back.Select(e => e.IsOpen).ShouldBe([true, null, false, null]);
    }

    // The export is meant to be readable by DuckDB and Spark, not only by us.
    // Asserting the column names and types from an independent reader is what
    // makes that claim checkable rather than aspirational.
    [FactIfPyArrow]
    public void PyArrow_Sees_The_Expected_Schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-ept-{Guid.NewGuid():N}.parquet");
        try
        {
            var rows = new[] { Minimal("x") with { Tags = ["t"] } };
            var writer = new ParquetFileWriter(EntryParquetTable.Schema);
            using (var fs = File.Create(path))
                writer.Write(fs, EntryParquetTable.BuildPages(rows), rows.Length);

            var schema = PyArrow.ReadSchema(path);
            schema.ShouldContain("shortname: string");
            // The Hive partition key must NOT also be a column, or every
            // partition-inferring reader fails to merge the two.
            schema.ShouldNotContain("space_name:");
            schema.ShouldContain("is_active: bool");
            // tz=UTC, not naive: TIMESTAMP_MICROS is UTC-adjusted by
            // definition, so consumers correctly see an instant. Our reader is
            // what converts back to the local-naive form dmart stores.
            schema.ShouldContain("updated_at: timestamp[us, tz=UTC]");
            schema.ShouldContain("payload: string");
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
