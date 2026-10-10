using System.Globalization;
using System.Runtime.CompilerServices;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;

namespace Dmart.Ldap;

internal enum PrincipalKind { Anonymous, Service, User }

// Who a connection is bound as. Set by a successful bind, reset by any bind
// attempt (RFC 4513 §5.1: a bind, even a failed one, discards the previous
// authentication state).
internal readonly record struct LdapPrincipal(PrincipalKind Kind, string? Shortname)
{
    public static readonly LdapPrincipal Anonymous = new(PrincipalKind.Anonymous, null);
}

// The dmart users table seen as a directory tree:
//
//   <base>                              dcObject / organization
//   ├── ou=people   uid=<shortname>     every live user except service accounts
//   ├── ou=groups   cn=<group>          groupOfNames, member = the users in it
//   └── ou=services cn=<name>           the configured service accounts
//
// It is a projection, not a store. Every entry is built from a row at search
// time and nothing is written back; changes go through dmart's API and UI.
//
// The freex/dmart directory attributes come from the user's core directory
// fields (docs/user-directory-fields.md): mail = mailbox (else the contact
// email), mailAlias = mail_aliases, authorizedService = services. Groups are
// memberOf and nothing else.
internal sealed class LdapDirectory(
    UserRepository users, AccessRepository access, IOptions<DmartSettings> settings)
{
    // User rows fetched per keyset page during a scan.
    private const int ScanPage = 500;

    // Filter attributes a single indexed lookup answers (UserCandidatesAsync).
    private static readonly HashSet<string> PointIndexed = new(StringComparer.OrdinalIgnoreCase)
    {
        "uid", "mail", "mailAlias", "mobile",
    };

    private Layout? _layout;
    private Layout L => _layout ??= new Layout(settings.Value);

    public string BaseDn => L.Base;

    public string UserDn(string shortname) => $"uid={LdapDn.Escape(shortname)},{L.People}";
    public string ServiceDn(string shortname) => $"cn={LdapDn.Escape(shortname)},{L.Services}";
    public string GroupDn(string shortname) => $"cn={LdapDn.Escape(shortname)},{L.Groups}";

    public string DnOf(LdapPrincipal p) => p.Kind switch
    {
        PrincipalKind.User => UserDn(p.Shortname!),
        PrincipalKind.Service => ServiceDn(p.Shortname!),
        _ => "",
    };

    // Which account a bind DN names. Anything that is not exactly one RDN under
    // ou=people (uid=) or ou=services (cn=, and listed in LdapServiceAccounts)
    // names nobody, and the bind fails like a wrong password.
    public LdapPrincipal ClassifyBindDn(string dn)
    {
        if (!LdapDn.TryParse(dn, out var avas) || avas.Count < 2) return LdapPrincipal.Anonymous;
        var first = avas.Where(a => a.Rdn == 0).ToList();
        if (first.Count != 1) return LdapPrincipal.Anonymous;
        var parent = ParentOf(avas);

        var type = LdapSchema.Canonical(first[0].Type);
        var name = first[0].Value;
        if (parent == L.NormPeople && type.Equals("uid", StringComparison.OrdinalIgnoreCase))
            return new LdapPrincipal(PrincipalKind.User, name);
        if (parent == L.NormServices && type.Equals("cn", StringComparison.OrdinalIgnoreCase)
            && L.ServiceAccounts.Contains(name))
            return new LdapPrincipal(PrincipalKind.Service, name);
        return LdapPrincipal.Anonymous;
    }

    public bool IsServiceAccount(string shortname) => L.ServiceAccounts.Contains(shortname);

    // ----- search -----

    // A search that charges every examined row to a budget, and STREAMS its
    // matches: Results yields entries as they are found, so a paged search
    // reads one page of rows at a time instead of materializing the answer.
    //
    // A null in Results is a checkpoint: the budget ran out. An unpaged caller
    // stops there (adminLimitExceeded); a paged caller ends the page, and on the
    // next page resets the budget and resumes the same enumerator, so a full
    // listing of a multi-million-row table is many bounded requests rather than
    // one unbounded one. Code / Message / MatchedDn are final once Results ends.
    internal sealed class Search
    {
        public ScanBudget Budget { get; } = new();
        public int Code { get; private set; } = LdapResult.Success;
        public string Message { get; private set; } = "";
        public string MatchedDn { get; private set; } = "";
        public IAsyncEnumerable<LdapEntry?> Results { get; set; } = Nothing();

        private static async IAsyncEnumerable<LdapEntry?> Nothing()
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Fail(int code, string message, string matchedDn = "")
            => (Code, Message, MatchedDn) = (code, message, matchedDn);
    }

    internal sealed class ScanBudget
    {
        private int _left;
        public bool Exhausted => _left <= 0;
        public void Reset(int rows) => _left = rows;
        // Charges one examined row; true when that used up the budget.
        public bool Spend() => --_left <= 0;
    }

    public Search Start(LdapSearchRequest req, LdapPrincipal who)
    {
        var search = new Search();
        search.Budget.Reset(settings.Value.LdapMaxScan);
        search.Results = RunAsync(search, req, who);
        return search;
    }

    private async IAsyncEnumerable<LdapEntry?> RunAsync(
        Search s, LdapSearchRequest req, LdapPrincipal who, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var nb = LdapDn.Normalize(req.BaseDn);
        if (nb is null) { s.Fail(LdapResult.InvalidDnSyntax, "invalid base DN"); yield break; }

        if (nb.Length == 0)
        {
            // The root DSE: readable before binding, as on every LDAP server,
            // because clients read it to discover the naming context.
            if (req.Scope != LdapScope.BaseObject)
            {
                s.Fail(LdapResult.NoSuchObject, "no entries above the naming context");
                yield break;
            }
            var root = RootDse();
            if (req.Filter.Evaluate(root) == Tri.True) yield return root;
            yield break;
        }

        if (who.Kind == PrincipalKind.Anonymous)
        {
            s.Fail(LdapResult.InsufficientAccessRights, "bind before searching");
            yield break;
        }
        if (nb != L.NormBase && !nb.EndsWith("," + L.NormBase, StringComparison.Ordinal))
        {
            s.Fail(LdapResult.NoSuchObject, "outside the naming context");
            yield break;
        }

        if (who.Kind == PrincipalKind.User)
        {
            // A user reads their own entry and nothing else — the directory
            // equivalent of `by self read`.
            var self = await users.GetByShortnameAsync(who.Shortname!, ct);
            if (self is { IsDeleted: false })
            {
                var entry = UserEntry(self);
                if (InScope(LdapDn.Normalize(entry.Dn)!, nb, req.Scope) && req.Filter.Evaluate(entry) == Tri.True)
                    yield return entry;
            }
            yield break;
        }

        await foreach (var e in ServiceViewAsync(s, nb, req, ct)) yield return e;
    }

    // Everything a service account may read under `nb`, filtered.
    private async IAsyncEnumerable<LdapEntry?> ServiceViewAsync(
        Search s, string nb, LdapSearchRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var scope = req.Scope;
        var f = req.Filter;
        bool Match(LdapEntry e) => f.Evaluate(e) == Tri.True;

        if (nb == L.NormBase)
        {
            if (scope != LdapScope.SingleLevel && Match(BaseEntry())) yield return BaseEntry();
            if (scope == LdapScope.BaseObject) yield break;
            foreach (var ou in new[] { OuEntry(L.People, "people"), OuEntry(L.Groups, "groups"), OuEntry(L.Services, "services") })
                if (Match(ou)) yield return ou;
            if (scope != LdapScope.WholeSubtree) yield break;
            await foreach (var e in UsersAsync(s, f, ct)) yield return e;
            await foreach (var e in GroupsAsync(s, f, ct)) yield return e;
            await foreach (var e in ServicesAsync(f, ct)) yield return e;
            yield break;
        }

        foreach (var (ouDn, normOu, ou) in new[]
                 {
                     (L.People, L.NormPeople, "people"),
                     (L.Groups, L.NormGroups, "groups"),
                     (L.Services, L.NormServices, "services"),
                 })
        {
            if (nb == normOu)
            {
                var ouEntry = OuEntry(ouDn, ou);
                if (scope != LdapScope.SingleLevel && Match(ouEntry)) yield return ouEntry;
                if (scope == LdapScope.BaseObject) yield break;
                var children = ou switch
                {
                    "people" => UsersAsync(s, f, ct),
                    "groups" => GroupsAsync(s, f, ct),
                    _ => ServicesAsync(f, ct),
                };
                await foreach (var e in children) yield return e;
                yield break;
            }

            if (!nb.EndsWith("," + normOu, StringComparison.Ordinal)) continue;

            // One entry under an OU. All of them are leaves, so a one-level
            // search under one is empty, but only once the entry is known to
            // exist — otherwise it is noSuchObject like any missing base.
            if (!LdapDn.TryParse(req.BaseDn, out var avas)) break;
            var first = avas.Where(a => a.Rdn == 0).ToList();
            var entry = first.Count == 1 && ParentOf(avas) == normOu
                ? await LeafAsync(ou, LdapSchema.Canonical(first[0].Type), first[0].Value, ct)
                : null;
            if (entry is null)
            {
                s.Fail(LdapResult.NoSuchObject, "no such entry", ouDn);
                yield break;
            }
            if (scope != LdapScope.SingleLevel && Match(entry)) yield return entry;
            yield break;
        }

        s.Fail(LdapResult.NoSuchObject, "no such entry", L.Base);
    }

    private async Task<LdapEntry?> LeafAsync(string ou, string type, string name, CancellationToken ct)
    {
        switch (ou)
        {
            case "people" when type.Equals("uid", StringComparison.OrdinalIgnoreCase):
            {
                var u = await FindUserAsync(name, ct);
                return u is null || L.ServiceAccounts.Contains(u.Shortname) ? null : UserEntry(u);
            }
            case "groups" when type.Equals("cn", StringComparison.OrdinalIgnoreCase):
            {
                var g = await access.GetGroupAsync(name, ct);
                return g is null ? null : GroupEntry(g, await users.ListShortnamesInGroupAsync(g.Shortname, ct));
            }
            case "services" when type.Equals("cn", StringComparison.OrdinalIgnoreCase):
            {
                if (!L.ServiceAccounts.Contains(name)) return null;
                var u = await users.GetByShortnameAsync(name, ct);
                return u is { IsDeleted: false } ? ServiceEntry(u.Shortname) : null;
            }
            default:
                return null;
        }
    }

    // ----- candidate sources -----

    private async IAsyncEnumerable<LdapEntry?> UsersAsync(
        Search s, LdapFilter f, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var u in UserCandidatesAsync(s, f, ct))
        {
            if (u is null) { yield return null; continue; }   // budget checkpoint
            if (L.ServiceAccounts.Contains(u.Shortname)) continue;
            var e = UserEntry(u);
            if (f.Evaluate(e) == Tri.True) yield return e;
        }
    }

    // The cheapest source that is guaranteed to contain every match:
    //   1. point lookups, when the filter pins uid / mail / mailAlias / mobile;
    //   2. the user_services range, when it pins authorizedService;
    //   3. otherwise a keyset walk of the whole users table.
    // Sources 2 and 3 charge each row to the budget and yield a null checkpoint
    // when it runs out (see Search).
    private async IAsyncEnumerable<User?> UserCandidatesAsync(
        Search s, LdapFilter f, [EnumeratorCancellation] CancellationToken ct)
    {
        if (f.Anchors(PointIndexed.Contains) is { } points)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (attr, value) in points)
            {
                var u = await LookupAsync(attr, value, ct);
                if (u is { IsDeleted: false } && seen.Add(u.Shortname)) yield return u;
            }
            yield break;
        }

        if (f.Anchors(a => a.Equals("authorizedService", StringComparison.OrdinalIgnoreCase)) is { } grants)
        {
            var services = grants.Select(g => g.Value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            // One service (the usual case) comes out of its index range already
            // unique; a union of several needs de-duplicating.
            var seen = services.Count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var service in services)
            {
                await foreach (var u in KeysetAsync(s, (after, n) => users.ListByServiceAsync(service, after, n, ct)))
                    if (u is null || seen is null || seen.Add(u.Shortname)) yield return u;
            }
            yield break;
        }

        await foreach (var u in KeysetAsync(s, (after, n) => users.ListForDirectoryAsync(after, n, ct)))
            yield return u;
    }

    // Pages through a shortname-ordered source, charging each row to the
    // budget. Holds no connection between pages, so a paged LDAP search can
    // sit between client requests indefinitely.
    private static async IAsyncEnumerable<User?> KeysetAsync(
        Search s, Func<string?, int, Task<List<User>>> fetch)
    {
        string? after = null;
        while (true)
        {
            var page = await fetch(after, ScanPage);
            foreach (var u in page)
            {
                yield return u;
                if (s.Budget.Spend()) yield return null;
            }
            if (page.Count < ScanPage) yield break;
            after = page[^1].Shortname;
        }
    }

    private async Task<User?> LookupAsync(string attr, string value, CancellationToken ct)
    {
        switch (attr.ToLowerInvariant())
        {
            case "uid":
                return await FindUserAsync(value, ct);
            case "mail":
            {
                // LDAP `mail` is the hosted mailbox, or the contact email for a
                // user who has none (UserEntry) — so look it up the same way.
                if (DirectoryFields.NormalizeAddress(value) is not { } address) return null;
                if (await users.GetByAddressAsync(address, "mailbox", ct) is { } owner) return owner;
                var contact = await users.GetByEmailAsync(value, ct);
                return contact is { Mailbox: null } ? contact : null;
            }
            case "mailalias":
                return DirectoryFields.NormalizeAddress(value) is { } alias
                    ? await users.GetByAddressAsync(alias, "alias", ct)
                    : null;
            case "mobile":
                return await users.GetByMsisdnAsync(value, ct);
            default:
                return null;
        }
    }

    // uid is case-insensitive in LDAP; dmart shortnames are stored as written.
    // An exact hit wins, then the lowercase spelling every UI produces.
    private async Task<User?> FindUserAsync(string uid, CancellationToken ct)
    {
        var u = await users.GetByShortnameAsync(uid, ct);
        if (u is null)
        {
            var lower = uid.ToLowerInvariant();
            if (!string.Equals(lower, uid, StringComparison.Ordinal))
                u = await users.GetByShortnameAsync(lower, ct);
        }
        return u is { IsDeleted: false } ? u : null;
    }

    private async IAsyncEnumerable<LdapEntry?> GroupsAsync(
        Search s, LdapFilter f, [EnumeratorCancellation] CancellationToken ct)
    {
        List<Group> groups;
        var anchors = f.Anchors(a => a.Equals("cn", StringComparison.OrdinalIgnoreCase)
            || a.Equals("member", StringComparison.OrdinalIgnoreCase));
        if (anchors is null)
        {
            groups = await access.ListGroupsForDirectoryAsync(ct);
        }
        else
        {
            // `(member=<user dn>)` is how Gitea and Dex ask "which groups is
            // this user in": answer it from the user's own row, not a scan.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (attr, value) in anchors)
            {
                if (attr.Equals("cn", StringComparison.OrdinalIgnoreCase)) { names.Add(value); continue; }
                var p = ClassifyBindDn(value);
                if (p.Kind != PrincipalKind.User) continue;
                if (await FindUserAsync(p.Shortname!, ct) is { } member) names.UnionWith(member.Groups);
            }
            groups = names.Count == 0 ? [] : await access.GetGroupsAsync(names, ct);
        }

        foreach (var g in groups.OrderBy(g => g.Shortname, StringComparer.Ordinal))
        {
            var e = GroupEntry(g, await users.ListShortnamesInGroupAsync(g.Shortname, ct));
            if (f.Evaluate(e) == Tri.True) yield return e;
            if (s.Budget.Spend()) yield return null;
        }
    }

    private async IAsyncEnumerable<LdapEntry?> ServicesAsync(
        LdapFilter f, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var name in L.ServiceAccounts.Order(StringComparer.Ordinal))
        {
            if (await users.GetByShortnameAsync(name, ct) is not { IsDeleted: false } u) continue;
            var e = ServiceEntry(u.Shortname);
            if (f.Evaluate(e) == Tri.True) yield return e;
        }
    }

    // ----- entries -----

    private LdapEntry RootDse() => new LdapEntry("")
        .Add("objectClass", "top")
        .Add("namingContexts", L.Base)
        .Add("supportedLDAPVersion", "3")
        .Add("supportedControl", LdapOid.PagedResults)
        .Add("supportedExtension", LdapOid.WhoAmI)
        .Add("vendorName", "dmart");

    private LdapEntry BaseEntry()
    {
        var entry = new LdapEntry(L.Base).Add("objectClass", "top");
        LdapDn.TryParse(L.Base, out var avas);
        var first = avas[0];
        var type = LdapSchema.Canonical(first.Type);
        if (type.Equals("dc", StringComparison.OrdinalIgnoreCase))
            entry.Add("objectClass", "dcObject").Add("objectClass", "organization").Add("dc", first.Value).Add("o", first.Value);
        else if (type.Equals("o", StringComparison.OrdinalIgnoreCase))
            entry.Add("objectClass", "organization").Add("o", first.Value);
        else
            entry.Add(type, first.Value);
        return entry.Add("hasSubordinates", "TRUE");
    }

    private static LdapEntry OuEntry(string dn, string ou) => new LdapEntry(dn)
        .Add("objectClass", "top").Add("objectClass", "organizationalUnit").Add("ou", ou)
        .Add("hasSubordinates", "TRUE");

    internal LdapEntry UserEntry(User u)
    {
        var name = FirstNonEmpty(u.Displayname?.En, u.Displayname?.Ar, u.Displayname?.Ku) ?? u.Shortname;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var entry = new LdapEntry(UserDn(u.Shortname))
            .AddRange("objectClass", ["top", "person", "organizationalPerson", "inetOrgPerson", "dmartPerson", "dmartUser"])
            .AddRange("objectClass", L.ExtraUserClasses)
            .Add("uid", u.Shortname)
            .Add("cn", name)
            // inetOrgPerson inherits MUST (cn $ sn) from person.
            .Add("sn", words.Length > 0 ? words[^1] : u.Shortname)
            .Add("givenName", words.Length > 1 ? words[0] : null)
            .Add("displayName", name)
            // The hosted mailbox; a user without one is reachable at their
            // contact email, which Dex and Gitea need as the account's email.
            .Add("mail", u.Mailbox ?? u.Email)
            .AddRange("mailAlias", u.MailAliases)
            .Add("mobile", u.Msisdn)
            .Add("isActive", u.IsUsable ? "TRUE" : "FALSE")
            .AddRange("authorizedService", u.Services)
            .Add("preferredLanguage", u.Language.ToString().ToLowerInvariant())
            .Add("description", u.Description?.En)
            .Add("entryUUID", u.Uuid)
            .Add("createTimestamp", GeneralizedTime(u.CreatedAt))
            .Add("modifyTimestamp", GeneralizedTime(u.UpdatedAt))
            .Add("hasSubordinates", "FALSE");
        foreach (var g in u.Groups) entry.Add("memberOf", GroupDn(g));
        return entry;
    }

    private LdapEntry GroupEntry(Group g, IEnumerable<string> members)
    {
        var entry = new LdapEntry(GroupDn(g.Shortname))
            .Add("objectClass", "top").Add("objectClass", "groupOfNames")
            .Add("cn", g.Shortname)
            .Add("description", FirstNonEmpty(g.Displayname?.En, g.Displayname?.Ar, g.Description?.En))
            .Add("isActive", g.IsActive ? "TRUE" : "FALSE")
            .Add("entryUUID", g.Uuid)
            .Add("createTimestamp", GeneralizedTime(g.CreatedAt))
            .Add("modifyTimestamp", GeneralizedTime(g.UpdatedAt))
            .Add("hasSubordinates", "FALSE");
        foreach (var m in members)
            if (!L.ServiceAccounts.Contains(m)) entry.Add("member", UserDn(m));
        return entry;
    }

    private LdapEntry ServiceEntry(string shortname) => new LdapEntry(ServiceDn(shortname))
        .Add("objectClass", "top").Add("objectClass", "applicationProcess")
        .Add("cn", shortname)
        .Add("hasSubordinates", "FALSE");

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string GeneralizedTime(DateTime t)
        => t.ToUniversalTime().ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture);

    // ----- tree helpers -----

    private static bool InScope(string normEntry, string normBase, LdapScope scope) => scope switch
    {
        LdapScope.BaseObject => normEntry == normBase,
        LdapScope.SingleLevel => normEntry.EndsWith("," + normBase, StringComparison.Ordinal)
            && normEntry.IndexOf(',', StringComparison.Ordinal) == normEntry.Length - normBase.Length - 1,
        _ => normEntry == normBase || normEntry.EndsWith("," + normBase, StringComparison.Ordinal),
    };

    // Normalized DN of everything after the first RDN.
    private static string ParentOf(List<LdapDn.Ava> avas)
    {
        var rest = avas.Where(a => a.Rdn > 0)
            .GroupBy(a => a.Rdn)
            .Select(g => string.Join('+', g.Select(a => a.Type + "=" + LdapDn.Escape(a.Value))));
        return LdapDn.Normalize(string.Join(',', rest)) ?? "";
    }

    private sealed class Layout
    {
        public Layout(DmartSettings s)
        {
            Base = s.LdapBaseDn.Trim();
            People = "ou=people," + Base;
            Groups = "ou=groups," + Base;
            Services = "ou=services," + Base;
            NormBase = LdapDn.Normalize(Base) ?? "";
            NormPeople = LdapDn.Normalize(People) ?? "";
            NormGroups = LdapDn.Normalize(Groups) ?? "";
            NormServices = LdapDn.Normalize(Services) ?? "";
            ServiceAccounts = new HashSet<string>(s.ParseLdapServiceAccounts(), StringComparer.Ordinal);
            ExtraUserClasses = s.ParseLdapExtraUserObjectClasses();
        }

        public string Base { get; }
        public string People { get; }
        public string Groups { get; }
        public string Services { get; }
        public string NormBase { get; }
        public string NormPeople { get; }
        public string NormGroups { get; }
        public string NormServices { get; }
        public HashSet<string> ServiceAccounts { get; }
        public string[] ExtraUserClasses { get; }
    }
}
