using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

internal sealed record SearchOutcome(int Code, string Message, string MatchedDn, IReadOnlyList<LdapEntry> Entries)
{
    public static SearchOutcome Fail(int code, string message, string matchedDn = "")
        => new(code, message, matchedDn, []);
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
// Two mappings carry the freex/dmart directory schema:
//   * authorizedService = the user's dmart groups. Granting a user Matrix or
//     mail is adding them to that group, which is the same act as making them a
//     member of cn=<group>,ou=groups.
//   * mailAlias = payload.body.mail_aliases (an array of addresses), so the
//     user's schema decides whether aliases exist at all.
internal sealed class LdapDirectory(
    UserRepository users, AccessRepository access, IOptions<DmartSettings> settings)
{
    // User rows fetched per keyset page during a scan.
    private const int ScanPage = 500;

    // Filter attributes that resolve through an index instead of a scan.
    private static readonly HashSet<string> IndexedUserAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "uid", "mail", "mobile",
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

    // Returns at most limit + 1 entries; the caller turns the extra one into
    // sizeLimitExceeded. The search itself never throws for a client mistake —
    // those are result codes.
    public async Task<SearchOutcome> SearchAsync(LdapSearchRequest req, LdapPrincipal who, int limit, CancellationToken ct)
    {
        var nb = LdapDn.Normalize(req.BaseDn);
        if (nb is null) return SearchOutcome.Fail(LdapResult.InvalidDnSyntax, "invalid base DN");

        if (nb.Length == 0)
        {
            // The root DSE: readable before binding, as on every LDAP server,
            // because clients read it to discover the naming context.
            if (req.Scope != LdapScope.BaseObject)
                return SearchOutcome.Fail(LdapResult.NoSuchObject, "no entries above the naming context");
            var root = RootDse();
            return new SearchOutcome(LdapResult.Success, "", "",
                req.Filter.Evaluate(root) == Tri.True ? [root] : []);
        }

        if (who.Kind == PrincipalKind.Anonymous)
            return SearchOutcome.Fail(LdapResult.InsufficientAccessRights, "bind before searching");
        if (nb != L.NormBase && !nb.EndsWith("," + L.NormBase, StringComparison.Ordinal))
            return SearchOutcome.Fail(LdapResult.NoSuchObject, "outside the naming context");

        var results = new List<LdapEntry>();
        var collector = new Collector(req.Filter, results, limit);

        if (who.Kind == PrincipalKind.User)
        {
            // A user reads their own entry and nothing else — the directory
            // equivalent of `by self read`.
            var self = await users.GetByShortnameAsync(who.Shortname!, ct);
            if (self is { IsDeleted: false })
            {
                var entry = UserEntry(self);
                if (InScope(LdapDn.Normalize(entry.Dn)!, nb, req.Scope)) collector.Offer(entry);
            }
            return new SearchOutcome(LdapResult.Success, "", "", results);
        }

        // Null when the base exists; otherwise the deepest existing ancestor,
        // which noSuchObject reports as matchedDN.
        var missing = await SearchAsServiceAsync(nb, req, collector, ct);
        if (missing is not null)
            return SearchOutcome.Fail(LdapResult.NoSuchObject, "no such entry", missing);
        if (collector.ScanLimitHit)
            return new SearchOutcome(LdapResult.AdminLimitExceeded,
                $"filter names no indexed attribute (uid, mail, mobile) and the scan passed {settings.Value.LdapMaxScan} rows",
                "", results);
        return new SearchOutcome(LdapResult.Success, "", "", results);
    }

    private async Task<string?> SearchAsServiceAsync(string nb, LdapSearchRequest req, Collector c, CancellationToken ct)
    {
        var scope = req.Scope;
        var sub = scope == LdapScope.WholeSubtree;

        if (nb == L.NormBase)
        {
            if (scope != LdapScope.SingleLevel) c.Offer(BaseEntry());
            if (scope == LdapScope.BaseObject) return null;
            c.Offer(OuEntry(L.People, "people"));
            c.Offer(OuEntry(L.Groups, "groups"));
            c.Offer(OuEntry(L.Services, "services"));
            if (!sub) return null;
            await OfferUsersAsync(req.Filter, c, ct);
            await OfferGroupsAsync(req.Filter, c, ct);
            await OfferServicesAsync(c, ct);
            return null;
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
                if (scope != LdapScope.SingleLevel) c.Offer(OuEntry(ouDn, ou));
                if (scope == LdapScope.BaseObject) return null;
                if (ou == "people") await OfferUsersAsync(req.Filter, c, ct);
                else if (ou == "groups") await OfferGroupsAsync(req.Filter, c, ct);
                else await OfferServicesAsync(c, ct);
                return null;
            }

            if (!nb.EndsWith("," + normOu, StringComparison.Ordinal)) continue;

            // One entry under an OU. All of them are leaves, so a one-level
            // search under one is empty, but only once the entry is known to
            // exist — otherwise it is noSuchObject like any missing base.
            if (!LdapDn.TryParse(req.BaseDn, out var avas)) break;
            var first = avas.Where(a => a.Rdn == 0).ToList();
            if (first.Count != 1 || ParentOf(avas) != normOu) return ouDn;
            var entry = await LeafAsync(ou, LdapSchema.Canonical(first[0].Type), first[0].Value, ct);
            if (entry is null) return ouDn;
            if (scope != LdapScope.SingleLevel) c.Offer(entry);
            return null;
        }

        return L.Base;
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

    private async Task OfferUsersAsync(LdapFilter filter, Collector c, CancellationToken ct)
    {
        await foreach (var u in UserCandidatesAsync(filter, c, ct))
        {
            if (L.ServiceAccounts.Contains(u.Shortname)) continue;
            if (c.Offer(UserEntry(u))) return;
        }
    }

    // Anchored filters become indexed lookups; anything else walks the table in
    // keyset pages, bounded by LdapMaxScan.
    private async IAsyncEnumerable<User> UserCandidatesAsync(
        LdapFilter filter, Collector c, [EnumeratorCancellation] CancellationToken ct)
    {
        if (filter.Anchors(IndexedUserAttributes.Contains) is { } anchors)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (attr, value) in anchors)
            {
                var u = attr.ToLowerInvariant() switch
                {
                    "uid" => await FindUserAsync(value, ct),
                    "mail" => await users.GetByEmailAsync(value, ct),
                    "mobile" => await users.GetByMsisdnAsync(value, ct),
                    _ => null,
                };
                if (u is { IsDeleted: false } && seen.Add(u.Shortname)) yield return u;
            }
            yield break;
        }

        string? after = null;
        var scanned = 0;
        var max = settings.Value.LdapMaxScan;
        while (true)
        {
            var page = await users.ListForDirectoryAsync(after, ScanPage, ct);
            foreach (var u in page)
            {
                if (++scanned > max)
                {
                    c.ScanLimitHit = true;
                    yield break;
                }
                yield return u;
            }
            if (page.Count < ScanPage) yield break;
            after = page[^1].Shortname;
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

    private async Task OfferGroupsAsync(LdapFilter filter, Collector c, CancellationToken ct)
    {
        List<Group> groups;
        if (c.Full) return;
        var anchors = filter.Anchors(a => a.Equals("cn", StringComparison.OrdinalIgnoreCase)
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
            if (c.Offer(GroupEntry(g, await users.ListShortnamesInGroupAsync(g.Shortname, ct)))) return;
    }

    private async Task OfferServicesAsync(Collector c, CancellationToken ct)
    {
        if (c.Full) return;
        foreach (var name in L.ServiceAccounts.Order(StringComparer.Ordinal))
            if (await users.GetByShortnameAsync(name, ct) is { IsDeleted: false } u && c.Offer(ServiceEntry(u.Shortname)))
                return;
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
            .Add("mail", u.Email)
            .AddRange("mailAlias", MailAliases(u))
            .Add("mobile", u.Msisdn)
            .Add("isActive", u.IsUsable ? "TRUE" : "FALSE")
            .AddRange("authorizedService", u.Groups)
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

    private static List<string> MailAliases(User u)
    {
        if (u.Payload?.Body is not { ValueKind: JsonValueKind.Object } body) return [];
        if (!body.TryGetProperty("mail_aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array) return [];
        return aliases.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString()!)
            .ToList();
    }

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

    // Applies the filter and the size limit to every entry offered, in order.
    private sealed class Collector(LdapFilter filter, List<LdapEntry> results, int limit)
    {
        // Set when an unanchored scan passed LdapMaxScan; the entries collected
        // so far are still returned, with adminLimitExceeded.
        public bool ScanLimitHit { get; set; }

        public bool Full => results.Count > limit || ScanLimitHit;

        // True once the limit is passed: the caller stops producing.
        public bool Offer(LdapEntry entry)
        {
            if (Full) return true;
            if (filter.Evaluate(entry) == Tri.True) results.Add(entry);
            return Full;
        }
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
