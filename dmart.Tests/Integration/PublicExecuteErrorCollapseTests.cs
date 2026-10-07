using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// V-19: the anonymous /public/excute route must not distinguish "task absent"
// from "task present but not a Query" from "unknown task type" — those were an
// existence/shape oracle that also leaked framework type names. Every
// resolution failure now returns one uniform response.
public sealed class PublicExecuteErrorCollapseTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicExecuteErrorCollapseTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task PublicExecute_Collapses_Resolution_Errors_To_One_Response()
    {
        var client = _factory.CreateClient();
        var space = "management";
        var body = new StringContent(
            $"{{\"shortname\":\"does_not_exist_{System.Guid.NewGuid():N}\"}}",
            Encoding.UTF8, "application/json");
        var body2 = new StringContent(
            $"{{\"shortname\":\"does_not_exist_{System.Guid.NewGuid():N}\"}}",
            Encoding.UTF8, "application/json");

        // (a) task_type != "query" — previously NOT_SUPPORTED_TYPE with the
        // offending type echoed back.
        var badType = await (await client.PostAsync(
            $"/public/excute/report/{space}", body)).Content
            .ReadFromJsonAsync(DmartJsonContext.Default.Response);

        // (b) valid task_type, missing saved query — previously
        // SHORTNAME_DOES_NOT_EXIST naming the task and path.
        var missing = await (await client.PostAsync(
            $"/public/excute/query/{space}", body2)).Content
            .ReadFromJsonAsync(DmartJsonContext.Default.Response);

        badType!.Error.ShouldNotBeNull();
        missing!.Error.ShouldNotBeNull();

        // Identical code AND message: no information distinguishes the two.
        missing.Error!.Code.ShouldBe(badType.Error!.Code);
        missing.Error.Message.ShouldBe(badType.Error.Message);
        // And the message carries no task name, path, or framework type.
        badType.Error.Message.ShouldBe("task not found");
    }
}
