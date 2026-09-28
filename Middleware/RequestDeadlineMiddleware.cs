using System.Globalization;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

/// <summary>
/// The REQUEST_TIMEOUT deadline of the current request, published as an
/// HttpContext feature so code running under it can tell "the deadline
/// expired" apart from "the client disconnected" — both cancel
/// <see cref="HttpContext.RequestAborted"/>, but only the second one leaves
/// nobody to answer.
/// </summary>
public sealed class RequestDeadline
{
    public const int TimeoutStatusCode = StatusCodes.Status504GatewayTimeout;

    private readonly CancellationToken _deadline;
    private readonly CancellationToken _clientAborted;
    private bool _expired;

    internal RequestDeadline(TimeSpan timeout, CancellationToken deadline, CancellationToken clientAborted)
    {
        Timeout = timeout;
        _deadline = deadline;
        _clientAborted = clientAborted;
    }

    public TimeSpan Timeout { get; }

    // Read from the tokens rather than set by a callback on the deadline token:
    // cancellation runs the handler's own callbacks first (LIFO), so a handler
    // can already be unwinding before any callback registered here would fire.
    // Latched so the access log and the middleware classify a request the same
    // way even if the client also disconnects in between.
    public bool Expired
    {
        get
        {
            if (!_expired && _deadline.IsCancellationRequested && !_clientAborted.IsCancellationRequested)
                _expired = true;
            return _expired;
        }
    }

    public string? Message { get; private set; }
    public List<Dictionary<string, object>>? Info { get; private set; }

    /// <summary>
    /// Replaces the generic timeout message with one from the code that was cut
    /// off, for work that commits as it goes and can say how far it got.
    /// </summary>
    public void Explain(string message, List<Dictionary<string, object>>? info = null)
    {
        Message = message;
        Info = info;
    }

    public string LimitText => Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "-second";

    public static RequestDeadline? Of(HttpContext ctx) => ctx.Features.Get<RequestDeadline>();
}

/// <summary>
/// Runs every request under REQUEST_TIMEOUT (default 35 s) and answers one
/// that runs out of time with a 504 — Python dmart's answer to its own
/// request_timeout.
/// </summary>
/// <remarks>
/// The deadline used to be only a cancelled token. The access-log middleware
/// took any cancellation for a client disconnect and swallowed it, so a client
/// that was still waiting got a 200 with an empty body (or, without LOG_FILE,
/// an empty 500 from the global handler writing through the cancelled token).
/// </remarks>
public sealed class RequestDeadlineMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx, IOptions<DmartSettings> settings, ILogger<RequestDeadlineMiddleware> log)
    {
        // Long-lived connections must NOT get a deadline. Overwriting
        // RequestAborted with a CancelAfter token kills every WebSocket session and
        // every MCP SSE stream RequestTimeout seconds (default 35) after it opens —
        // the 15s keep-alive ticker on those endpoints exists precisely because they
        // are expected to stay open indefinitely. Skip before installing the token.
        if (ctx.WebSockets.IsWebSocketRequest || ctx.Request.Path.StartsWithSegments("/mcp"))
            return next(ctx);

        var seconds = settings.Value.RequestTimeout > 0 ? settings.Value.RequestTimeout : 35;
        return RunAsync(ctx, next, TimeSpan.FromSeconds(seconds), log);
    }

    internal static async Task RunAsync(HttpContext ctx, RequestDelegate next, TimeSpan timeout, ILogger log)
    {
        var clientAborted = ctx.RequestAborted;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(clientAborted);
        var deadline = new RequestDeadline(timeout, cts.Token, clientAborted);
        ctx.Features.Set(deadline);
        ctx.RequestAborted = cts.Token;
        cts.CancelAfter(timeout);

        // Counts what the handler wrote: HasStarted alone misses bytes an outer
        // middleware buffers (JsonStripEmpties holds JSON bodies until the end).
        // Published as a feature so the 404-envelope wrapper further in reuses it
        // instead of stacking a second counter on every write.
        var originalBody = ctx.Response.Body;
        var counter = new BodyByteCounterStream(originalBody);
        ctx.Response.Body = counter;
        ctx.Features.Set(counter);
        var cutOff = false;
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            cutOff = true;
        }
        finally
        {
            ctx.Response.Body = originalBody;
            // Outer middleware runs after this and must not inherit a token from
            // the CancellationTokenSource disposed on the way out.
            ctx.RequestAborted = clientAborted;
        }

        // A handler can also catch the cancellation itself (or finish just as the
        // timer fires) and return normally: its answer is then written through
        // the cancelled token and dropped, which would leave an empty 200.
        if (!cutOff && deadline.Expired && counter.BytesWritten == 0
            && ctx.Response.StatusCode == StatusCodes.Status200OK)
            cutOff = true;
        if (cutOff) await AnswerTimeoutAsync(ctx, deadline, counter.BytesWritten > 0, log);
    }

    private static async Task AnswerTimeoutAsync(HttpContext ctx, RequestDeadline deadline, bool bodyWritten, ILogger log)
    {
        if (ctx.Response.HasStarted || bodyWritten)
        {
            // Appending an error to half a body would produce a response that is
            // neither; a cut connection is the one signal a client can't mistake
            // for a complete (truncated) answer.
            log.LogWarning("request exceeded REQUEST_TIMEOUT ({Seconds}s) after its response began: {Method} {Path}; aborting the connection",
                deadline.Timeout.TotalSeconds, ctx.Request.Method, RequestLoggingMiddleware.SanitizeForLog(ctx.Request.Path.Value));
            ctx.Abort();
            return;
        }

        log.LogWarning("request exceeded REQUEST_TIMEOUT ({Seconds}s): {Method} {Path}; answered {Status}",
            deadline.Timeout.TotalSeconds, ctx.Request.Method, RequestLoggingMiddleware.SanitizeForLog(ctx.Request.Path.Value),
            RequestDeadline.TimeoutStatusCode);

        ctx.Response.StatusCode = RequestDeadline.TimeoutStatusCode;
        ctx.Response.ContentLength = null;
        var body = Dmart.Models.Api.Response.Fail(
            InternalErrorCode.REQUEST_TIMEOUT,
            deadline.Message ?? $"The request took longer than the server's {deadline.LimitText} time limit and was stopped.",
            ErrorTypes.Server,
            deadline.Info);
        await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
    }
}
