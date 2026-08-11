using Microsoft.Data.Sqlite;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Backend-neutral classification of the ONE database error the service layer
// reacts to rather than propagates.
//
// Deliberately not a general error-mapping abstraction. Every other catch in
// the codebase is either PostgreSQL-only by construction (the deadlock retry
// in Db, the import constraint isolation, which does not run on SQLite) or
// wants the raw exception. Only the unique-violation case is genuinely shared:
// both backends enforce the same (shortname, space_name, subpath) index, and
// both must produce SHORTNAME_ALREADY_EXIST rather than a 500.
public static class DbErrors
{
    /// <summary>
    /// True when <paramref name="ex"/> is a unique/primary-key constraint
    /// violation from either backend.
    /// </summary>
    public static bool IsUniqueViolation(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState == "23505",
        // SQLITE_CONSTRAINT (19) is one code for every constraint kind, so the
        // primary code alone would also swallow NOT NULL, CHECK and foreign-key
        // failures — real bugs that must not be reported as "name taken". The
        // extended code is what distinguishes them.
        SqliteException lite => lite.SqliteExtendedErrorCode is 2067 // _CONSTRAINT_UNIQUE
                                                             or 1555, // _CONSTRAINT_PRIMARYKEY
        _ => false,
    };
}
