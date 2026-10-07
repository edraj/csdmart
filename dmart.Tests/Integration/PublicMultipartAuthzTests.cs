using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The anonymous multipart upload paths must obey the same AllowedSubmitModels
// allow-list as /public/submit, and every multipart path must apply the same
// identifier validation as /managed/request.
//   - /public/resource_with_payload took space_name from the form with no
//     allow-list at all;
//   - /public/attach/{space}'s `request_record` back-compat branch returned
//     BEFORE the allow-list check;
//   - the multipart handler persisted shortnames/subpaths that the JSON path
//     rejects (e.g. containing '/'), which breaks the parent-split ACL.
// The test host has no AllowedSubmitModels, so a correctly placed gate answers
// NOT_ALLOWED_LOCATION; the old ordering fell through to a CanCreate NOT_ALLOWED
// (or further), which is the observable difference.
public sealed class PublicMultipartAuthzTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicMultipartAuthzTests(DmartFactory factory) => _factory = factory;

    private static MultipartFormDataContent Form(string space, string recordJson)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(space), "space_name" },
        };
        var rec = new ByteArrayContent(Encoding.UTF8.GetBytes(recordJson));
        rec.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        form.Add(rec, "request_record", "record.json");
        var payload = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"v\":1}"));
        payload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        form.Add(payload, "payload_file", "payload.json");
        return form;
    }

    private const string ContentRecord = "{\"resource_type\":\"content\",\"shortname\":\"squat\",\"subpath\":\"/\",\"attributes\":{}}";

    [FactIfPg]
    public async Task Anonymous_Resource_With_Payload_Is_Allow_Listed()
    {
        var client = _factory.CreateClient();
        var space = $"itest_pmp_{Guid.NewGuid():N}"[..20];
        var resp = await client.PostAsync("/public/resource_with_payload", Form(space, ContentRecord));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.NOT_ALLOWED_LOCATION);
    }

    [FactIfPg]
    public async Task Anonymous_Attach_BackCompat_Shape_Is_Allow_Listed_Before_Dispatch()
    {
        var client = _factory.CreateClient();
        var space = $"itest_pmp_{Guid.NewGuid():N}"[..20];
        var resp = await client.PostAsync($"/public/attach/{space}", Form(space, ContentRecord));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.NOT_ALLOWED_LOCATION,
            "the allow-list must run before the request_record branch dispatches");
    }

    [FactIfPg]
    public async Task Managed_Multipart_Rejects_An_Invalid_Shortname()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var space = $"itest_pmp_{Guid.NewGuid():N}"[..20];
        const string bad = "{\"resource_type\":\"content\",\"shortname\":\"bad/name\",\"subpath\":\"/\",\"attributes\":{}}";
        var resp = await admin.Client.PostAsync("/managed/resource_with_payload", Form(space, bad));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA,
            "a '/' in a shortname would make the parent-split ACL resolve a different row than the create gate checked");
    }
}
