using System.Text;
using System.Text.Json;
using Dmart.Middleware;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Middleware;

// REQUEST_TIMEOUT used to end a request by cancelling HttpContext.RequestAborted
// and nothing else. The access-log middleware read that cancellation as "the
// client went away" and swallowed it, so a client that was still waiting got a
// 200 with an empty body — a CSV import cut off half-way looked like a success.
// The deadline must now be answered, and told apart from a real disconnect.
public sealed class RequestDeadlineMiddlewareTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    private sealed class LifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private static (DefaultHttpContext Ctx, LifetimeFeature Lifetime, MemoryStream Body) NewContext(
        CancellationToken clientAborted = default)
    {
        var ctx = new DefaultHttpContext();
        var lifetime = new LifetimeFeature { RequestAborted = clientAborted };
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, lifetime, body);
    }

    private static Response ReadBody(MemoryStream body)
        => JsonSerializer.Deserialize(body.ToArray(), DmartJsonContext.Default.Response)!;

    private static Task WaitForCancellation(HttpContext ctx)
        => Task.Delay(Timeout.Infinite, ctx.RequestAborted);

    [Fact]
    public async Task Expired_Deadline_Is_Answered_With_504_And_Error_Envelope()
    {
        var (ctx, _, body) = NewContext();

        await RequestDeadlineMiddleware.RunAsync(ctx, WaitForCancellation, Short, NullLogger.Instance);

        ctx.Response.StatusCode.ShouldBe(504);
        ctx.Response.ContentType.ShouldStartWith("application/json");
        var resp = ReadBody(body);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Code.ShouldBe(InternalErrorCode.REQUEST_TIMEOUT);
        resp.Error.Type.ShouldBe(ErrorTypes.Server);
        resp.Error.Message.ShouldContain("time limit");
        RequestDeadline.Of(ctx)!.Expired.ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_Can_Explain_What_It_Finished_Before_The_Deadline()
    {
        var (ctx, _, body) = NewContext();

        await RequestDeadlineMiddleware.RunAsync(ctx, async c =>
        {
            try { await WaitForCancellation(c); }
            catch (OperationCanceledException) when (RequestDeadline.Of(c) is { Expired: true } deadline)
            {
                deadline.Explain("stopped at row 3",
                    new List<Dictionary<string, object>> { new() { ["resume_row"] = 3 } });
                throw;
            }
        }, Short, NullLogger.Instance);

        ctx.Response.StatusCode.ShouldBe(504);
        var resp = ReadBody(body);
        resp.Error!.Message.ShouldBe("stopped at row 3");
        resp.Error.Info.ShouldNotBeNull();
        ((JsonElement)resp.Error.Info![0]["resume_row"]).GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Handler_That_Swallows_The_Cancellation_Still_Gets_A_504()
    {
        var (ctx, _, body) = NewContext();

        // An endpoint that catches the cancellation and returns normally: its
        // own answer would be written through the cancelled token and dropped.
        await RequestDeadlineMiddleware.RunAsync(ctx, async c =>
        {
            try { await WaitForCancellation(c); }
            catch (OperationCanceledException) { }
        }, Short, NullLogger.Instance);

        ctx.Response.StatusCode.ShouldBe(504);
        ReadBody(body).Error!.Code.ShouldBe(InternalErrorCode.REQUEST_TIMEOUT);
    }

    [Fact]
    public async Task Client_Disconnect_Is_Not_A_Timeout()
    {
        using var client = new CancellationTokenSource();
        var (ctx, _, body) = NewContext(client.Token);
        client.CancelAfter(Short);

        // Nobody is left to read an answer, so nothing is written and the
        // cancellation keeps propagating exactly as it did before.
        await Should.ThrowAsync<OperationCanceledException>(() =>
            RequestDeadlineMiddleware.RunAsync(ctx, WaitForCancellation, TimeSpan.FromMinutes(5), NullLogger.Instance));

        ctx.Response.StatusCode.ShouldBe(200);
        body.Length.ShouldBe(0);
        RequestDeadline.Of(ctx)!.Expired.ShouldBeFalse();
    }

    [Fact]
    public async Task Deadline_After_The_Body_Started_Aborts_Instead_Of_Appending_An_Error()
    {
        var (ctx, lifetime, body) = NewContext();

        await RequestDeadlineMiddleware.RunAsync(ctx, async c =>
        {
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("partial"));
            await WaitForCancellation(c);
        }, Short, NullLogger.Instance);

        // A 504 appended to half a body would be a corrupt response that parses
        // as neither; cutting the connection is the only honest signal left.
        lifetime.Aborted.ShouldBeTrue();
        Encoding.UTF8.GetString(body.ToArray()).ShouldBe("partial");
        ctx.Response.StatusCode.ShouldBe(200);
        // Which is also what the access log has to record: no 504 went out.
        RequestDeadline.CanStillAnswer(ctx).ShouldBeFalse();
    }

    [Fact]
    public void Deadline_That_Fires_Before_A_Disconnect_Is_Still_A_Timeout()
    {
        using var client = new CancellationTokenSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(client.Token);
        var deadline = new RequestDeadline(Short, cts.Token, client.Token);

        // The order a slow request the user gives up on actually produces: the
        // limit is reached, and only then does the browser go away. Deciding
        // this on the first READ lost it — by then both tokens are cancelled and
        // the request would be filed as a disconnect for ever, which is the
        // empty-200 behaviour this class exists to remove.
        cts.Cancel();
        client.Cancel();

        deadline.Expired.ShouldBeTrue();
    }

    [Fact]
    public void Disconnect_That_Fires_Before_The_Deadline_Is_Not_A_Timeout()
    {
        using var client = new CancellationTokenSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(client.Token);
        var deadline = new RequestDeadline(TimeSpan.FromMinutes(5), cts.Token, client.Token);

        // Cancelling the client token cancels the linked deadline token too, so
        // both are set here as well — the latch has to look at which came first.
        client.Cancel();

        deadline.Expired.ShouldBeFalse();
    }

    [Fact]
    public void A_Response_That_Has_Begun_Can_No_Longer_Be_Answered_With_A_504()
    {
        var ctx = new DefaultHttpContext();
        var counter = new BodyByteCounterStream(new MemoryStream());
        ctx.Features.Set(counter);

        RequestDeadline.CanStillAnswer(ctx).ShouldBeTrue();
        counter.Write([1], 0, 1);
        // The access log asks the same question: recording a clean 504 for this
        // request would claim an answer the client never got — it gets a reset
        // in the middle of the body instead.
        RequestDeadline.CanStillAnswer(ctx).ShouldBeFalse();
    }

    [Fact]
    public async Task Request_That_Finishes_In_Time_Is_Untouched()
    {
        using var client = new CancellationTokenSource();
        var (ctx, lifetime, body) = NewContext(client.Token);

        await RequestDeadlineMiddleware.RunAsync(ctx, async c =>
        {
            c.RequestAborted.ShouldNotBe(client.Token, "the handler must run under the deadline");
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
        }, TimeSpan.FromMinutes(5), NullLogger.Instance);

        ctx.Response.StatusCode.ShouldBe(200);
        Encoding.UTF8.GetString(body.ToArray()).ShouldBe("ok");
        lifetime.Aborted.ShouldBeFalse();
        RequestDeadline.Of(ctx)!.Expired.ShouldBeFalse();
        // Outer middleware (JSON strip, compression, the exception handler)
        // runs after this returns; it must not inherit a disposed deadline token.
        ctx.RequestAborted.ShouldBe(client.Token);
        ctx.Response.Body.ShouldBeSameAs(body);
    }
}
