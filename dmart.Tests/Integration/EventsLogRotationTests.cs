using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The per-space events.jsonl used to grow without bound and type=events read
// ALL of it into memory per request. Now: SpaceEventLogger rolls the file over
// to events.jsonl.1 at EventsLogMaxBytes, and the reader walks both
// generations with a bounded retained set while still reporting an exact
// total.
public sealed class EventsLogRotationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public EventsLogRotationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Writer_Rolls_Over_And_Reader_Serves_Both_Generations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dmart-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dmart:SpacesFolder"] = root,
                    ["Dmart:EventsLogMaxBytes"] = "400",   // a handful of events per generation
                })));
            var logger = host.Services.GetRequiredService<SpaceEventLogger>();
            var query = host.Services.GetRequiredService<QueryService>();
            logger.Enabled.ShouldBeTrue();

            var space = $"itest_evr_{Guid.NewGuid():N}"[..20];
            const int n = 8;
            for (var i = 0; i < n; i++)
                await logger.LogAsync(new Event
                {
                    SpaceName = space, Subpath = "/", Shortname = $"e{i}",
                    ActionType = ActionType.Create, ResourceType = ResourceType.Content,
                    UserShortname = "dmart",
                });

            var live = logger.ResolveLogPath(space);
            File.Exists(live + ".1").ShouldBeTrue("the writer must have rolled the log over at the size cap");

            // Retention is bounded by design: ONE previous generation is kept,
            // so with a cap this small several rollovers have overwritten it.
            // What the reader must report is exactly what the two files hold.
            static int Lines(string p) => File.Exists(p) ? File.ReadAllLines(p).Length : 0;
            var onDisk = Lines(live + ".1") + Lines(live);
            onDisk.ShouldBeGreaterThan(3, "both generations together must exceed the page");
            onDisk.ShouldBeLessThan(n, "the overwritten generations are gone — bounded disk, not a log");

            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Events, SpaceName = space, Subpath = "/",
                Limit = 3, RetrieveTotal = true,
            }, _factory.AdminShortname);
            resp.Status.ShouldBe(Status.Success, resp.Error?.Message);
            ((int)resp.Attributes!["total"]).ShouldBe(onDisk, "total spans both generations");
            resp.Records!.Count.ShouldBe(3, "the page is bounded by limit, not by the file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
