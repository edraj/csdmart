using System.Diagnostics;
using System.Text;

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
//   dmart → plugin stdin:  {"type":"info"}
//   plugin → dmart stdout: {"shortname":"x","type":"hook|api","version":"1.2.3",...}
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

    public string Shortname { get; }

    public SubprocessPluginHost(string executablePath, string shortname)
    {
        _executablePath = executablePath;
        _workingDir = Path.GetDirectoryName(executablePath) ?? ".";
        Shortname = shortname;
        EnsureRunning();
    }

    // Upper bound on one request→response exchange. The read below happens
    // under _lock, which EVERY hook dispatch and plugin API call must take:
    // a plugin that stops writing without exiting would otherwise wedge that
    // lock forever and take the whole host down with it. Generous enough for
    // a slow-but-honest plugin, short enough that a wedged one is a blip.
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    public string SendAndReceive(string jsonLine)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureRunning();
                    _stdin!.WriteLine(jsonLine);
                    _stdin.Flush();
                    var response = ReadLineWithTimeout(_stdout!);
                    if (response is not null) return response;
                    // null = process died, retry
                    Kill();
                }
                catch (TimeoutException)
                {
                    // The plugin accepted the request but never answered. Kill
                    // it so EnsureRunning respawns a fresh process on the next
                    // call, and fail THIS call instead of retrying — a retry
                    // would re-deliver a request the plugin may already have
                    // acted on.
                    Console.Error.WriteLine(
                        $"SUBPROCESS_PLUGIN_TIMEOUT: {Shortname} no response within {ExchangeTimeout.TotalSeconds:0}s; killing");
                    Kill();
                    return "{\"status\":\"error\",\"message\":\"plugin process unresponsive\"}";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SUBPROCESS_PLUGIN_ERROR: {Shortname} attempt={attempt + 1}/2 {ex.GetType().Name}: {ex.Message}");
                    Kill();
                    if (attempt == 1) throw;
                }
            }
            return "{\"status\":\"error\",\"message\":\"plugin process unresponsive\"}";
        }
    }

    // Bounded ReadLine: StreamReader has no read timeout, so run the blocking
    // read on the thread pool and abandon it after ExchangeTimeout (throws
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
        return read.WaitAsync(ExchangeTimeout).GetAwaiter().GetResult();
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
