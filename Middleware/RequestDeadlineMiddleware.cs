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
    // Set once — by the cancellation callback or by a reader — and never
    // cleared. Volatile because RequestLoggingMiddleware and
    // RequestDeadlineMiddleware read it from different points in the pipeline.
    private volatile bool _expired;

    internal RequestDeadline(TimeSpan timeout, CancellationToken deadline, CancellationToken clientAborted)
    {
        Timeout = timeout;
        _deadline = deadline;
        _clientAborted = clientAborted;
        // Latch the classification the MOMENT the deadline fires. Deciding it
        // only on the first read loses the race where the client also
        // disconnects before anyone asks: _clientAborted is true by then, the
        // request is filed as a disconnect for ever, and RunAsync's
        // `when (deadline.Expired)` never fires — the original empty-200
        // behaviour this class exists to remove. The registration is released
        // with the linked CancellationTokenSource RunAsync owns.
        deadline.Register(static s => ((RequestDeadline)s!).Latch(), this);
    }

    public TimeSpan Timeout { get; }

    // Read from the tokens too, not only latched by the callback above:
    // cancellation runs callbacks LIFO and this one is registered before the
    // request has run, so every callback a handler adds runs ahead of it — a
    // handler can already be unwinding and asking.
    public bool Expired
    {
        get
        {
            Latch();
            return _expired;
        }
    }

    private void Latch()
    {
        if (!_expired && _deadline.IsCancellationRequested && !_clientAborted.IsCancellationRequested)
            _expired = true;
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

    /// <summary>
    /// Whether a request that ran out of time can still be ANSWERED with the
    /// 504, i.e. nothing of a response has gone out yet. Once it has,
    /// <see cref="RequestDeadlineMiddleware"/> cuts the connection instead —
    /// appending an error to half a body would produce a response that is
    /// neither. The access log asks the same question so it does not record a
    /// clean 504 for a client that actually saw a reset.
    /// </summary>
    public static bool CanStillAnswer(HttpContext ctx)
        => !ctx.Response.HasStarted
           && (ctx.Features.Get<BodyByteCounterStream>()?.BytesWritten ?? 0) == 0;
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

        // No "> 0 ? … : 35" fallback: DmartSettingsValidator fails startup under
        // ValidateOnStart when RequestTimeout is not positive, so the false
        // branch is unreachable — and a third copy of the default is a third
        // place to forget when DmartSettings.RequestTimeout changes.
        return RunAsync(ctx, next, TimeSpan.FromSeconds(settings.Value.RequestTimeout), log);
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
        if (cutOff) await AnswerTimeoutAsync(ctx, deadline, log);
    }

    private static async Task AnswerTimeoutAsync(HttpContext ctx, RequestDeadline deadline, ILogger log)
    {
        // One rule, shared with the access log (RequestDeadline.CanStillAnswer),
        // so the line recorded for this request matches what the client got.
        if (!RequestDeadline.CanStillAnswer(ctx))
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
