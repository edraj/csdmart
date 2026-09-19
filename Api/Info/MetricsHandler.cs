using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Dmart.Models.Api;
using Dmart.Models.Enums;

namespace Dmart.Api.Info;

// GET /info/metrics — process and garbage-collector telemetry.
//
// WHY THIS EXISTS
//
// dmart advertises a small resident footprint and is deployed on hardware where
// that claim matters — a 416 MB board runs the server, PostgreSQL and this
// admin UI together. Until now nothing in the process reported *why* memory
// moved, so the only available signal was RSS sampled from outside, and RSS
// alone cannot distinguish the three things that make a managed process grow:
//
//   * a managed leak — a cache that never evicts, so the GC correctly keeps
//     objects that are reachable and unused;
//   * native memory the GC never sees — libargon2 mallocs its whole `m` for the
//     duration of a hash (19 MiB at the current defaults), Npgsql buffers,
//     `jq` subprocesses;
//   * the GC simply not returning freed segments to the OS, which it is not
//     obliged to do and generally does not.
//
// Those have identical RSS signatures and completely different remedies. Gen
// counts, heap size and allocation totals separate them.
//
// WHY IT IS ADMIN-ONLY
//
// It is mapped into the /info group, which carries GlobalAdminFilter. Load
// shape, memory pressure and uptime are modest reconnaissance, but the group's
// existing reasoning applies: the filter fails closed, and opting a route out
// of it should be a visible edit rather than a default. A Prometheus scraper
// therefore needs a bot token, the same as any other dmart client.
//
// FORMAT
//
// JSON `Response` envelope by default, matching every other route here.
// `?format=prometheus` returns text/plain in exposition format, because the
// thing most likely to consume a metrics endpoint cannot parse the envelope.
public static class MetricsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/metrics", (string? format) =>
        {
            var m = Collect();
            return string.Equals(format, "prometheus", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(Exposition(m), "text/plain; version=0.0.4; charset=utf-8")
                : Results.Json(
                    Response.Ok(records: [new Record
                    {
                        ResourceType = ResourceType.Content,
                        Shortname = "metrics",
                        Subpath = "/",
                        Attributes = m,
                    }]),
                    Dmart.Models.Json.DmartJsonContext.Default.Response);
        }).WithTags("Info");

    private static Dictionary<string, object> Collect()
    {
        var proc = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();

        // Gen2 is the number that answers "how often does the GC actually do
        // expensive work". Gen0 counts rise constantly under any load and say
        // little on their own; gen2 collections are what pause and what a
        // Large Object Heap allocation forces. The Argon2 work is the worked
        // example: a managed 19 MiB buffer is an LOH allocation, so five hashes
        // drove 2-3 gen2 collections, where the native path drives none.
        return new Dictionary<string, object>
        {
            ["gc_gen0_collections"] = GC.CollectionCount(0),
            ["gc_gen1_collections"] = GC.CollectionCount(1),
            ["gc_gen2_collections"] = GC.CollectionCount(2),

            ["gc_heap_bytes"] = GC.GetTotalMemory(forceFullCollection: false),
            ["gc_allocated_bytes_total"] = GC.GetTotalAllocatedBytes(precise: false),
            ["gc_heap_committed_bytes"] = info.TotalCommittedBytes,
            ["gc_pause_time_percent"] = Math.Round(info.PauseTimePercentage, 3),

            // Whether the runtime is under a hard limit at all. Documented for
            // small devices (DOTNET_GCHeapHardLimit); 0 here means unset, which
            // is worth being able to see rather than assume.
            ["gc_heap_hard_limit_bytes"] = info.TotalAvailableMemoryBytes,
            ["gc_server_mode"] = GCSettings.IsServerGC,
            ["gc_latency_mode"] = GCSettings.LatencyMode.ToString(),

            // The gap between working_set and gc_heap_bytes is where native
            // allocations live -- argon2's buffer while a login is in flight,
            // Npgsql, the SQLite provider. Reporting both is the point.
            ["process_working_set_bytes"] = proc.WorkingSet64,
            ["process_private_memory_bytes"] = proc.PrivateMemorySize64,
            ["process_cpu_seconds_total"] = Math.Round(proc.TotalProcessorTime.TotalSeconds, 2),
            ["process_threads"] = proc.Threads.Count,
            ["process_uptime_seconds"] = Math.Round(
                (DateTime.Now - proc.StartTime).TotalSeconds, 1),

            ["dotnet_version"] = RuntimeInformation.FrameworkDescription,
            ["runtime_identifier"] = RuntimeInformation.RuntimeIdentifier,
        };
    }

    // Prometheus exposition format. Hand-written rather than pulled from a
    // package: it is a dozen lines, and prometheus-net is not Native-AOT clean.
    private static string Exposition(Dictionary<string, object> m)
    {
        var sb = new System.Text.StringBuilder(1024);
        foreach (var (k, v) in m)
        {
            // A string cannot be a sample value, so build metadata is exposed
            // as a labelled `_info` gauge -- the Prometheus convention for it.
            if (v is string sv)
            {
                sb.Append("dmart_").Append(k).Append("_info{value=\"")
                  .Append(sv.Replace("\\", "\\\\", StringComparison.Ordinal)
                            .Replace("\"", "\\\"", StringComparison.Ordinal))
                  .Append("\"} 1\n");
                continue;
            }

            var value = v switch
            {
                bool b => b ? "1" : "0",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (value is null) continue;
            sb.Append("dmart_").Append(k).Append(' ').Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
