using Dmart.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Moving an entry must carry its history along. MoveOnceAsync re-keyed entries,
// attachments and locks but never `histories`, so every pre-move history row
// was orphaned at coordinates nothing lives at; once history became parent-ACL
// filtered (fail-closed) those orphans were invisible to EVERYONE and a move
// silently truncated the audit trail. The one history row the move itself
// writes at the destination hid the loss from the existing move tests.
public sealed class MoveRekeysHistoryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public MoveRekeysHistoryTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task PreMove_History_Is_Visible_At_The_New_Coordinates()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var entries = sp.GetRequiredService<EntryRepository>();
        var history = sp.GetRequiredService<HistoryRepository>();
        var entryService = sp.GetRequiredService<EntryService>();
        var query = sp.GetRequiredService<QueryService>();

        var space = $"itest_mvh_{Guid.NewGuid():N}"[..20];
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "doc", SpaceName = space, Subpath = "/a",
            OwnerShortname = "dmart", ResourceType = ResourceType.Content, IsActive = true,
            Payload = new Payload { ContentType = ContentType.Json, Body = JsonDocument.Parse("{\"v\":1}").RootElement.Clone() },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        // A history row written BEFORE the move, at the entry's original coords.
        await history.AppendAsync(space, "/a", "doc", "dmart", null,
            new Dictionary<string, object> { ["marker"] = new Dictionary<string, object?> { ["old"] = null, ["new"] = "pre_move" } });

        var moved = await entryService.MoveAsync(
            new Locator(ResourceType.Content, space, "/a", "doc"),
            new Locator(ResourceType.Content, space, "/b", "doc2"),
            _factory.AdminShortname);
        moved.IsOk.ShouldBeTrue(moved.ErrorMessage);

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = space, Subpath = "/b",
            FilterShortnames = new() { "doc2" }, Limit = 50,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success, resp.Error?.Message);

        var diffs = resp.Records!.Select(r => r.Attributes is { } a && a.TryGetValue("diff", out var d) ? d?.ToString() ?? "" : "");
        diffs.ShouldContain(d => d.Contains("pre_move", StringComparison.Ordinal),
            "the pre-move history row must follow the entry to its new coordinates");

        // And nothing is left behind at the old coordinates.
        var old = await query.ExecuteAsync(new Query
        {
            Type = QueryType.History, SpaceName = space, Subpath = "/a",
            FilterShortnames = new() { "doc" }, Limit = 50,
        }, _factory.AdminShortname);
        (old.Records?.Count ?? 0).ShouldBe(0);
    }
}
