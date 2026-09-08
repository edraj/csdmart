using System.Diagnostics;
using System.Text.Json;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// The pool is the only thing that lets a plugin handle more than one call at a
// time, so the cases that matter are the ones a reader would otherwise have to
// take on trust: that N workers really do run concurrently, that the default
// really is the old single-process behaviour, and that a respawned worker is
// told what the host supports instead of silently losing its callbacks.
//
// Fake plugins are POSIX shell so the suite depends on nothing but /bin/sh.
// Each one answers the info frame, because the pool handshakes every worker as
// it starts and a plugin that ignores it would never come up.
[Collection(Dmart.Tests.Integration.PluginInvocationContextCollection.Name)]
public class SubprocessPluginPoolTests : IDisposable
{
    private const string InfoFrame = """{"type":"info","host":{"callbacks":1}}""";

    private readonly List<string> _roots = new();
    private readonly List<SubprocessPluginPool> _pools = new();

    public void Dispose()
    {
        foreach (var p in _pools) { try { p.Dispose(); } catch { } }
        foreach (var r in _roots) { try { Directory.Delete(r, true); } catch { } }
    }

    private string WritePlugin(string shortname, string script, int? workers = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "dmart-pool-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, shortname);
        Directory.CreateDirectory(dir);
        _roots.Add(root);

        var exe = Path.Combine(dir, shortname);
        File.WriteAllText(exe, script);
#pragma warning disable CA1416 // /bin/sh fakes — this class is POSIX-only by construction
        File.SetUnixFileMode(exe,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        var w = workers is null ? "" : $",\"workers\":{workers}";
        File.WriteAllText(Path.Combine(dir, "config.json"),
            $$"""{"shortname":"{{shortname}}","is_active":true,"type":"hook"{{w}}}""");
        return exe;
    }

    private SubprocessPluginPool Pool(string shortname, string script, int workers)
    {
        var exe = WritePlugin(shortname, script, workers);
        var pool = new SubprocessPluginPool(exe, shortname, workers, InfoFrame);
        _pools.Add(pool);
        return pool;
    }

    // Answers info, then sleeps before answering each hook — so wall-clock across
    // concurrent calls is what distinguishes a pool from a queue.
    private const string SlowPlugin = """
        #!/bin/sh
        while IFS= read -r line; do
          case "$line" in
            *'"type":"info"'*) echo '{"shortname":"slow","type":"hook"}' ;;
            *) sleep 0.25; echo '{"status":"ok"}' ;;
          esac
        done
        """;

    private static double ElapsedMsForFourCalls(SubprocessPluginPool pool)
    {
        var sw = Stopwatch.StartNew();
        Parallel.For(0, 4, _ => pool.SendAndReceive("""{"type":"hook","event":{}}"""));
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Four_Workers_Run_Four_Calls_Concurrently()
    {
        // Four 250ms calls: ~250ms pooled, ~1000ms if they queue. The bound is
        // set well clear of both so a slow machine cannot turn this into a
        // timing flake.
        var pool = Pool("slow4", SlowPlugin, workers: 4);
        pool.Workers.ShouldBe(4);
        ElapsedMsForFourCalls(pool).ShouldBeLessThan(700);
    }

    [Fact]
    public void One_Worker_Still_Serializes()
    {
        // The other half of the claim. Without this, the test above would pass
        // just as happily if the sleep were not doing anything.
        var pool = Pool("slow1", SlowPlugin, workers: 1);
        pool.Workers.ShouldBe(1);
        ElapsedMsForFourCalls(pool).ShouldBeGreaterThan(800);
    }

    [Fact]
    public void Work_Is_Spread_Across_Distinct_Processes()
    {
        // Reports the worker's own pid. More than one distinct value proves the
        // calls landed on different processes -- and is exactly why state a
        // plugin keeps between calls becomes per-worker once workers > 1.
        var pool = Pool("pids", """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"info"'*) echo '{"shortname":"pids","type":"hook"}' ;;
                *) sleep 0.25; printf '{"status":"ok","pid":%s}\n' "$$" ;;
              esac
            done
            """, workers: 3);

        var pids = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 6, _ =>
        {
            var r = pool.SendAndReceive("""{"type":"hook","event":{}}""");
            pids.Add(JsonDocument.Parse(r).RootElement.GetProperty("pid").GetInt32());
        });

        pids.Count.ShouldBe(6);
        pids.Distinct().Count().ShouldBeGreaterThan(1);
        pids.Distinct().Count().ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Every_Worker_Is_Handshaked_Before_It_Serves_Traffic()
    {
        // A worker that never received the info frame would believe the host
        // answers no callbacks and would quietly stop making them. Each worker
        // appends on handshake; three workers means three lines before any hook
        // is dispatched.
        var dir = Path.GetDirectoryName(WritePlugin("hs", "placeholder"))!;
        var marker = Path.Combine(dir, "handshakes.txt");
        var exe = Path.Combine(dir, "hs");
        // $$ so the shell's own braces stay literal and only {{marker}} interpolates.
        File.WriteAllText(exe, $$"""
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"info"'*)
                  echo hs >> "{{marker}}"
                  echo '{"shortname":"hs","type":"hook"}' ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);
#pragma warning disable CA1416
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416

        var pool = new SubprocessPluginPool(exe, "hs", 3, InfoFrame);
        _pools.Add(pool);

        File.ReadAllLines(marker).Length.ShouldBe(3);
        pool.InfoResponse.ShouldNotBeNull();
        JsonDocument.Parse(pool.InfoResponse!).RootElement
            .GetProperty("shortname").GetString().ShouldBe("hs");
    }

    [Fact]
    public void A_Respawned_Worker_Is_Handshaked_Again()
    {
        // The regression this fixes: a crashed worker used to be replaced by a
        // process that had never been told what the host supports, so a plugin
        // caching that answer silently stopped calling back after its first
        // fault, with nothing in the log to explain it.
        var dir = Path.GetDirectoryName(WritePlugin("respawn", "placeholder"))!;
        var marker = Path.Combine(dir, "handshakes.txt");
        var exe = Path.Combine(dir, "respawn");
        File.WriteAllText(exe, $$"""
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"info"'*)
                  echo hs >> "{{marker}}"
                  echo '{"shortname":"respawn","type":"hook"}' ;;
                *'"die"'*) exit 1 ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);
#pragma warning disable CA1416
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416

        var pool = new SubprocessPluginPool(exe, "respawn", 1, InfoFrame);
        _pools.Add(pool);
        File.ReadAllLines(marker).Length.ShouldBe(1);

        // Kill it mid-exchange, then make a normal call: the host respawns and
        // must replay the handshake to the new process.
        pool.SendAndReceive("""{"type":"hook","event":{"die":true}}""");
        var after = pool.SendAndReceive("""{"type":"hook","event":{}}""");

        // Greater-than rather than an exact count: the death also triggers the
        // host's one retry, which respawns (and so handshakes) before
        // re-delivering, and that plugin dies on the retry too. The number of
        // respawns is an implementation detail of the retry policy; what this
        // pins is that a replacement process is never left un-handshaked.
        File.ReadAllLines(marker).Length.ShouldBeGreaterThan(1);
        JsonDocument.Parse(after).RootElement.GetProperty("status").GetString().ShouldBe("ok");
    }

    [Fact]
    public void Worker_Count_Comes_From_Config_And_Defaults_To_One()
    {
        // Default 1 keeps the behaviour every plugin written before pooling
        // assumes; a plugin only gets concurrency when an operator asks.
        var noSetting = WritePlugin("nocfg", SlowPlugin);
        NativePluginLoader.ReadWorkerCount(noSetting, "nocfg").ShouldBe(1);

        var withSetting = WritePlugin("wcfg", SlowPlugin, workers: 5);
        NativePluginLoader.ReadWorkerCount(withSetting, "wcfg").ShouldBe(5);
    }

    [Fact]
    public void An_Absurd_Worker_Count_Is_Clamped_Rather_Than_Honoured()
    {
        // `workers` is operator-edited JSON. A stray extra zero should not
        // decide how many processes dmart forks.
        var pool = Pool("clamped", SlowPlugin, workers: 5000);
        pool.Workers.ShouldBe(SubprocessPluginPool.MaxWorkers);

        var zero = Pool("zero", SlowPlugin, workers: 0);
        zero.Workers.ShouldBe(SubprocessPluginPool.MinWorkers);
    }
}
