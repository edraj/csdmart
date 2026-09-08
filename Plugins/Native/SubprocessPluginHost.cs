using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dmart.Plugins.Native;

// Manages a plugin subprocess. Communicates via stdin/stdout JSON lines.
// If the process crashes, it's automatically respawned on the next call.
//
// Protocol (one JSON line per message):
//   dmart → plugin stdin:  {"type":"hook","event":{...}}
//   plugin → dmart stdout: {"status":"ok"} or {"status":"error","message":"..."}
//
//   dmart → plugin stdin:  {"type":"request","request":{...}}
//   plugin → dmart stdout: {"status":"success","attributes":{...}}
//
//   dmart → plugin stdin:  {"type":"info","host":{"callbacks":1}}
//   plugin → dmart stdout: {"shortname":"x","type":"hook|api","version":"1.2.3",...}
//
// Before writing its final response, a plugin may interleave any number of
// CALLBACK frames — requests back into dmart (load an entry, run a query, send
// mail). Each is answered on stdin before the exchange continues:
//
//   plugin → dmart stdout: {"type":"callback","id":1,"op":"query","args":{...}}
//   dmart → plugin stdin:  {"type":"callback_result","id":1,"ok":true,"result":{...}}
//
// The host advertises support in the info frame's "host" object so a plugin
// can tell whether callbacks will be answered before it strands itself waiting
// on one. See PluginCallbackDispatcher for the op table.
//
// `version` on the info response is OPTIONAL — when absent the loader
// records "0.0.0" as a sentinel for "no version declared". The version
// literal is expected to live in the plugin's build artifact (Python
// __version__, Go `var Version` set via `-ldflags "-X main.Version=…"`,
// etc.) so it's baked into the binary the operator deploys, mirroring how
// dmart bakes its own version via AssemblyInformationalVersion.
internal sealed class SubprocessPluginHost : IDisposable
{
    private readonly string _executablePath;
    private readonly string _workingDir;
    private readonly object _lock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    // The info frame, replayed to EVERY process this host starts — including
    // the ones it starts to replace a crashed predecessor.
    //
    // A plugin learns what the host supports from this frame and caches the
    // answer (the SDK sample keeps it in a module global). Before this was
    // replayed, a respawn produced a process that had never been told, so a
    // plugin that had been making callbacks silently stopped after its first
    // crash and there was nothing in the logs to say why. Sending it on every
    // spawn costs one round trip per process start and makes every worker's
    // view of the host identical.
    private readonly string? _handshakeLine;

    // What the most recent process answered to the handshake. The loader reads
    // it for the plugin's shortname/type/version/routes instead of issuing its
    // own info exchange. Null when the plugin never answered.
    public string? HandshakeResponse { get; private set; }

    public string Shortname { get; }

    public SubprocessPluginHost(string executablePath, string shortname, string? handshakeLine = null)
    {
        _executablePath = executablePath;
        _workingDir = Path.GetDirectoryName(executablePath) ?? ".";
        Shortname = shortname;
        _handshakeLine = handshakeLine;
        EnsureRunning();
    }

    // Upper bound on ONE LINE read, not on the whole exchange. The read below
    // happens under _lock, which EVERY hook dispatch and plugin API call must
    // take: a plugin that stops writing without exiting would otherwise wedge
    // that lock forever and take the whole host down with it. Generous enough
    // for a slow-but-honest plugin, short enough that a wedged one is a blip.
    //
    // Per-LINE rather than per-exchange because an exchange can now contain a
    // chain of callbacks: a plugin doing ten honest 5s callbacks would blow a
    // 30s exchange budget while never once going unresponsive. The clock
    // restarts on every line, so "wedged" still means "silent for 30s" — what
    // the timeout is actually trying to detect.
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(30);

    // A per-line timeout alone can't bound an exchange: a plugin looping on
    // callbacks answers within 30s every time and never finishes. This caps
    // the loop. 256 is far above what a real hook needs (the sample plugins
    // use 0–3) and far below anything that could hold the lock for long.
    private const int MaxCallbacksPerExchange = 256;

    // Managed thread id of the thread currently inside an exchange, 0 when
    // idle. Guards against reentrancy that `lock` cannot: see SendAndReceive.
    private int _exchangeOwner;

    // `actor` is the user the exchange runs as — the hook's user_shortname or
    // the API request's resolved user. It is threaded through to any callback
    // the plugin makes so a `query` honors that user's permissions by default,
    // exactly as the in-process transport does. Null means "no ambient actor"
    // (the info handshake, or a dispatch with no user), which resolves to the
    // same system-level behavior the C ABI gives when the dispatcher never set
    // one.
    public string SendAndReceive(string jsonLine, string? actor = null)
    {
        // `lock` is REENTRANT on the same thread, so it does not by itself stop
        // a callback from re-entering this method. Without this check a nested
        // call would sail past the lock and write a second request onto a pipe
        // that is midway through an exchange — the plugin would answer them out
        // of order and both exchanges would silently read each other's
        // responses. Rejecting is the honest outcome: one stdio pipe cannot
        // multiplex, and the in-process transport's nesting support (see
        // PluginInvocationContext) has no equivalent here.
        //
        // Nothing reachable today re-enters — no Emit* dispatches plugin hooks,
        // they write through the repositories directly — so this guards the
        // pipe invariant against a future callback that does, rather than a
        // live bug.
        if (Volatile.Read(ref _exchangeOwner) == Environment.CurrentManagedThreadId)
        {
            Console.Error.WriteLine($"SUBPROCESS_PLUGIN_REENTRANT: {Shortname} callback re-entered its own plugin; rejecting");
            return "{\"status\":\"error\",\"message\":\"reentrant plugin call rejected\"}";
        }

        lock (_lock)
        {
            // Published for the duration of the exchange so the shared Emit*
            // implementations tag history rows with this plugin's marker and
            // route its logs to `plugin.<shortname>`. Set from the values we
            // were handed rather than inherited ambiently — the subprocess
            // dispatch may run on any pool thread. Restored in the finally so a
            // nested dispatcher (hook triggered by this call's side effects)
            // sees its own context back.
            var prevActor = PluginInvocationContext.CurrentActor;
            var prevShortname = PluginInvocationContext.CurrentShortname;
            PluginInvocationContext.CurrentActor = actor;
            PluginInvocationContext.CurrentShortname = Shortname;
            Volatile.Write(ref _exchangeOwner, Environment.CurrentManagedThreadId);
            try
            {
                return Exchange(jsonLine, actor);
            }
            finally
            {
                Volatile.Write(ref _exchangeOwner, 0);
                PluginInvocationContext.CurrentActor = prevActor;
                PluginInvocationContext.CurrentShortname = prevShortname;
            }
        }
    }

    // One request → (callbacks…) → response exchange. Caller holds _lock.
    private string Exchange(string jsonLine, string? actor)
    {
        // Counted across the whole exchange, including retries, because it also
        // decides whether a retry is safe at all (see below).
        var callbacks = 0;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                EnsureRunning();
                _stdin!.WriteLine(jsonLine);
                _stdin.Flush();

                while (true)
                {
                    var line = ReadLineWithTimeout(_stdout!);
                    if (line is null) break; // process died — handled below

                    if (!LooksLikeCallback(line)) return line;

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException)
                    {
                        // Mentions "callback" but isn't parseable JSON, so it
                        // can't be a callback frame. Hand it up as the response
                        // and let the caller report the parse failure with the
                        // context to describe it.
                        return line;
                    }

                    using (doc)
                    {
                        if (!PluginCallbackDispatcher.IsCallback(doc.RootElement)) return line;

                        if (++callbacks > MaxCallbacksPerExchange)
                        {
                            Console.Error.WriteLine(
                                $"SUBPROCESS_PLUGIN_CALLBACK_FLOOD: {Shortname} exceeded {MaxCallbacksPerExchange} callbacks in one exchange; killing");
                            Kill();
                            return "{\"status\":\"error\",\"message\":\"plugin exceeded callback limit\"}";
                        }

                        _stdin!.WriteLine(PluginCallbackDispatcher.Handle(doc.RootElement, actor));
                        _stdin.Flush();
                    }
                }

                // Process died. Retrying re-delivers the request, which is only
                // safe if the plugin can't already have changed anything —
                // i.e. it made no callbacks. Once one has been serviced, a
                // save_entry has already landed and a replay would double it.
                Kill();
                if (callbacks > 0)
                {
                    Console.Error.WriteLine(
                        $"SUBPROCESS_PLUGIN_DIED_MID_EXCHANGE: {Shortname} died after {callbacks} callback(s); not retrying");
                    return "{\"status\":\"error\",\"message\":\"plugin process died mid-exchange\"}";
                }
            }
            catch (TimeoutException)
            {
                // The plugin accepted the request but never answered. Kill
                // it so EnsureRunning respawns a fresh process on the next
                // call, and fail THIS call instead of retrying — a retry
                // would re-deliver a request the plugin may already have
                // acted on.
                Console.Error.WriteLine(
                    $"SUBPROCESS_PLUGIN_TIMEOUT: {Shortname} no line within {LineTimeout.TotalSeconds:0}s (callbacks={callbacks}); killing");
                Kill();
                return "{\"status\":\"error\",\"message\":\"plugin process unresponsive\"}";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SUBPROCESS_PLUGIN_ERROR: {Shortname} attempt={attempt + 1}/2 {ex.GetType().Name}: {ex.Message}");
                Kill();
                if (attempt == 1 || callbacks > 0) throw;
            }
        }
        return "{\"status\":\"error\",\"message\":\"plugin process unresponsive\"}";
    }

    // Cheap pre-filter so the common case — a plain response line — never pays
    // for a JsonDocument.Parse on the hook hot path. Any real callback frame
    // carries "type":"callback", so it must contain this substring however the
    // plugin spaces its JSON; a false positive only costs the parse we would
    // have done anyway.
    private static bool LooksLikeCallback(string line)
        => line.Contains("callback", StringComparison.Ordinal);

    // Bounded ReadLine: StreamReader has no read timeout, so run the blocking
    // read on the thread pool and abandon it after LineTimeout (throws
    // TimeoutException). The abandoned read swallows its own exception — Kill()
    // disposes the stream out from under it — so it can never resurface as an
    // unobserved task fault.
    private static string? ReadLineWithTimeout(StreamReader stdout)
    {
        var read = Task.Run(() =>
        {
            try { return stdout.ReadLine(); }
            catch { return null; }
        });
        return read.WaitAsync(LineTimeout).GetAwaiter().GetResult();
    }

    private void EnsureRunning()
    {
        if (_process is not null && !_process.HasExited) return;

        Kill(); // cleanup any dead process

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start plugin: {_executablePath}");
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = false;
        _stdout = _process.StandardOutput;

        // Drain stderr to console in background (plugin debug output)
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line is not null)
                        Console.Error.WriteLine($"[{Shortname}] {line}");
                }
            }
            catch { /* process exited */ }
        });

        // Status to stderr so stdout stays pure JSONL (matches ASP.NET Core's
        // json-console-formatter output) — keeps `dmart serve | jq` usable.
        Console.Error.WriteLine($"SUBPROCESS_PLUGIN_STARTED: {Shortname} pid={_process.Id}");

        if (_handshakeLine is null) return;

        // Deliberately swallowed rather than thrown. This runs inside the
        // caller's exchange, and a plugin that cannot answer `info` will fail
        // that exchange on its own a moment later with a message about the
        // request the caller actually made — more useful than a handshake
        // stack trace. A null response is itself the signal the loader reads.
        try
        {
            _stdin.WriteLine(_handshakeLine);
            _stdin.Flush();
            HandshakeResponse = ReadLineWithTimeout(_stdout);
            if (HandshakeResponse is null)
                Console.Error.WriteLine($"SUBPROCESS_PLUGIN_HANDSHAKE_EOF: {Shortname} exited during the info exchange");
        }
        catch (TimeoutException)
        {
            HandshakeResponse = null;
            Console.Error.WriteLine(
                $"SUBPROCESS_PLUGIN_HANDSHAKE_TIMEOUT: {Shortname} no info response within {LineTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            HandshakeResponse = null;
            Console.Error.WriteLine($"SUBPROCESS_PLUGIN_HANDSHAKE_ERROR: {Shortname} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Kill()
    {
        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(1000);
            }
            _process?.Dispose();
        }
        catch { /* ignore cleanup errors */ }
        finally
        {
            _process = null;
            _stdin = null;
            _stdout = null;
        }
    }

    // Graceful shutdown: close stdin so the subprocess's `for line in sys.stdin`
    // (or equivalent) returns EOF and exits on its own, give it up to 500ms,
    // then kill if it's still around. Used by IHostApplicationLifetime's
    // ApplicationStopping hook so non-terminal shutdowns (SIGTERM from
    // systemd, docker stop, host.StopAsync()) never have to fall through to
    // Kill().
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_process is null) return;
            try
            {
                _stdin?.Dispose();  // closes the pipe → subprocess sees EOF
                if (_process is { HasExited: false })
                    _process.WaitForExit(500);
            }
            catch { /* ignore — fall through to Kill */ }
            Kill();
        }
    }

    public void Dispose() => Kill();
}
