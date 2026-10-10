namespace Dmart.DataAdapters.Sql;

// Rebuilds user_addresses / user_services from users when they are empty but
// some live user has directory fields (docs/user-directory-fields.md): the
// first start after the upgrade that added them, or a restore that loaded users
// through a path predating the index. Every ordinary write maintains the
// tables itself, so on any other boot this is one cheap probe.
//
// It must run after the schema initializers, which create the tables.
public sealed class DirectoryIndexRepair(
    IDbConnectionFactory db, UserRepository users, ILogger<DirectoryIndexRepair> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!db.IsConfigured) return;
        try
        {
            var rows = await users.RebuildDirectoryIndexIfEmptyAsync(cancellationToken);
            if (rows > 0)
                log.LogInformation("Rebuilt the user directory index: {Rows} address and service row(s)", rows);
        }
        catch (System.Data.Common.DbException ex)
        {
            // A clash here means two users already hold the same address in
            // their stored rows — possible only for data written around the
            // repository. Logged loudly, not fatal: everything except directory
            // lookups still works, and the operator has to choose who keeps it.
            log.LogError(ex, "rebuilding the user directory index failed; mail and LDAP lookups by address will miss until it succeeds");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
