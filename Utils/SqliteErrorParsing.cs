namespace Dmart.Utils;

// Pulls the offending identifier out of SQLite's error messages, so the HTTP
// layer can say the same thing it says on PostgreSQL.
//
// SQLite has no structured error fields — no Detail, no ConstraintName, no
// ColumnName. Everything is in the message text, which is why this parses
// where PgErrorParsing reads properties. The formats below are stable across
// SQLite 3.x; a message this code does not recognise yields null, and the
// caller falls back to its generic wording rather than guessing.
public static class SqliteErrorParsing
{
    private const string UniquePrefix = "UNIQUE constraint failed: ";
    private const string NoColumnPrefix = "no such column: ";

    /// <summary>
    /// The column a UNIQUE violation collided on, or null.
    /// </summary>
    /// <remarks>
    /// Two message shapes: "UNIQUE constraint failed: users.email" for a table
    /// constraint, and "UNIQUE constraint failed: index 'idx_users_email_lower_unique'"
    /// for a partial or expression index, which names the index instead of the
    /// column. The second shape is recovered the same way PgErrorParsing
    /// recovers it from a constraint name.
    /// </remarks>
    public static string? ExtractUniqueViolationColumn(string? message, string? tableName = null)
    {
        if (message is null || !message.StartsWith(UniquePrefix, StringComparison.Ordinal)) return null;
        var rest = message[UniquePrefix.Length..].Trim();

        if (rest.StartsWith("index '", StringComparison.Ordinal))
        {
            var end = rest.IndexOf('\'', 7);
            if (end < 0) return null;
            return PgErrorParsing.ExtractUniqueViolationKey(rest[7..end], tableName);
        }

        // "users.email" or, on a composite index, "users.a, users.b" — report
        // the first, matching what PostgreSQL's Detail leads with.
        var first = rest.Split(',')[0].Trim();
        var dot = first.LastIndexOf('.');
        var column = dot >= 0 ? first[(dot + 1)..] : first;
        return column.Length == 0 ? null : column;
    }

    /// <summary>The name in "no such column: x", or null.</summary>
    public static string? ExtractUndefinedColumn(string? message)
    {
        if (message is null) return null;
        var at = message.IndexOf(NoColumnPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        // The name runs to end-of-message; SQLite appends nothing after it.
        var name = message[(at + NoColumnPrefix.Length)..].Trim();
        return name.Length == 0 ? null : name;
    }
}
