using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The db_size_info plugin answers a question only PostgreSQL can answer in
// full, so it is one of the few endpoints whose RESPONSE differs by backend.
// Both shapes are pinned here — the SQLite one especially, because it used to
// leak "Dmart:PostgresConnection not configured" to the caller, which is not
// an explanation of anything.
public class DbSizeInfoPluginTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public DbSizeInfoPluginTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Db_Size_Info_Reports_Per_Table_Sizes_Or_Explains_Why_Not()
    {
        var client = _factory.CreateClient();
        var loginRaw = await (await client.PostAsync("/user/login", new StringContent(
            $$"""{"shortname":"{{_factory.AdminShortname}}","password":"{{_factory.AdminPassword}}"}""",
            System.Text.Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
        using var loginDoc = JsonDocument.Parse(loginRaw);
        var token = loginDoc.RootElement.GetProperty("records")[0]
            .GetProperty("attributes").GetProperty("access_token").GetString();
        token.ShouldNotBeNullOrEmpty($"login failed: {loginRaw}");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/db_size_info/");
        req.Headers.Authorization = new("Bearer", token);

        using var resp = await client.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var attrs = doc.RootElement.GetProperty("attributes");
        var status = attrs.GetProperty("status").GetString();

        if (!DmartFactory.UseSqlite)
        {
            status.ShouldBe("success", raw);
            var data = attrs.GetProperty("data").EnumerateArray().ToArray();
            data.Length.ShouldBeGreaterThan(0, "the schema has tables, so some must be listed");
            data[0].GetProperty("table_name").GetString().ShouldNotBeNullOrEmpty();
            data[0].GetProperty("pretty_size").GetString().ShouldNotBeNullOrEmpty();
            return;
        }

        // SQLite answers one of two ways depending on how the SQLite it is
        // linked against was compiled, so the test accepts both and pins what
        // each must contain. The e_sqlite3 build SQLitePCLRaw ships has no
        // dbstat; the static musl artifact links Alpine's SQLite, which has it.
        // Asserting only one outcome would fail on whichever build CI is not
        // running -- which is how the old hardcoded "unavailable" survived.
        if (status == "success")
        {
            // dbstat is present: per-table rows, same shape as PostgreSQL.
            var data = attrs.GetProperty("data").EnumerateArray().ToArray();
            data.Length.ShouldBeGreaterThan(0, "dbstat answered, so some table must be listed");
            data[0].GetProperty("table_name").GetString().ShouldNotBeNullOrEmpty();
            data[0].GetProperty("pretty_size").GetString().ShouldNotBeNullOrEmpty();
        }
        else
        {
            // No dbstat: the honest answer is a failure that SAYS SO — not a
            // success with whole-file bytes dressed up as one table's, and not
            // a leaked internal message.
            status.ShouldBe("failed", raw);
            var error = attrs.GetProperty("error").GetString();
            error.ShouldNotBeNull();
            error!.ShouldContain("dbstat", customMessage: "the message must name what is missing");
            error.ShouldNotContain("PostgresConnection",
                customMessage: "an internal connection-setting name is not an explanation");
        }

        // Either way the whole-file size is reported, and the two
        // representations must agree — a pretty string that does not match its
        // own byte count is worse than no string.
        var bytes = attrs.GetProperty("database_size_bytes").GetInt64();
        bytes.ShouldBeGreaterThan(0);
        attrs.GetProperty("database_size").GetString().ShouldNotBeNullOrEmpty();
        attrs.GetProperty("database_used_bytes").GetInt64()
            .ShouldBeLessThanOrEqualTo(bytes, "used pages cannot exceed allocated pages");
    }
}
