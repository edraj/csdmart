using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Models.Api;

namespace Dmart.Utils;

// Subprocess wrapper around the `jq` binary. Mirrors Python dmart's
// backend/data_adapters/sql/adapter.py:1803-1872, which shells out to jq
// when a join sub-query carries a jq_filter expression.
//
// Why a subprocess (and not a managed library): there is no AOT-compatible
// jq engine for .NET today. Shelling out matches Python dmart's behavior
// exactly and keeps parity with the wire contract without a custom
// implementation that would drift in semantics.
//
// Availability: `jq` must be on PATH. RPMs declare `Requires: jq`; the
// container image installs it via apk/dnf.
public static class JqRunner
{
    // Mirrors Python's blocklist (backend/models/api.py:23) — dangerous
    // builtins that can leak server state or read external files.
    //
    // `path(` needs the `\(` so a literal "path(" in a filter triggers the
    // rejection even if it appears inside an expression.
    private static readonly Regex DangerousBuiltins = new(
        @"\benv\b|\$ENV\b|\binput\b|\bdebug\b|\bstderr\b|\bpath\(|\bhalt\b|\bhalt_error\b|\bbuiltins\b|\bmodulemeta\b|\bgetpath\b|\$__loc__",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // jq's MODULE SYSTEM, which the blocklist above does not cover and which is
    // a filesystem read, not a builtin:
    //
    //     import "config" as $c {search:"/some/dir"}; $c
    //         -> [{"db_password":"hunter2"}]      (verified, jq 1.8.2)
    //
    // `search` takes an absolute directory, so this reads any <dir>/<name>.json
    // the server process can open — which in dmart means entry payloads, with
    // no ACL anywhere in the path. `include` is the same read for a `.jq` file.
    //
    // Today neither is reachable: both call sites wrap the caller's filter as
    // `map(<filter>)`, and jq's grammar (Module Imports Exp) only accepts a
    // directive at the TOP of a program, so nothing the caller writes can get
    // in front of the `map(`. That is the wrapper saving us, not this
    // validator — and ValidateFilter is a public method whose contract is "this
    // filter is safe to run", so it should not depend on how its one caller
    // happens to spell things.
    //
    // Anchored at the start of the program (after leading whitespace and
    // #-comments, which jq allows there) because that is the only position the
    // directive is legal in. Matching the bare keyword anywhere would reject
    // honest filters that merely mention it — `.import`, `test("include")`,
    // `{note: "please import this"}` — for no gain.
    private static readonly Regex ModuleDirective = new(
        @"^\s*(?:#[^\n]*\n\s*)*(?:import|include)\s*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const int MaxFilterLength = 1024;

    // Hard ceiling on how much of jq's stdout we buffer. ValidateFilter caps the
    // filter's *length*, not the volume it can produce: `[range(200000000)]` is
    // 18 chars, passes validation, and streams hundreds of MB into the heap well
    // inside JqTimeout — a few concurrent requests like that OOM the process.
    // Overflow is reported as JqError (the filter is at fault), not Timeout.
    public const int MaxOutputBytes = 32 * 1024 * 1024;

    // ---- concurrency budget ------------------------------------------------
    //
    // Every run forks a `jq` and buffers its stdout in memory up to
    // MaxOutputBytes. Unbounded, that is the amplification: an anonymous
    // POST /public/query carrying a jq_filter reaches here (an empty result set
    // is still Status.Success with a non-null records[]), and neither
    // /public/query nor /managed/query is rate limited. N concurrent requests
    // meant N processes and up to N x 32MB of heap, on a server that targets
    // boards with 512MB of RAM.
    //
    // A rate limit would not fix this — it bounds requests per minute, while
    // the thing that hurts is how many are running AT ONCE. This bounds that
    // directly: worst-case buffered jq output is MaxConcurrency x MaxOutputBytes
    // whatever the arrival rate, and callers past the queue timeout get a clean
    // 503 instead of the process getting slower for everyone.
    //
    // Same shape as PasswordHasher's memory budget, for the same reason.
    private const int DefaultMaxConcurrency = 4;
    private const int DefaultQueueTimeoutSeconds = 5;

    private static SemaphoreSlim _slots = new(DefaultMaxConcurrency, DefaultMaxConcurrency);
    private static TimeSpan _queueTimeout = TimeSpan.FromSeconds(DefaultQueueTimeoutSeconds);

    /// <summary>Applies the configured budget. Call ONCE, at startup.</summary>
    /// <remarks>
    /// JqRunner is static (it holds no per-request state and its one resource
    /// is the machine's process table), so the budget is static too. Replacing
    /// the semaphore is only safe before any request is served — Program.cs
    /// calls this while the host is still building. It is not a runtime knob.
    /// </remarks>
    internal static void Configure(int maxConcurrency, int queueTimeoutSeconds)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        _queueTimeout = TimeSpan.FromSeconds(Math.Max(1, queueTimeoutSeconds));
    }

    public enum FailureKind
    {
        None = 0,
        // Filter rejected by validation (length or blocked builtin).
        Invalid,
        // jq binary is not on PATH / failed to start.
        JqMissing,
        // Subprocess did not finish within timeoutSeconds.
        Timeout,
        // jq exited non-zero (syntax error or runtime error in the filter).
        JqError,
        // Every concurrency slot was taken for longer than the queue timeout.
        // Server-side backpressure, not a fault in the filter.
        Busy,
    }

    public readonly record struct Result(FailureKind Failure, JsonElement? Output, string? Stderr);

    public readonly record struct RawResult(FailureKind Failure, byte[]? StdoutBytes, string? Stderr);

    /// <summary>Validate the filter expression against length and blocklist.
    /// Mirrors Python's Pydantic field_validator.</summary>
    public static bool ValidateFilter(string filter, out string? reason)
    {
        if (filter.Length > MaxFilterLength)
        {
            reason = $"jq_filter exceeds {MaxFilterLength} character limit";
            return false;
        }
        if (DangerousBuiltins.IsMatch(filter))
        {
            reason = "jq_filter contains disallowed builtins (env, input, debug, stderr, path)";
            return false;
        }
        if (ModuleDirective.IsMatch(filter))
        {
            reason = "jq_filter may not import or include modules";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Run <c>jq -c &lt;filter&gt;</c> with the given UTF-8 JSON input.
    /// Returns the parsed stdout root element, or a failure kind.</summary>
    public static async Task<Result> RunAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct = default)
    {
        var (failure, stdout, stderr) = await RunCoreAsync(filter, inputJson, timeoutSeconds, ct);
        if (failure != FailureKind.None)
            return new Result(failure, null, stderr);

        var stdoutStr = Encoding.UTF8.GetString(stdout!);
        if (string.IsNullOrWhiteSpace(stdoutStr))
            return new Result(FailureKind.None, null, null);

        // jq -c emits one JSON value per line. The join path wraps with
        // `map( [ <expr> ] )` so the output is a single top-level array —
        // parse that directly. Unvectorized filters emit JSONL; wrap into
        // an array so the caller can enumerate uniformly.
        try
        {
            var trimmed = stdoutStr.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                return new Result(FailureKind.None, JsonDocument.Parse(trimmed).RootElement.Clone(), null);

            var sb = new StringBuilder("[");
            var first = true;
            foreach (var line in stdoutStr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (!first) sb.Append(',');
                sb.Append(t);
                first = false;
            }
            sb.Append(']');
            return new Result(FailureKind.None, JsonDocument.Parse(sb.ToString()).RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return new Result(FailureKind.JqError, null, $"jq produced non-JSON output: {ex.Message}");
        }
    }

    /// <summary>Same as <see cref="RunAsync"/> but returns jq's raw stdout bytes
    /// without parsing. Used by the top-level jq_filter path to write jq output
    /// directly into the response envelope via <c>Utf8JsonWriter.WriteRawValue</c>
    /// — saves a parse+reserialize round-trip.</summary>
    public static async Task<RawResult> RunRawAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct = default)
    {
        var (failure, stdout, stderr) = await RunCoreAsync(filter, inputJson, timeoutSeconds, ct);
        return new RawResult(failure, failure == FailureKind.None ? stdout : null, stderr);
    }

    /// <summary>Translate a <see cref="FailureKind"/> into the client-facing
    /// <see cref="Response"/> that dmart emits for that failure. Centralizes the
    /// mapping used by both the top-level (<see cref="JqEnvelope"/>) and join
    /// (<c>QueryService.ApplyClientJoinsAsync</c>) paths. Callers pass
    /// <c>FailureKind.None</c> at their peril — that's a success state and
    /// throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public static Response ToFailureResponse(FailureKind kind, string? stderr) => kind switch
    {
        FailureKind.Timeout => Response.Fail(InternalErrorCode.JQ_TIMEOUT,
            "jq filter took too long to execute", ErrorTypes.Request),
        FailureKind.JqMissing => Response.Fail(InternalErrorCode.JQ_ERROR,
            "jq binary not available on this dmart deployment", ErrorTypes.Request),
        FailureKind.Invalid => Response.Fail(InternalErrorCode.JQ_ERROR,
            "jq_filter validation failed", ErrorTypes.Request),
        FailureKind.JqError => Response.Fail(InternalErrorCode.JQ_ERROR,
            $"jq filter failed: {(stderr ?? "unknown error").Trim()}", ErrorTypes.Request),
        // Backpressure, not the caller's filter: every slot was busy. Server
        // error type so clients retry rather than "fix" a filter that is fine.
        FailureKind.Busy => Response.Fail(InternalErrorCode.JQ_TIMEOUT,
            "jq is at capacity, retry shortly", ErrorTypes.Internal),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "None is not a failure"),
    };

    // Shared subprocess plumbing for RunAsync / RunRawAsync. Returns raw stdout
    // bytes alongside the failure kind and stderr; the two public entry points
    // differ only in how they post-process the bytes (parse to JsonElement vs
    // hand off as-is).
    private static async Task<(FailureKind Failure, byte[]? Stdout, string? Stderr)> RunCoreAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct)
    {
        // Validation first: a filter we are going to refuse should not wait for
        // a slot, and refusing it costs nothing to run concurrently.
        if (!ValidateFilter(filter, out _))
            return (FailureKind.Invalid, null, null);

        if (!await _slots.WaitAsync(_queueTimeout, ct).ConfigureAwait(false))
            return (FailureKind.Busy, null, "jq is at capacity");
        try
        {
            return await RunProcessAsync(filter, inputJson, timeoutSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    // The actual subprocess run. Split out so the slot acquired above is
    // released by exactly one `finally`, on every path out of here — including
    // the early `return`s for a missing binary.
    private static async Task<(FailureKind Failure, byte[]? Stdout, string? Stderr)> RunProcessAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "jq",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        // `--` first: without it a filter beginning with `-` is parsed as an
        // OPTION rather than the program. jq 1.8 rejects attached values
        // (`--from-file=x`) so the reachable damage there is a usage error, but
        // the jq version is the distro's choice — dmart declares a dependency
        // and takes what the package manager installs — and "which jq is on
        // PATH" is the wrong thing for that to depend on.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(filter);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("jq failed to start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (FailureKind.JqMissing, null, "jq binary not found on PATH");
        }
        catch (Exception ex)
        {
            return (FailureKind.JqMissing, null, ex.Message);
        }

        using (proc)
        {
            // Pipe JSON onto stdin. When jq rejects the filter (bad syntax,
            // blocked builtin) it exits before consuming stdin — our write
            // then hits an IOException ("Pipe is broken"). Expected; swallow
            // and let the caller branch on jq's exit code.
            var stdinTask = Task.Run(async () =>
            {
                try { await proc.StandardInput.BaseStream.WriteAsync(inputJson, ct); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                try { proc.StandardInput.Close(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }, ct);

            using var stdoutMs = new MemoryStream();
            var stdoutTask = CopyStdoutBoundedAsync(proc, stdoutMs, ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Settle the readers before returning: the enclosing `using`
                // disposes stdoutMs the moment we do, and an un-awaited copy
                // would then write into a disposed stream (surfacing as an
                // unobserved task exception). The kill closes jq's pipes so
                // these complete immediately; their results are discarded.
                try { await Task.WhenAll(stdinTask, stdoutTask, stderrTask); } catch { }
                return (FailureKind.Timeout, null, "jq timed out");
            }

            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);

            // Budget check before the exit code: an overflow kills jq, so the
            // exit code would report the kill rather than the real cause.
            if (!stdoutTask.Result)
                return (FailureKind.JqError, null,
                    $"jq filter produced more than the {MaxOutputBytes} byte output limit");

            if (proc.ExitCode != 0)
                return (FailureKind.JqError, null, stderrTask.Result);

            return (FailureKind.None, stdoutMs.ToArray(), null);
        }
    }

    // Copy jq's stdout into `dest`, stopping at MaxOutputBytes. Returns false
    // when the budget is blown — jq is killed at that point so WaitForExitAsync
    // returns straight away instead of blocking until JqTimeout on a full pipe.
    private static async Task<bool> CopyStdoutBoundedAsync(
        Process proc, MemoryStream dest, CancellationToken ct)
    {
        var src = proc.StandardOutput.BaseStream;
        var buffer = new byte[81920];   // same chunk size Stream.CopyToAsync uses
        while (true)
        {
            var read = await src.ReadAsync(buffer, ct);
            if (read == 0) return true;
            if (dest.Length + read > MaxOutputBytes)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
