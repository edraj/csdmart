using System.Collections.Concurrent;

namespace Dmart.Plugins.Native;

// One plugin, N interchangeable worker processes.
//
// A SubprocessPluginHost owns a single process and a single stdin/stdout pipe
// pair, and serializes every exchange through it. That is not an arbitrary
// restriction: the hook and request frames carry no correlation id, so a
// response can only be matched to its request by arrival order, and two
// exchanges sharing a pipe would each be free to read the other's answer.
//
// The alternative — adding ids and demultiplexing one pipe — would push the
// cost onto plugin authors, since a plugin would then have to handle
// concurrent requests itself. Every sample, and the shape the SDK documents,
// is a strictly serial `while line in stdin: handle; print` loop. So the
// concurrency lives here instead: each worker still sees one message at a
// time and the plugin contract is completely unchanged, while the pool lets
// `workers` calls proceed at once.
//
// What an author DOES have to know is that state stops being per-plugin and
// becomes per-worker. A counter, a warm connection or a cache now exists once
// per process, and consecutive calls need not land on the same one. That is
// why the default is 1: today's behaviour, exactly, unless an operator asks
// for more and has thought about it.
internal sealed class SubprocessPluginPool : IDisposable
{
    // Sanity bounds on the config value. `workers` comes from an operator-
    // edited JSON file, and a stray zero either way should not decide how many
    // processes dmart forks.
    public const int MinWorkers = 1;
    public const int MaxWorkers = 32;

    private readonly SubprocessPluginHost[] _workers;
    private readonly ConcurrentBag<SubprocessPluginHost> _idle = new();

    // Counts idle workers, so a caller blocks here rather than spinning on an
    // empty bag. Every successful Wait() is matched by exactly one TryTake.
    private readonly SemaphoreSlim _available;

    // Pools this thread is currently inside an exchange for.
    //
    // Per-thread and per-pool, not a single flag: a callback from plugin A that
    // triggers a hook on plugin B is legitimate and must not be blocked, while
    // A re-entering A must be. Thread-scoped is the right granularity because
    // an exchange runs synchronously on the thread that started it — the same
    // reasoning that makes PluginInvocationContext [ThreadStatic].
    [ThreadStatic]
    private static List<SubprocessPluginPool>? _entered;

    public string Shortname { get; }
    public int Workers => _workers.Length;

    // The first worker's answer to the info frame. All workers get the same
    // frame and are the same executable, so any of them would do; the loader
    // needs one shortname/type/version/routes, not N.
    public string? InfoResponse { get; }

    public SubprocessPluginPool(string executablePath, string shortname, int workers, string handshakeLine)
    {
        Shortname = shortname;
        var n = Math.Clamp(workers, MinWorkers, MaxWorkers);
        if (n != workers)
            Console.Error.WriteLine(
                $"SUBPROCESS_PLUGIN_WORKERS_CLAMPED: {shortname} requested {workers}, using {n} (allowed {MinWorkers}-{MaxWorkers})");

        _workers = new SubprocessPluginHost[n];
        for (var i = 0; i < n; i++)
        {
            // Each worker handshakes as it starts, so all N agree on what the
            // host supports before any traffic is dispatched to them.
            _workers[i] = new SubprocessPluginHost(executablePath, shortname, handshakeLine);
            _idle.Add(_workers[i]);
        }
        _available = new SemaphoreSlim(n, n);
        InfoResponse = _workers[0].HandshakeResponse;

        if (n > 1)
            Console.Error.WriteLine($"SUBPROCESS_PLUGIN_POOL: {shortname} {n} workers");
    }

    // Runs one exchange on whichever worker is free, blocking until one is.
    // `actor` is threaded straight through — see SubprocessPluginHost.
    public string SendAndReceive(string jsonLine, string? actor = null)
    {
        var entered = _entered ??= new List<SubprocessPluginPool>();

        // Checked BEFORE waiting for a worker, and that order is load-bearing.
        // A nested call routed to a second worker would be safe on the wire,
        // but it would deadlock the moment the pool is saturated: the outer
        // exchange is holding a worker and cannot finish until the inner one
        // returns, while the inner one waits for a worker that only the outer
        // can release. Rejecting keeps the guarantee identical at every pool
        // size instead of making it depend on how busy the plugin happens to be.
        if (entered.Contains(this))
        {
            Console.Error.WriteLine(
                $"SUBPROCESS_PLUGIN_REENTRANT: {Shortname} callback re-entered its own plugin; rejecting");
            return "{\"status\":\"error\",\"message\":\"reentrant plugin call rejected\"}";
        }

        _available.Wait();
        SubprocessPluginHost? worker = null;
        try
        {
            // The semaphore counts what is in the bag, so this always succeeds.
            if (!_idle.TryTake(out worker))
                return "{\"status\":\"error\",\"message\":\"no plugin worker available\"}";
            entered.Add(this);
            return worker.SendAndReceive(jsonLine, actor);
        }
        finally
        {
            entered.Remove(this);
            // Returned even after a failed exchange: the host respawns a dead
            // process on its next call, so a worker is never permanently lost.
            if (worker is not null) _idle.Add(worker);
            _available.Release();
        }
    }

    // Graceful shutdown for every worker — see SubprocessPluginHost.Shutdown.
    public void Shutdown()
    {
        foreach (var w in _workers)
        {
            try { w.Shutdown(); } catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        foreach (var w in _workers) w.Dispose();
        _available.Dispose();
    }
}
