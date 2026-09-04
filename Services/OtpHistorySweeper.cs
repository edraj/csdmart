using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Periodic purge of `otps` rows older than OtpHistoryRetentionDays.
//
// Hourly cadence. Same Timer pattern as OAuthStoreSweeper. The first sweep
// fires one interval after startup; the Timer runs on the monotonic clock,
// so suspended time doesn't count toward it.
public sealed class OtpHistorySweeper(
    OtpRepository otps,
    IOptions<DmartSettings> settings,
    ILogger<OtpHistorySweeper> log) : IHostedService, IDisposable
{
    private Timer? _timer;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = SweepAsync(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task SweepAsync()
    {
        try
        {
            var days = settings.Value.OtpHistoryRetentionDays;
            if (days <= 0) return; // 0 disables purging entirely
            var cutoff = TimeUtils.Now().AddDays(-days);
            var purged = await otps.PurgeOlderThanAsync(cutoff);
            if (purged > 0)
                log.LogInformation("OTP history sweep purged {Count} rows older than {Cutoff}", purged, cutoff);
        }
        catch (Exception ex)
        {
            // A failed sweep only delays cleanup until the next tick; never
            // let it take the host down from a timer thread.
            log.LogWarning(ex, "OTP history sweep failed; will retry next interval");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
