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

        try
        {
            var body = await PostAsync(admin.Client, Move(target.Shortname, renamed));
            body.Status.ShouldBe(Status.Success, JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));

            (await users.GetByShortnameAsync(target.Shortname)).ShouldBeNull();
            var moved = (await users.GetByShortnameAsync(renamed)).ShouldNotBeNull();
            moved.OwnerShortname.ShouldBe(renamed); // self-owned stays self-owned

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
            await ExecAsync("DELETE FROM histories WHERE space_name = 'management' AND subpath IN ($1, $2)",
                $"/users/{target.Shortname}", $"/users/{renamed}");
            await ExecAsync("DELETE FROM entries WHERE owner_shortname IN ($1, $2)", target.Shortname, renamed);
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

    private async Task ExecAsync(string sql, params string[] args)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(sql);
        foreach (var arg in args) DbParams.Add(cmd, arg);
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }
}
