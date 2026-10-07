using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `move` on a user is a rename. Users live in their own table, so the request
// used to fall through to EntryService.MoveAsync and fail with "source entry
// missing" for every user. owner_shortname is a deferrable FK → users, so the
// rename has to carry every owned row with it or the COMMIT fails.
public sealed class MoveUserTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public MoveUserTests(DmartFactory factory) => _factory = factory;

    // The shape the cxb client sends: subpath without the leading slash and
    // the full src_/dest_ attribute set.
    private static Request Move(string from, string to, string destSubpath = "users", string destSpace = "management") => new()
    {
        RequestType = RequestType.Move, SpaceName = "management",
        Records = new()
        {
            new Record
            {
                ResourceType = ResourceType.User, Subpath = "users", Shortname = from,
                Attributes = new()
                {
                    ["src_space_name"] = "management", ["src_subpath"] = "users", ["src_shortname"] = from,
                    ["dest_space_name"] = destSpace, ["dest_subpath"] = destSubpath, ["dest_shortname"] = to,
                },
            },
        },
    };

    private async Task<Response> PostAsync(HttpClient client, Request req)
    {
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        return JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
    }

    [FactIfPg]
    public async Task Move_User_Renames_And_Carries_Owned_Rows()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var target = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var history = _factory.Services.GetRequiredService<HistoryRepository>();
        var renamed = $"rn_{Guid.NewGuid():N}"[..16];
        var entryName = $"own_{Guid.NewGuid():N}"[..16];
        var folderName = $"fld_{Guid.NewGuid():N}"[..16];
        var groupedName = $"grp_{Guid.NewGuid():N}"[..16];
        var stale = TimeUtils.Now().AddDays(-1);

        Entry Owned(string shortname, ResourceType type, string? ownerGroup = null) => new()
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = "test", Subpath = "/move_user/deep",
            OwnerShortname = target.Shortname, OwnerGroupShortname = ownerGroup, ResourceType = type, IsActive = true,
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        };
        // Content and folder take the set-based fast path; a group named like
        // the user makes ":<name>" ambiguous, so that row takes the per-row path.
        await entries.UpsertAsync(Owned(entryName, ResourceType.Content));
        await entries.UpsertAsync(Owned(folderName, ResourceType.Folder));
        await entries.UpsertAsync(Owned(groupedName, ResourceType.Content, ownerGroup: target.Shortname));
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "note1", SpaceName = "management",
            Subpath = $"/users/{target.Shortname}", ResourceType = ResourceType.Comment,
            OwnerShortname = target.Shortname, IsActive = true, Body = "hi",
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        // Attachment-history sits at the attachment's coords, under the user's path.
        await history.AppendAsync("management", $"/users/{target.Shortname}", "note1", target.Shortname, null,
            new Dictionary<string, object> { ["marker"] = new Dictionary<string, object?> { ["old"] = null, ["new"] = "pre_rename" } });
        // Someone ELSE's comment under the user's record: not owned by the
        // user, so only the path relocation touches it — and that relocation
        // must still look like a change to an incremental export.
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "note2", SpaceName = "management",
            Subpath = $"/users/{target.Shortname}", ResourceType = ResourceType.Comment,
            OwnerShortname = admin.Shortname, IsActive = true, Body = "admin note",
            CreatedAt = stale, UpdatedAt = stale,
        });
        // Something the user DID, elsewhere: audit authorship is immutable.
        await history.AppendAsync("test", "/move_user/deep", entryName, target.Shortname, null,
            new Dictionary<string, object> { ["marker"] = new Dictionary<string, object?> { ["old"] = null, ["new"] = "authored" } });

        try
        {
            var body = await PostAsync(admin.Client, Move(target.Shortname, renamed));
            body.Status.ShouldBe(Status.Success, JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));

            (await users.GetByShortnameAsync(target.Shortname)).ShouldBeNull();
            var moved = (await users.GetByShortnameAsync(renamed)).ShouldNotBeNull();
            moved.OwnerShortname.ShouldBe(renamed); // self-owned stays self-owned

            var relocated = (await attachments.GetAsync("management", $"/users/{renamed}", "note2")).ShouldNotBeNull();
            relocated.OwnerShortname.ShouldBe(admin.Shortname);
            relocated.UpdatedAt.ShouldBeGreaterThan(stale, "a relocated attachment must surface in an incremental export");
            (await ScalarAsync("SELECT count(*) FROM deletions WHERE table_name = 'attachments' AND space_name = 'management' AND subpath = $1 AND shortname = 'note2'",
                $"/users/{target.Shortname}")).ShouldBe(1L, "the old coordinates are tombstoned, like the user row's");

            (await ScalarAsync("SELECT owner_shortname FROM histories WHERE space_name = 'test' AND subpath = '/move_user/deep' AND shortname = $1", entryName))
                .ShouldBe(target.Shortname, "history authorship is an audit record and must not be rewritten");

            // Whichever path a row took, its policies must be exactly what a
            // fresh generation for the new owner produces.
            foreach (var (name, type) in new[]
            {
                (entryName, ResourceType.Content), (folderName, ResourceType.Folder), (groupedName, ResourceType.Content),
            })
            {
                var entry = (await entries.GetAsync("test", "/move_user/deep", name, type)).ShouldNotBeNull();
                entry.OwnerShortname.ShouldBe(renamed);
                entry.QueryPolicies.ShouldNotBeNull().OrderBy(p => p, StringComparer.Ordinal)
                    .ShouldBe(QueryPolicies.Generate(entry).OrderBy(p => p, StringComparer.Ordinal), $"{name} ({type})");
            }
            var grouped = (await entries.GetAsync("test", "/move_user/deep", groupedName, ResourceType.Content)).ShouldNotBeNull();
            grouped.QueryPolicies.ShouldNotBeNull().ShouldContain($"test:move_user/deep:content:true:{target.Shortname}"); // group literal kept

            (await attachments.ListForParentAsync("management", "/users", renamed))
                .Select(a => a.Shortname).ShouldContain("note1");

            Query NoteHistory(string owner) => new()
            {
                Type = QueryType.History, SpaceName = "management", Subpath = $"/users/{owner}",
                FilterShortnames = new() { "note1" }, Limit = 50,
            };
            (await history.QueryHistoryAsync(NoteHistory(renamed))).Count.ShouldBe(1, "attachment history must follow the rename");
            (await history.QueryHistoryAsync(NoteHistory(target.Shortname))).ShouldBeEmpty();
        }
        finally
        {
            await ExecAsync("DELETE FROM attachments WHERE owner_shortname IN ($1, $2)", target.Shortname, renamed);
            await ExecAsync("DELETE FROM attachments WHERE space_name = 'management' AND subpath IN ($1, $2)",
                $"/users/{target.Shortname}", $"/users/{renamed}");
            await ExecAsync("DELETE FROM histories WHERE space_name = 'management' AND subpath IN ($1, $2)",
                $"/users/{target.Shortname}", $"/users/{renamed}");
            await ExecAsync("DELETE FROM histories WHERE space_name = 'test' AND subpath = '/move_user/deep' AND shortname = $1", entryName);
            await ExecAsync("DELETE FROM entries WHERE owner_shortname IN ($1, $2)", target.Shortname, renamed);
            try { await users.DeleteAsync(renamed); } catch { }
            await target.Cleanup();
            await admin.Cleanup();
        }
    }

    // The gate answers before anything about the target does: a caller
    // without move access must get the same reply for a user that exists, one
    // that was deleted and one that never existed — otherwise `move` is an
    // account-enumeration oracle for any logged-in user.
    [FactIfPg]
    public async Task Move_User_Is_Authorized_Before_Anything_Is_Revealed()
    {
        var nobody = await _factory.CreateLoggedInUserAsync(roles: new());
        var live = await _factory.CreateTestUserAsync();
        var deleted = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        try
        {
            await users.SoftDeleteAsync(deleted.Shortname);
            var missing = $"nx_{Guid.NewGuid():N}"[..16];

            foreach (var (from, label) in new[] { (live.Shortname, "live"), (deleted.Shortname, "deleted"), (missing, "missing") })
            {
                // A bogus destination too: the real space:subpath must not be
                // echoed back before the gate either.
                var body = await PostAsync(nobody.Client, Move(from, from + "x", destSubpath: "elsewhere"));
                body.Status.ShouldBe(Status.Failed, label);
                var (code, message) = FirstFailure(body);
                code.ShouldBe(InternalErrorCode.NOT_ALLOWED, label);
                message.ShouldBe("no move access", label);
            }
        }
        finally
        {
            await deleted.Cleanup();
            await live.Cleanup();
            await nobody.Cleanup();
        }
    }

    // Both sentinel rows the server recreates when missing are protected, not
    // just the bootstrap admin: renaming `anonymous` would drop the world role
    // for every unauthenticated caller until the next restart re-seeded it.
    [FactIfPg]
    public async Task Move_Anonymous_Sentinel_Is_Refused()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var renamed = $"anon_{Guid.NewGuid():N}"[..16];
        try
        {
            (await users.GetByShortnameAsync("anonymous")).ShouldNotBeNull("AdminBootstrap seeds the anonymous row");

            var body = await PostAsync(admin.Client, Move("anonymous", renamed));
            body.Status.ShouldBe(Status.Failed);
            var (code, message) = FirstFailure(body);
            code.ShouldBe(InternalErrorCode.NOT_ALLOWED);
            message.ShouldContain("anonymous");

            (await users.GetByShortnameAsync("anonymous")).ShouldNotBeNull();
            (await users.GetByShortnameAsync(renamed)).ShouldBeNull();
        }
        finally
        {
            await admin.Cleanup();
        }
    }

    // cxb's bulk move sends dest_shortname = shortname (it moves between
    // subpaths, which a user cannot do), so "keep this user in /users" is the
    // one move the UI can express for a user: a no-op, and a success — not
    // "destination already occupied" by the user itself.
    [FactIfPg]
    public async Task Move_User_Onto_Itself_Is_A_NoOp_Success()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var target = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        try
        {
            var body = await PostAsync(admin.Client, Move(target.Shortname, target.Shortname));
            body.Status.ShouldBe(Status.Success, JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));
            (await users.GetByShortnameAsync(target.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            await target.Cleanup();
            await admin.Cleanup();
        }
    }

    // Force-delete removes only what the deleted user OWNED, so another
    // actor's lock on, or comment under, a long-deleted user survives at
    // coordinates nothing lives at. Those orphans sit exactly where a rename
    // onto the vacated name needs to put the user's own rows; the unique
    // indexes must not turn them into "destination already occupied" for a
    // user that does not exist (and that no UI could clear).
    [FactIfPg]
    public async Task Move_User_Purges_Orphans_At_The_Destination()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var target = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var locks = _factory.Services.GetRequiredService<LockRepository>();
        var renamed = $"rn_{Guid.NewGuid():N}"[..16];

        // An admin lock on the (non-existent) user record at the destination,
        // and an admin comment under it that collides by shortname with the
        // user's own comment.
        (await locks.TryLockAsync("management", "/users", renamed, admin.Shortname, 300)).ShouldBe(LockOutcome.Acquired);
        Attachment Note(string under, string owner, string body) => new()
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "note1", SpaceName = "management",
            Subpath = $"/users/{under}", ResourceType = ResourceType.Comment,
            OwnerShortname = owner, IsActive = true, Body = body,
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        };
        await attachments.UpsertAsync(Note(renamed, admin.Shortname, "orphan"));
        await attachments.UpsertAsync(Note(target.Shortname, target.Shortname, "mine"));
        try
        {
            var body = await PostAsync(admin.Client, Move(target.Shortname, renamed));
            body.Status.ShouldBe(Status.Success, JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));

            (await users.GetByShortnameAsync(renamed)).ShouldNotBeNull();
            (await locks.GetLockerAsync("management", "/users", renamed, 300)).ShouldBeNull("the orphan lock is purged, not inherited");
            var survivor = (await attachments.GetAsync("management", $"/users/{renamed}", "note1")).ShouldNotBeNull();
            survivor.Body.ShouldBe("mine", "the user's own attachment wins over the orphan at the destination");
            (await ScalarAsync("SELECT count(*) FROM deletions WHERE table_name = 'attachments' AND space_name = 'management' AND subpath = $1 AND shortname = 'note1'",
                $"/users/{renamed}")).ShouldBe(1L, "the purged orphan is tombstoned like any other delete");
        }
        finally
        {
            await ExecAsync("DELETE FROM locks WHERE space_name = 'management' AND subpath = '/users' AND shortname = $1", renamed);
            await ExecAsync("DELETE FROM attachments WHERE space_name = 'management' AND subpath IN ($1, $2)",
                $"/users/{target.Shortname}", $"/users/{renamed}");
            await ExecAsync("DELETE FROM histories WHERE space_name = 'management' AND subpath IN ($1, $2)",
                $"/users/{target.Shortname}", $"/users/{renamed}");
            try { await users.DeleteAsync(renamed); } catch { }
            await target.Cleanup();
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Move_User_Onto_Existing_User_Fails_And_Changes_Nothing()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var a = await _factory.CreateTestUserAsync();
        var b = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        try
        {
            var body = await PostAsync(admin.Client, Move(a.Shortname, b.Shortname));
            body.Status.ShouldBe(Status.Failed);
            (await users.GetByShortnameAsync(a.Shortname)).ShouldNotBeNull();
            (await users.GetByShortnameAsync(b.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            await a.Cleanup();
            await b.Cleanup();
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Move_User_Out_Of_Users_Subpath_Is_Rejected()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var target = await _factory.CreateTestUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        try
        {
            var body = await PostAsync(admin.Client, Move(target.Shortname, target.Shortname + "x", destSubpath: "elsewhere"));
            body.Status.ShouldBe(Status.Failed);
            (await users.GetByShortnameAsync(target.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            await target.Cleanup();
            await admin.Cleanup();
        }
    }

    // Aggregate envelope: per-record failures land in error.info[0].failed[].
    private static (int Code, string Message) FirstFailure(Response body)
    {
        var first = ((JsonElement)body.Error!.Info![0]["failed"])[0];
        return (first.GetProperty("error_code").GetInt32(), first.GetProperty("error").GetString() ?? "");
    }

    private async Task ExecAsync(string sql, params string[] args)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(sql);
        foreach (var arg in args) DbParams.Add(cmd, arg);
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    private async Task<object?> ScalarAsync(string sql, params string[] args)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(sql);
        foreach (var arg in args) DbParams.Add(cmd, arg);
        var raw = await cmd.ExecuteScalarAsync();
        // count(*) is int64 on both engines; a text column comes back as string.
        return raw is string ? raw : DbParams.ReadCount(raw);
    }
}
