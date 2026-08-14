using System.Diagnostics;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Dmart.Tests.Integration;

// Not a correctness test — a measurement, so the bulk path's complexity can be
// judged against what it actually buys. Skipped unless DMART_BENCH=1.
public class BulkRestoreBenchmark : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    private readonly ITestOutputHelper _out;
    public BulkRestoreBenchmark(DmartFactory f, ITestOutputHelper o) { _factory = f; _out = o; }

    // Plain [Fact] that returns early rather than a skip attribute: this is a
    // measurement, and it must never fail or slow an ordinary run.
    [Fact]
    public async Task Measure()
    {
        if (Environment.GetEnvironmentVariable("DMART_BENCH") != "1") return;

        const int N = 5000;
        var sp = _factory.Services;
        _factory.CreateClient();
        var svc = sp.GetRequiredService<ParquetArchiveService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "bench_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space, SpaceName = space,
            Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        var dir = Path.Combine(Path.GetTempPath(), $"dmart-bench-{Guid.NewGuid():N}");
        try
        {
            for (var i = 0; i < N; i++)
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"e{i:D6}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });

            await svc.ExportAsync(dir, space, "/", actor: null);

            async Task WipeAsync()
            {
                await using var conn = await sp.GetRequiredService<IDbConnectionFactory>().OpenAsync();
                await using var cmd = conn.CreateCommand();
                DbParams.Add(cmd, space);
                cmd.CommandText = "DELETE FROM entries WHERE space_name = $1";
                await cmd.ExecuteNonQueryAsync();
            }

            await WipeAsync();
            var sw = Stopwatch.StartNew();
            var bulk = await svc.ImportAsync(dir);
            sw.Stop();
            var bulkMs = sw.ElapsedMilliseconds;

            await WipeAsync();
            ParquetArchiveService.ForcePerRowRestore = true;
            sw.Restart();
            try { await svc.ImportAsync(dir); } finally { ParquetArchiveService.ForcePerRowRestore = false; }
            sw.Stop();
            var perRowMs = sw.ElapsedMilliseconds;

            _out.WriteLine($"rows={bulk.For("entries").Imported}  bulk={bulkMs}ms  per-row={perRowMs}ms  "
                         + $"speedup={(double)perRowMs / Math.Max(1, bulkMs):F1}x");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
        }
    }
}
