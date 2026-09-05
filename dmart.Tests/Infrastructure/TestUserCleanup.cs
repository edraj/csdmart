using Dmart.DataAdapters.Sql;
using Dmart.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Dmart.Tests.Infrastructure;

// Tests that successfully create a user via /user/create end up triggering
// the resource_folders_creation plugin, which materializes
//   personal/people/{shortname}             (folder)
//   personal/people/{shortname}/notifications
//   personal/people/{shortname}/private
//   personal/people/{shortname}/protected
//   personal/people/{shortname}/public
//   personal/people/{shortname}/inbox
// each with owner_shortname = the new user. A naive
// `users.DeleteAsync(shortname)` then trips the
// entries.owner_shortname → users.shortname FK.
//
// This helper purges everything the plugin (or future plugins) creates
// owned by that user across ALL FK-referencing tables, then deletes the
// user. Use it instead of `users.DeleteAsync(shortname)` whenever the
// test went through /user/create successfully.
public static class TestUserCleanup
{
    public static async Task DeleteUserAndOwnedAsync(IServiceProvider sp, string shortname)
    {
        // Let the plugin finish before purging what it wrote.
        //
        // resource_folders_creation is an AFTER-hook. Pinned `"concurrent":
        // false` it completes inside the request, so its rows are already there
        // when the purge below runs. Set `"concurrent": true` it is dispatched
        // with Task.Run and outlives the request — so its inserts can land
        // AFTER the purge, and `users.DeleteAsync` then trips the very
        // entries.owner_shortname FK this helper exists to avoid. The symptom
        // is a "FOREIGN KEY constraint failed" from the CLEANUP, attributed to
        // whichever test happened to be running, which is why it looked like an
        // unrelated flake wandering between test classes.
        //
        // DrainPluginHooksAttribute cannot cover this: it settles hooks AFTER
        // the test method, and this cleanup runs inside it.
        var plugins = sp.GetService<PluginManager>();
        if (plugins is not null)
            await plugins.WaitForIdleAsync(TimeSpan.FromSeconds(15));

        var db = sp.GetRequiredService<IDbConnectionFactory>();
        var users = sp.GetRequiredService<UserRepository>();

        await using (var conn = await db.OpenAsync())
        {
            // Order: drop FK-bearing rows in entries/attachments/roles/
            // permissions/spaces first (everything that REFERENCES
            // users.shortname), then the user row itself. Single connection,
            // single round-trip per table — fine for tests.
            foreach (var table in new[] { "attachments", "entries", "spaces", "roles", "permissions" })
            {
                await using var cmd = conn.Command($"DELETE FROM {table} WHERE owner_shortname = $1");
                DbParams.Add(cmd, shortname);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await users.DeleteAsync(shortname);
    }
}
