using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `query.search` under REAL authorization: a user, a role, a permission, and a
// space of rows they may only partly see.
//
// Two independent gates sit between a search expression and the rows it
// returns, and both have to hold:
//
//   query policies  — PermissionService.BuildUserQueryPoliciesAsync turns the
//                     actor's grants into LIKE patterns matched against each
//                     row's `query_policies` TEXT[]. Applied as its own SQL
//                     clause (PermissionFilter / QueryHelper.AppendAclFilter),
//                     structurally separate from the search expression.
//   filter_fields_values — a per-permission `@field:value` clause MERGED INTO
//                     the caller's own search string, then parsed as one
//                     expression. Being textual, it shares the boolean grammar
//                     with whatever the caller typed.
//
// The load-bearing property throughout: a search expression must only ever
// NARROW what the actor may see. No expression may widen it. Each test drives
// QueryService.ExecuteAsync with a non-privileged actor, so the whole gate
// chain runs exactly as it does for POST /managed/query.
//
// SearchPermissionCompositionTests covers the same composition at unit level
// without a database.
[Collection(AnonymousWorldCollection.Name)]
public class QuerySearchPermissionsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public QuerySearchPermissionsTests(DmartFactory factory) => _factory = factory;

    private sealed record Ctx(
        QueryService Query, EntryRepository Entries, SpaceRepository Spaces,
        UserRepository Users, AccessRepository Access, PasswordHasher Hasher);

    private Ctx Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return new Ctx(
            sp.GetRequiredService<QueryService>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<UserRepository>(),
            sp.GetRequiredService<AccessRepository>(),
            sp.GetRequiredService<PasswordHasher>());
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    // ── Fixture ───────────────────────────────────────────────────────────
    // One space, one subpath, four content rows spanning the axes each
    // permission shape slices on: department (FFV), owner (`own` condition),
    // is_active (`is_active` condition).
    //
    //  shortname   dept    region  owner        is_active
    //  ─────────────────────────────────────────────────────────
    //  e_sales_a   sales   emea    <the user>   true
    //  e_sales_b   sales   apac    dmart        true
    //  e_ops_a     ops     emea    <the user>   true
    //  e_ops_b     ops     apac    dmart        false
    private const string SalesA = "e_sales_a";
    private const string SalesB = "e_sales_b";
    private const string OpsA = "e_ops_a";
    private const string OpsB = "e_ops_b";

    private sealed record Fixture(string Space, string Subpath, string User, string Role, string Perm);

    private async Task<Fixture> SeedAsync(
        Ctx c,
        string? filterFieldsValues = null,
        List<string>? conditions = null,
        List<string>? resourceTypes = null,
        List<string>? actions = null,
        bool permissionActive = true,
        bool grantRoleToUser = true)
    {
        var space = Unique("qsp_space");
        var subpath = "/records";
        var user = Unique("qsp_user");
        var role = Unique("qsp_role");
        var perm = Unique("qsp_perm");
        var now = DateTime.UtcNow;

        await c.Spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = space,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });

        await c.Access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = permissionActive,
            Subpaths = new() { [space] = new() { subpath } },
            ResourceTypes = resourceTypes ?? new() { "content" },
            Actions = actions ?? new() { "view", "query" },
            Conditions = conditions ?? new(),
            FilterFieldsValues = filterFieldsValues,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await c.Access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });

        await c.Users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = user,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = user,
            IsActive = true,
            Password = c.Hasher.Hash("Test1234"),
            Type = UserType.Web,
            Language = Language.En,
            Roles = grantRoleToUser ? new() { role } : new(),
            Groups = new(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        await SeedEntry(c, space, subpath, SalesA, user, true, "sales", "emea");
        await SeedEntry(c, space, subpath, SalesB, "dmart", true, "sales", "apac");
        await SeedEntry(c, space, subpath, OpsA, user, true, "ops", "emea");
        await SeedEntry(c, space, subpath, OpsB, "dmart", false, "ops", "apac");

        await c.Access.InvalidateAllCachesAsync();
        return new Fixture(space, subpath, user, role, perm);
    }

    private static async Task SeedEntry(Ctx c, string space, string subpath, string shortname,
        string owner, bool isActive, string dept, string region)
    {
        await c.Entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
            IsActive = isActive,
            OwnerShortname = owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(
                    $$"""{"dept":"{{dept}}","region":"{{region}}"}""").RootElement.Clone(),
            },
        });
    }

    private static async Task CleanupAsync(Ctx c, Fixture f)
    {
        foreach (var sn in new[] { SalesA, SalesB, OpsA, OpsB })
            try { await c.Entries.DeleteAsync(f.Space, f.Subpath, sn, ResourceType.Content); } catch { }
        try { await c.Users.DeleteAllSessionsAsync(f.User); } catch { }
        try { await c.Users.DeleteAsync(f.User); } catch { }
        try { await c.Access.DeleteRoleAsync(f.Role); } catch { }
        try { await c.Access.DeletePermissionAsync(f.Perm); } catch { }
        try { await c.Spaces.DeleteAsync(f.Space); } catch { }
        await c.Access.InvalidateAllCachesAsync();
    }

    private static async Task<string[]> SearchAs(Ctx c, Fixture f, string? actor, string? search)
    {
        var resp = await c.Query.ExecuteAsync(new Query
        {
            Type = QueryType.Subpath,
            SpaceName = f.Space,
            Subpath = f.Subpath,
            Limit = 100,
            RetrieveJsonPayload = true,
            Search = search,
        }, actor);
        resp.Status.ShouldBe(Status.Success);
        return (resp.Records ?? new List<Record>())
            .Select(r => r.Shortname).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    private static string[] Set(params string[] items)
        => items.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ══════════════════════════════════════════════════════════════════════
    // The grant itself
    // ══════════════════════════════════════════════════════════════════════

    [FactIfPg]
    public async Task Plain_Query_Grant_Sees_Every_Row_And_Search_Narrows_It()
    {
        // Baseline for everything below: with an unconditional grant the
        // actor sees all four rows, and a search expression narrows that set
        // exactly as it does for a privileged actor.
        var c = Resolve();
        var f = await SeedAsync(c);
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, SalesB, OpsA, OpsB));
            (await SearchAs(c, f, f.User, "@payload.body.dept:sales")).ShouldBe(Set(SalesA, SalesB));
            (await SearchAs(c, f, f.User, "@payload.body.region:emea")).ShouldBe(Set(SalesA, OpsA));
            (await SearchAs(c, f, f.User, "-@payload.body.dept:sales")).ShouldBe(Set(OpsA, OpsB));
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task No_Grant_Returns_Nothing_Whatever_The_Search_Says()
    {
        // The user exists and is active but holds no role, so
        // BuildUserQueryPoliciesAsync yields no patterns and the query
        // short-circuits. No expression — however permissive, however
        // malformed — may produce a row.
        var c = Resolve();
        var f = await SeedAsync(c, grantRoleToUser: false);
        try
        {
            foreach (var search in new string?[]
            {
                null, "", "@payload.body.dept:sales", "@shortname:*", "@query_policies:*",
                "-@shortname:nonexistent", "@is_active:true or @is_active:false",
                "@owner_shortname:dmart", "()", "or",
            })
            {
                (await SearchAs(c, f, f.User, search)).ShouldBeEmpty($"search: {search ?? "<null>"}");
            }
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Inactive_Permission_Grants_Nothing()
    {
        var c = Resolve();
        var f = await SeedAsync(c, permissionActive: false);
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@payload.body.dept:sales")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Permission_Without_The_Query_Action_Grants_Nothing()
    {
        // BuildUserQueryPoliciesAsync only consults permissions carrying the
        // "query" action. A view-only grant must not become a listing grant
        // just because the caller supplies a search.
        var c = Resolve();
        var f = await SeedAsync(c, actions: new() { "view" });
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@shortname:e_sales_a")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Resource_Type_Scoped_Grant_Does_Not_Reach_Other_Types()
    {
        // The grant names resource_types ["folder"]; every fixture row is
        // content, so the POLICY contributes no visibility. What remains is
        // the ACL filter's owner disjunct — dmart always lets an actor list
        // rows they own — so the page is exactly the actor's own two rows and
        // never the two owned by dmart. That difference is the assertion: the
        // resource_type segment of the policy pattern really does participate
        // in the match rather than being ignored.
        var c = Resolve();
        var f = await SeedAsync(c, resourceTypes: new() { "folder" });
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, OpsA));
            (await SearchAs(c, f, f.User, "@resource_type:content")).ShouldBe(Set(SalesA, OpsA));
            (await SearchAs(c, f, f.User, "@owner_shortname:dmart")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@shortname:e_sales_b")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Conditions on the grant
    // ══════════════════════════════════════════════════════════════════════

    [FactIfPg]
    public async Task Own_Condition_Confines_Results_To_The_Actors_Own_Rows()
    {
        var c = Resolve();
        var f = await SeedAsync(c, conditions: new() { "own" });
        try
        {
            // Only the two rows owned by the actor are reachable...
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, OpsA));

            // ...and no search can reach the other two, including one that
            // names a foreign row directly or filters on its owner.
            (await SearchAs(c, f, f.User, "@shortname:e_sales_b")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@owner_shortname:dmart")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@payload.body.region:apac")).ShouldBeEmpty();

            // Within the owned set the search still narrows normally.
            (await SearchAs(c, f, f.User, "@payload.body.dept:sales")).ShouldBe(Set(SalesA));
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task IsActive_Condition_Hides_Inactive_Rows_From_Every_Search()
    {
        var c = Resolve();
        var f = await SeedAsync(c, conditions: new() { "is_active" });
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, SalesB, OpsA));

            // e_ops_b is inactive: unreachable by shortname, by an explicit
            // @is_active:false filter, or by negating the active ones.
            (await SearchAs(c, f, f.User, "@shortname:e_ops_b")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@is_active:false")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "-@payload.body.dept:sales")).ShouldBe(Set(OpsA));
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Own_And_IsActive_Conditions_Intersect()
    {
        var c = Resolve();
        var f = await SeedAsync(c, conditions: new() { "own", "is_active" });
        try
        {
            // Owned AND active — both of the actor's rows qualify here.
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, OpsA));
            (await SearchAs(c, f, f.User, "@shortname:e_ops_b")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@shortname:e_sales_b")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    // ══════════════════════════════════════════════════════════════════════
    // filter_fields_values (row-level ACL merged into the search string)
    // ══════════════════════════════════════════════════════════════════════

    [FactIfPg]
    public async Task Ffv_Narrows_The_Page_Even_With_No_Caller_Search()
    {
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA, SalesB));
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Ffv_Intersects_With_The_Callers_Own_Search()
    {
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            // sales ∩ emea
            (await SearchAs(c, f, f.User, "@payload.body.region:emea")).ShouldBe(Set(SalesA));
            // sales ∩ apac
            (await SearchAs(c, f, f.User, "@payload.body.region:apac")).ShouldBe(Set(SalesB));
            // A caller search that contradicts the FFV yields nothing — the
            // FFV is not overridable by naming the row.
            (await SearchAs(c, f, f.User, "@payload.body.dept:ops")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@shortname:e_ops_a")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Ffv_Holds_Across_Paren_Groups_Negation_And_Wildcards()
    {
        // The forms a caller most plausibly reaches for when trying to widen
        // a page. Each is still intersected with the FFV.
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            (await SearchAs(c, f, f.User, "(@payload.body.dept:ops)")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "(@payload.body.region:emea) (@payload.body.region:apac)"))
                .ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@shortname:*")).ShouldBe(Set(SalesA, SalesB));
            (await SearchAs(c, f, f.User, "-@payload.body.region:apac")).ShouldBe(Set(SalesA));

            // Negating the very field the FFV constrains neither empties the
            // page nor widens it. Both tokens land in one leaf run on the same
            // field with opposite signs, and "last sign wins"
            // (docs/query-search.md § Same-field accumulation) — the FFV is
            // appended last, so its positive form survives and the caller's
            // negation is discarded. Restriction intact.
            (await SearchAs(c, f, f.User, "-@payload.body.dept:sales")).ShouldBe(Set(SalesA, SalesB));
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task KnownGap_Alternation_On_The_Ffv_Field_Widens_The_Page()
    {
        // ⚠ CHARACTERIZATION TEST — second, distinct route past the FFV, same
        // root cause as the `or` case (textual merge into one expression;
        // PR_SECURITY_AUDIT.md High). This one needs no boolean keyword at
        // all, only a `|` on the constrained field, which makes it the easier
        // of the two to hit by accident.
        //
        // Mechanism: caller `@dept:sales|ops` parses as one selector with
        // Operation=OR. The appended FFV `@dept:sales` is the SAME field with
        // the SAME sign, so ParseSearchString ACCUMULATES its value into the
        // existing selector rather than AND-ing a second predicate — and the
        // accumulated selector keeps the caller's OR. The emitted predicate is
        // `dept IN (sales, ops, sales)`, so the ops rows the FFV was meant to
        // hide come back.
        //
        // A fix that only parenthesises the caller's search would NOT close
        // this: accumulation happens per leaf run, and `(@dept:sales|ops)
        // @dept:sales` still yields two separate AND-ed selectors only because
        // of the paren — worth verifying explicitly when the fix lands.
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            var leaked = await SearchAs(c, f, f.User, "@payload.body.dept:sales|ops");
            leaked.ShouldBe(Set(SalesA, SalesB, OpsA, OpsB));

            // The grant boundary still holds: an ungranted user gets nothing
            // from the same expression.
            var ungranted = await SeedAsync(c, grantRoleToUser: false);
            try
            {
                (await SearchAs(c, ungranted, ungranted.User, "@payload.body.dept:sales|ops"))
                    .ShouldBeEmpty();
            }
            finally { await CleanupAsync(c, ungranted); }
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Ffv_Combines_With_The_Own_Condition()
    {
        // Two independent narrowings from the same permission — the policy
        // pattern (own) and the FFV (dept) — must both apply.
        var c = Resolve();
        var f = await SeedAsync(c,
            filterFieldsValues: "@payload.body.dept:sales",
            conditions: new() { "own" });
        try
        {
            (await SearchAs(c, f, f.User, null)).ShouldBe(Set(SalesA));
            (await SearchAs(c, f, f.User, "@payload.body.dept:ops")).ShouldBeEmpty();
            (await SearchAs(c, f, f.User, "@owner_shortname:dmart")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task KnownGap_Or_Keyword_In_The_Caller_Search_Escapes_The_Ffv()
    {
        // ⚠ CHARACTERIZATION TEST — pins a KNOWN, UNFIXED weakness (High in
        // PR_SECURITY_AUDIT.md) so the suite flags the day it is fixed.
        //
        // MergeFilterFieldsValues appends the permission clause as bare tokens
        // after the caller's search. AND binds tighter than OR, so a caller
        // `or` splits the expression and the permission clause constrains only
        // the right-hand branch — the left branch is evaluated without it.
        //
        // Scope of the exposure: the query-policy gate is a SEPARATE SQL clause
        // and still holds, so this widens a row-level FIELD restriction inside
        // an already-granted subpath. It does not reach rows the actor has no
        // grant for — the assertion below pins both halves of that.
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            // Honest form: only sales rows.
            (await SearchAs(c, f, f.User, "@payload.body.region:emea")).ShouldBe(Set(SalesA));

            // With an `or`, the ops row leaks past the dept restriction.
            var leaked = await SearchAs(c, f, f.User,
                "@payload.body.dept:ops or @payload.body.dept:sales");
            leaked.ShouldContain(OpsA, "documenting the known FFV bypass");
            leaked.ShouldContain(OpsB);

            // Boundary that DOES hold: the leak stops at the grant. Re-run the
            // same shape against a user whose role was never attached — the
            // query-policy gate returns nothing regardless of the `or`.
            var ungranted = await SeedAsync(c, grantRoleToUser: false);
            try
            {
                (await SearchAs(c, ungranted, ungranted.User,
                    "@payload.body.dept:ops or @payload.body.dept:sales")).ShouldBeEmpty();
            }
            finally { await CleanupAsync(c, ungranted); }
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Repeated_Or_Keyword_Is_Not_Collapsed_Into_An_AND()
    {
        // REGRESSION: DedupeSearchTokens used to drop any repeated whitespace
        // token before parsing. A three-branch union
        // (`A or B or C`) lost its second `or` and silently became
        // `(A or B) AND C` — an intersection. Only SELECTORS are dedupe
        // candidates now; operators are structural.
        var c = Resolve();
        var f = await SeedAsync(c);
        try
        {
            (await SearchAs(c, f, f.User,
                "@shortname:e_sales_a or @shortname:e_ops_a or @shortname:e_ops_b"))
                .ShouldBe(Set(SalesA, OpsA, OpsB));

            // Nested groups repeat `or` too — this returned nothing before.
            (await SearchAs(c, f, f.User,
                "((@payload.body.dept:sales or @payload.body.dept:ops) @payload.body.region:emea) " +
                "or @shortname:e_ops_b"))
                .ShouldBe(Set(SalesA, OpsA, OpsB));

            // A genuinely repeated SELECTOR is still collapsed — idempotent,
            // so the result is unchanged either way.
            (await SearchAs(c, f, f.User, "@payload.body.dept:sales @payload.body.dept:sales"))
                .ShouldBe(Set(SalesA, SalesB));
        }
        finally { await CleanupAsync(c, f); }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Permission-bearing predicates do not grant
    // ══════════════════════════════════════════════════════════════════════

    [FactIfPg]
    public async Task Searching_Permission_Columns_Cannot_Widen_The_Page()
    {
        // @query_policies / @roles / @groups read columns that describe
        // authorization, which makes them the obvious thing to reach for when
        // probing. They are ordinary filters: they can only remove rows from
        // the set the gates already allowed.
        var c = Resolve();
        var f = await SeedAsync(c, conditions: new() { "own" });
        try
        {
            var owned = Set(SalesA, OpsA);

            // A tautological policy filter returns the allowed set, not more.
            (await SearchAs(c, f, f.User, "@query_policies:*")).ShouldBe(owned);
            (await SearchAs(c, f, f.User, $"@query_policies:{f.Space}:records:content:*"))
                .ShouldBe(owned);
            // Naming another user's policy pattern yields nothing.
            (await SearchAs(c, f, f.User, $"@query_policies:{f.Space}:records:content:true:dmart"))
                .ShouldBeEmpty();
            // Negating the policy filter cannot flip the gate open either.
            (await SearchAs(c, f, f.User, "-@query_policies:*")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Anonymous_Actor_Is_Gated_By_Its_Own_Policies()
    {
        // Anonymous resolves through the same BuildUserQueryPoliciesAsync path
        // under the "anonymous" shortname. The fixture grants nothing to it,
        // so a search must not surface the space's rows.
        var c = Resolve();
        var f = await SeedAsync(c);
        try
        {
            (await SearchAs(c, f, PermissionService.AnonymousUser, null)).ShouldBeEmpty();
            (await SearchAs(c, f, PermissionService.AnonymousUser, "@shortname:*")).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }

    [FactIfPg]
    public async Task Over_Length_Search_Fails_Closed_For_A_Permissioned_Actor()
    {
        // The FFV clause is folded into the same string the length cap
        // measures. Above the cap the parser answers FALSE — an empty page —
        // rather than dropping the expression, which would drop the
        // permission clause with it.
        var c = Resolve();
        var f = await SeedAsync(c, filterFieldsValues: "@payload.body.dept:sales");
        try
        {
            var huge = "@payload.body.region:" + new string('e', 64 * 1024 + 1);
            (await SearchAs(c, f, f.User, huge)).ShouldBeEmpty();
        }
        finally { await CleanupAsync(c, f); }
    }
}
