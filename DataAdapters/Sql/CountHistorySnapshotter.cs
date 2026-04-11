using Microsoft.Extensions.Hosting;

namespace Dmart.DataAdapters.Sql;

// IHostedService that periodically writes per-space entry counts to count_history.
// Mirrors dmart Python's analytical snapshot behaviour: append-only rows that other
// dashboards graph over time. Default cadence is 6 hours; override via
// Dmart:CountHistoryIntervalMinutes if you want a different rhythm.
public sealed class CountHistorySnapshotter(
    CountHistoryRepository repo,
    Microsoft.Extensions.Options.IOptions<Dmart.Config.DmartSettings> settings,
    ILogger<CountHistorySnapshotter> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait briefly so SchemaInitializer + AdminBootstrap have run first.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }

        var minutes = settings.Value.CountHistoryIntervalMinutes;
        var interval = TimeSpan.FromMinutes(Math.Max(1, minutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await repo.RecordSnapshotForAllSpacesAsync(ct);
                log.LogDebug("count_history snapshot written");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "count_history snapshot failed");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
