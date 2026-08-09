using System.Globalization;

namespace Dmart.DataAdapters.Sql;

// Conversions between CLR values and their SQLite TEXT representations.
//
// Centralized on purpose. SQLite has only INTEGER/REAL/TEXT/BLOB, so ordering
// and equality are properties of the *format*, not of a column type the engine
// enforces. If the writer and the schema default disagree about width by even
// one digit, ORDER BY silently returns the wrong order — there is no type error
// to catch it. One formatter, used by every writer and by the DDL defaults, is
// what makes that class of bug impossible.
public static class SqliteValues
{
    /// <summary>
    /// Timestamp storage format: fixed-width, most-significant-first, no offset.
    /// </summary>
    /// <remarks>
    /// Lexicographic-safe by construction — 27 characters for every value, so
    /// string comparison and chronological comparison agree. That is what lets
    /// ORDER BY created_at and BETWEEN work against a plain B-tree index with
    /// no functional index and no conversion.
    ///
    /// A space separator rather than 'T', matching what PostgreSQL renders and
    /// what SQLite's own date functions accept, so a value written here is
    /// still readable by strftime/julianday.
    ///
    /// SqliteSchema pads strftime's 3-digit %f out to these 7 fractional digits
    /// for its column defaults; changing the precision here without changing it
    /// there reintroduces the width mismatch this format exists to prevent.
    /// </remarks>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    /// <summary>
    /// Formats a DateTime for storage. The Kind is deliberately ignored rather
    /// than converted: dmart is timezone-less end to end (see TimeUtils), and
    /// a UTC conversion here would shift every wall-clock the operator sees.
    /// </summary>
    public static string FromDateTime(DateTime value)
        => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses a stored timestamp back, as Kind=Unspecified.</summary>
    public static DateTime ToDateTime(string value)
        => DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture,
                               DateTimeStyles.None);

    /// <summary>
    /// Parses a stored timestamp, tolerating a narrower fractional part.
    /// </summary>
    /// <remarks>
    /// Rows written by a column DEFAULT before the padding was in place, or by
    /// an external tool using strftime directly, carry 3 fractional digits
    /// instead of 7. Those still parse correctly; only their *ordering* against
    /// full-width values is affected, which is why writes always go through
    /// <see cref="FromDateTime"/>.
    /// </remarks>
    public static bool TryToDateTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrEmpty(value)) return false;
        string[] accepted =
        [
            TimestampFormat,
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss",
        ];
        return DateTime.TryParseExact(value, accepted, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out result);
    }

    /// <summary>
    /// Formats a Guid for storage: canonical lowercase, hyphenated, no braces.
    /// </summary>
    /// <remarks>
    /// Equality on a TEXT column is string equality, so the representation has
    /// to be pinned — "D" everywhere. Storing as BLOB is deliberately avoided:
    /// Microsoft.Data.Sqlite will happily write a Guid as a 16-byte BLOB, and a
    /// database holding both representations compares unequal for the same
    /// UUID with nothing to signal it.
    /// </remarks>
    public static string FromGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Booleans are INTEGER 0/1, matching SQLite's own convention.</summary>
    public static long FromBoolean(bool value) => value ? 1L : 0L;
}
