using System.Text.Json;
using Dmart.Models.Json;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// dmart strips empty arrays from every JSON response, so a quiet poll's page
// arrives with neither `users` nor `deleted`. The replica iterates both.
public class DirectoryFeedPageJsonTests
{
    [Fact]
    public void A_Page_Whose_Empty_Lists_Were_Stripped_Reads_Them_As_Null_With_Exact_Times()
    {
        // Pinned as null, not empty: the source-generated reader ignores the
        // initializers, which is why DirectoryFeedPage types them nullable and
        // the replica reads them through `?? []`.
        var page = JsonSerializer.Deserialize("""{"more":false,"server_time":"2026-10-10T08:00:00.1234567"}""",
            DmartJsonContext.Default.DirectoryFeedPage)!;
        page.Users.ShouldBeNull();
        page.Deleted.ShouldBeNull();
        // Cursors round-trip to the tick, or a keyset walk could stall.
        page.ServerTime.ShouldBe(new DateTime(2026, 10, 10, 8, 0, 0).AddTicks(1234567));
    }

    [Fact]
    public void The_Password_Hash_Travels_Beside_The_User_Which_Never_Serializes_It()
    {
        var json = JsonSerializer.Serialize(new DirectoryFeedPage
        {
            Users = [new DirectoryFeedUser(new Dmart.Models.Core.User
            {
                Uuid = "u1", Shortname = "alice", SpaceName = "management", Subpath = "/users", OwnerShortname = "alice",
                Password = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA",
            }, "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA")],
        }, DmartJsonContext.Default.DirectoryFeedPage);
        var doc = JsonDocument.Parse(json).RootElement.GetProperty("users")[0];
        doc.GetProperty("password_hash").GetString().ShouldStartWith("$argon2id$");
        doc.GetProperty("user").TryGetProperty("password", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_Fed_User_Whose_Empty_Lists_Were_Stripped_Keeps_Empty_Lists()
    {
        // The replica upserts these rows as they arrive; null lists would be
        // written as SQL NULL into columns the rest of dmart reads as arrays.
        var page = JsonSerializer.Deserialize("""
            {"users":[{"user":{"uuid":"u1","shortname":"alice","space_name":"management","subpath":"/users",
             "owner_shortname":"alice","is_active":true},"password_hash":"h"}]}
            """, DmartJsonContext.Default.DirectoryFeedPage)!;
        var user = page.Users!.ShouldHaveSingleItem().User;
        user.Roles.ShouldNotBeNull();
        user.Groups.ShouldNotBeNull();
        user.MailAliases.ShouldNotBeNull();
        user.Services.ShouldNotBeNull();
    }
}
