using System.Net;
using System.Text;
using System.Text.Json;
using Dmart.Client;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Client;

// Unit tests for the Dmart.Client SDK. The class runs against a mocked
// HttpMessageHandler so the tests never touch the network — they pin the
// wire shape the client sends (URL, method, headers, body) and the
// behavior it expects back (token storage, error envelope surfacing).
public class DmartClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await Responder(request);
        }
    }

    private static (DmartClient client, RecordingHandler handler) Make(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? responder = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;
        var http = new HttpClient(handler);
        var client = new DmartClient("https://dmart.test", http);
        return (client, handler);
    }

    // ----- token lifecycle -----

    [Fact]
    public async Task LoginAsync_Stores_AccessToken_From_Record_Attributes()
    {
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"user","shortname":"dmart","subpath":"users","attributes":{"access_token":"abc-123","type":"web","roles":["super_admin"]}}]}""")));

        var resp = await client.LoginAsync("dmart", "Test1234");
        resp.Status.ShouldBe(Status.Success);
        client.AuthToken.ShouldBe("abc-123");

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/user/login");
        handler.LastBody.ShouldNotBeNull();
        handler.LastBody.ShouldContain("\"shortname\":\"dmart\"");
        handler.LastBody.ShouldContain("\"password\":\"Test1234\"");
    }

    [Fact]
    public async Task Subsequent_Request_Sends_Bearer_Token()
    {
        var (client, handler) = Make();
        client.AuthToken = "xyz-789";

        await client.GetProfileAsync();

        handler.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.ShouldBe("xyz-789");
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/user/profile");
    }

    [Fact]
    public async Task LogoutAsync_Clears_Token_Even_On_Failure()
    {
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"status\":\"failed\",\"error\":{\"type\":\"internal\",\"code\":1,\"message\":\"boom\"}}",
                                          Encoding.UTF8, "application/json") }));
        client.AuthToken = "will-be-cleared";

        await Should.ThrowAsync<DmartException>(() => client.LogoutAsync());
        client.AuthToken.ShouldBeNull();
    }

    // ----- error surfacing -----

    [Fact]
    public async Task Failed_Envelope_Throws_DmartException_With_Error_Details()
    {
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"auth","code":10,"message":"Invalid username or password"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        var ex = await Should.ThrowAsync<DmartException>(
            () => client.LoginAsync("nope", "wrong"));
        ex.StatusCode.ShouldBe(401);
        ex.Error.Type.ShouldBe(ErrorTypes.Auth);
        ex.Error.Code.ShouldBe(InternalErrorCode.INVALID_USERNAME_AND_PASS);
        ex.Error.Message.ShouldBe("Invalid username or password");
    }

    [Fact]
    public async Task Transport_Error_Throws_DmartException_With_ClientError_Type()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("unreachable"));

        var ex = await Should.ThrowAsync<DmartException>(() => client.GetProfileAsync());
        ex.Error.Type.ShouldBe("ClientError");
        ex.Error.Message.ShouldBe("unreachable");
    }

    // ----- URL construction -----

    [Fact]
    public async Task QueryAsync_Managed_Scope_Hits_Managed_Query()
    {
        var (client, handler) = Make();
        await client.QueryAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users", Limit = 5,
        });
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/managed/query");
    }

    [Fact]
    public async Task QueryAsync_Public_Scope_Hits_Public_Query()
    {
        var (client, handler) = Make();
        await client.QueryAsync(new Query
        {
            Type = QueryType.Subpath, SpaceName = "public", Subpath = "/", Limit = 5,
        }, scope: "public");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/public/query");
    }

    [Fact]
    public async Task ProgressTicketAsync_Builds_Expected_Path()
    {
        var (client, handler) = Make();
        await client.ProgressTicketAsync("myspace", "tickets", "t-001", "approve", resolution: "ok");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Put);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/managed/progress-ticket/myspace/tickets/t-001/approve");
        handler.LastBody!.ShouldContain("\"resolution\":\"ok\"");
    }

    [Fact]
    public void GetAttachmentUrl_Is_Pure_String_Construction()
    {
        var (client, handler) = Make();
        var url = client.GetAttachmentUrl("media", "space1", "posts", "parent", "att-1", ".png");
        url.ShouldBe("https://dmart.test/managed/payload/media/space1/posts/parent/att-1.png");
        handler.LastRequest.ShouldBeNull(); // no HTTP fired
    }

    [Fact]
    public async Task CheckExistingAsync_Escapes_Query_Value()
    {
        var (client, handler) = Make();
        await client.CheckExistingAsync("email", "user+test@example.com");
        handler.LastRequest!.RequestUri!.PathAndQuery
            .ShouldBe("/user/check-existing?email=user%2Btest%40example.com");
    }

    // ----- helper -----

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
