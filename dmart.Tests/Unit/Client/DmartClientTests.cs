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

    [Fact]
    public async Task NotFound_Status_Throws_DmartNotFoundException()
    {
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"db","code":404,"message":"missing"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        var ex = await Should.ThrowAsync<DmartNotFoundException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Update,
                SpaceName = "app",
                Records = new(),
            }));
        ex.StatusCode.ShouldBe(404);
        ex.ShouldBeAssignableTo<DmartException>();
    }

    [Fact]
    public async Task NotAllowed_Code_Throws_DmartPermissionDeniedException()
    {
        // The dmart server returns HTTP 401 with InternalErrorCode.NOT_ALLOWED
        // (=401) for every RBAC denial — mapping must key on the code, not 403.
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"request","code":401,"message":"not allowed to update"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        await Should.ThrowAsync<DmartPermissionDeniedException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Update, SpaceName = "app", Records = new(),
            }));
    }

    [Fact]
    public async Task Generic_BadRequest_Stays_Base_DmartException_Not_Validation()
    {
        // The server routes most failures to HTTP 400; only specific codes are
        // validation, so a generic 400 must NOT become DmartValidationException.
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"request","code":430,"message":"something wrong"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        var ex = await Should.ThrowAsync<DmartException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Create, SpaceName = "app", Records = new(),
            }));
        ex.ShouldBeOfType<DmartException>();  // exactly base, not a subtype
    }

    [Fact]
    public async Task Conflict_Code_Throws_DmartConflictException()
    {
        // SHORTNAME_ALREADY_EXIST (=400) can arrive under HTTP 400 (service-
        // layer catch) or 409 (the 23505 middleware) — the CODE selects the
        // typed subclass, so even a 400 wrapper must map to Conflict.
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"request","code":400,"message":"already exists"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        var ex = await Should.ThrowAsync<DmartConflictException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Create, SpaceName = "app", Records = new(),
            }));
        ex.StatusCode.ShouldBe(400, "the wire status is preserved even when the code drove the type");
    }

    [Fact]
    public async Task InvalidData_Code_Throws_DmartValidationException()
    {
        // INVALID_DATA (=402) arrives under HTTP 400 — code drives the type.
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"request","code":402,"message":"Email format is invalid"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        await Should.ThrowAsync<DmartValidationException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Create, SpaceName = "app", Records = new(),
            }));
    }

    [Fact]
    public async Task Unrecognized_Code_Falls_Back_To_Http_Status()
    {
        // A code the switch doesn't know (proxy / future server) must fall
        // back to the HTTP status: 409 → Conflict.
        var (client, _) = Make(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    """{"status":"failed","error":{"type":"request","code":999,"message":"conflict, unknown code"}}""",
                    Encoding.UTF8, "application/json"),
            }));

        await Should.ThrowAsync<DmartConflictException>(
            () => client.RequestAsync(new Request
            {
                RequestType = RequestType.Update, SpaceName = "app", Records = new(),
            }));
    }

    // ----- IDmartData.GetProfileAsync(actor) -----

    [Fact]
    public async Task GetProfileAsync_Actor_Uses_Profile_Endpoint_And_Maps_User()
    {
        // IDmartData.GetProfileAsync(actor) must be an own-profile read via
        // GET /user/profile — NOT a managed entry read of /users/{actor}.
        // The managed read 401s for ordinary users (their user row is owned
        // by "dmart", not themselves), breaking HTTP↔SQL interchangeability;
        // per the interface caveats the bearer token, not `actor`, identifies
        // the caller on the HTTP backend.
        var (client, handler) = Make(_ => Task.FromResult(Ok(
            """{"status":"success","records":[{"resource_type":"user","shortname":"alice","subpath":"/users","attributes":{"email":"alice@x.yz","type":"web","language":"en","roles":["editor"],"is_email_verified":true}}]}""")));

        var user = await client.GetProfileAsync("alice");

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/user/profile");
        user.ShouldNotBeNull();
        user!.Shortname.ShouldBe("alice");
        user.Email.ShouldBe("alice@x.yz");
        user.Roles.ShouldContain("editor");
        user.IsEmailVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task GetProfileAsync_Actor_Returns_Null_On_Empty_Records()
    {
        var (client, _) = Make(_ => Task.FromResult(Ok("""{"status":"success","records":[]}""")));
        (await client.GetProfileAsync("alice")).ShouldBeNull();
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
