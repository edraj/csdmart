using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// `--drop-indexes` executes DDL that cannot be parameterised, and the text
// round-trips through the checkpoint sidecar — a JSON file on disk between the
// DROP and the rebuild. So by execution time it is NOT simply trusted catalog
// output: a tampered or corrupted sidecar would be an arbitrary-SQL vector
// running with the import role's privileges.
//
// These pin the validators that close that gap. The accept cases are verbatim
// `pg_indexes.indexdef` output captured from PostgreSQL 18.3, so a schema
// change that alters the shape fails here rather than silently disabling the
// drop/rebuild at runtime.
public sealed class ImportBulkIndexValidationTests
{
    [Theory]
    // Every GIN index the real schema produces on entries.
    [InlineData("CREATE INDEX idx_entries_payload_gin ON public.entries USING gin (payload jsonb_path_ops)")]
    [InlineData("CREATE INDEX idx_entries_tags_gin ON public.entries USING gin (tags jsonb_path_ops)")]
    [InlineData("CREATE INDEX idx_entries_acl_gin ON public.entries USING gin (acl jsonb_path_ops)")]
    [InlineData("CREATE INDEX idx_entries_relationships_gin ON public.entries USING gin (relationships jsonb_path_ops)")]
    [InlineData("CREATE INDEX idx_entries_query_policies_gin ON public.entries USING gin (query_policies)")]
    [InlineData("CREATE INDEX idx_entries_payload_trgm ON public.entries USING gin (((payload)::text) gin_trgm_ops)")]
    [InlineData("CREATE INDEX idx_attachments_payload_gin ON public.attachments USING gin (payload jsonb_path_ops)")]
    public void RealCatalogDefinitions_AreAccepted(string def)
        => ImportBulkIndexes.IsSafeDefinition(def).ShouldBeTrue(def);

    [Theory]
    // Statement chaining — the whole point of the guard.
    [InlineData("CREATE INDEX i ON public.entries USING gin (payload); DROP TABLE entries")]
    [InlineData("CREATE INDEX i ON public.entries USING gin (payload jsonb_path_ops);")]
    // Not a CREATE INDEX at all.
    [InlineData("DROP TABLE entries")]
    [InlineData("UPDATE entries SET is_active = false")]
    // A table this feature does not manage.
    [InlineData("CREATE INDEX i ON public.users USING gin (payload jsonb_path_ops)")]
    // Not GIN — the btrees must never be dropped or recreated by this path.
    [InlineData("CREATE INDEX i ON public.entries USING btree (space_name)")]
    // Unique index: would be a constraint-backing index.
    [InlineData("CREATE UNIQUE INDEX i ON public.entries USING gin (payload)")]
    // Schema-qualified elsewhere, or unqualified.
    [InlineData("CREATE INDEX i ON evil.entries USING gin (payload)")]
    [InlineData("CREATE INDEX i ON entries USING gin (payload)")]
    [InlineData("")]
    public void UnexpectedDefinitions_AreRejected(string def)
        => ImportBulkIndexes.IsSafeDefinition(def).ShouldBeFalse(def);

    [Theory]
    [InlineData("idx_entries_payload_gin")]
    [InlineData("_leading_underscore")]
    [InlineData("Mixed_Case_9")]
    public void PlainIdentifiers_AreAcceptedAsNames(string name)
        => ImportBulkIndexes.IsSafeName(name).ShouldBeTrue(name);

    [Theory]
    // Anything that could escape the identifier position in `DROP INDEX public.<name>`.
    [InlineData("idx\"; DROP TABLE entries; --")]
    [InlineData("idx; DROP TABLE entries")]
    [InlineData("idx with spaces")]
    [InlineData("\"quoted\"")]
    [InlineData("idx--comment")]
    [InlineData("9leading_digit")]
    [InlineData("")]
    public void DangerousNames_AreRejected(string name)
        => ImportBulkIndexes.IsSafeName(name).ShouldBeFalse(name);
}
