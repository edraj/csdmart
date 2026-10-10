using Dmart.DataAdapters.Sql;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The schema script on a PostgreSQL database from before the directory fields
// (docs/user-directory-fields.md): users without mailbox / mail_aliases /
// services, and none of the tables or indexes built on them. Every boot runs
// SqlSchema.CreateAll as ONE transaction, so a statement that reads a column
// before the script has added it fails the whole upgrade and the instance
// starts with no schema changes at all. A fresh database cannot catch that:
// there the CREATE TABLE already has the columns.
public sealed class DirectoryFieldsUpgradeTests
{
    [FactIfPostgresOnly]
    public async Task The_Schema_Script_Upgrades_A_Database_From_Before_The_Directory_Fields()
    {
        var schema = "upg_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new NpgsqlConnection(DmartFactory.PgConn);
        await conn.OpenAsync();
        async Task Run(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync();
        }

        await Run($"CREATE SCHEMA {schema}; SET search_path TO {schema}, public;");
        try
        {
            await Run(SqlSchema.CreateAll);
            // Back to the shape of a 1.5 database.
            await Run("""
                DROP TABLE user_addresses, user_services, directory_replica_state;
                ALTER TABLE users DROP COLUMN mailbox, DROP COLUMN mail_aliases, DROP COLUMN services;
                """);

            await Run(SqlSchema.CreateAll);

            await using var check = new NpgsqlCommand("""
                SELECT count(*) FROM pg_indexes
                 WHERE schemaname = current_schema()
                   AND indexname IN ('idx_users_services_gin', 'idx_users_mail_aliases_gin',
                                     'idx_users_shortname_bytes', 'idx_users_shortname_lower')
                """, conn);
            (await check.ExecuteScalarAsync()).ShouldBe(4L);
        }
        finally
        {
            await Run($"SET search_path TO public; DROP SCHEMA {schema} CASCADE;");
        }
    }
}
