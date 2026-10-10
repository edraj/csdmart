using System.Text;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmart.Cli;

// `dmart import-ldif <file.ldif> [--apply] [--update]` — moves an LDAP
// directory into dmart (docs/user-directory-fields.md): what `slapcat` prints
// for a directory shaped like matrix-deploy's (people, services and groups
// under one base).
//
//   person (has uid)         -> user. uid -> shortname; displayName or cn ->
//                               display name; mail -> mailbox (or, when
//                               USER_MAIL_DOMAINS does not list its domain,
//                               the contact email); mailAlias -> mail_aliases;
//                               authorizedService -> services; isActive ->
//                               is_active; mobile -> msisdn
//   cn=<x>,ou=services,...   -> bot user <x>, for LDAP_SERVICE_ACCOUNTS
//   groupOfNames / ...       -> group, and its members' `groups`
//
// Passwords keep working where the hash allows: {SSHA}, {SHA}, {SSHA256},
// {SHA256}, {SSHA512}, {SHA512} and {ARGON2} are stored as they are and
// replaced with Argon2id at the owner's first sign-in (LegacyLdapHash). A
// value with no scheme is a clear-text password: it is hashed now, and
// reported. Any other scheme ({CRYPT}, {MD5}, ...) imports the account without
// a password; its owner resets it.
//
// A dry run by default: it prints what it would do. --apply writes. An
// account that already exists is left alone unless --update, which replaces
// its directory fields, activity and password and adds the groups, keeping
// everything else (its uuid, roles, contact email).
public static class ImportLdifCommand
{
    public static async Task<int> RunAsync(string[] args, string? dotenvPath, IDictionary<string, string?> dotenvValues)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("usage: dmart import-ldif <file.ldif> [--apply] [--update]");
            return 1;
        }
        var apply = args.Contains("--apply");
        var update = args.Contains("--update");

        var (settings, db) = CliBootstrap.BuildFactoryOrExit(dotenvPath, dotenvValues);
        var refresher = new AuthzCacheRefresher();
        var users = new UserRepository(db, refresher, new SessionTokenHasher(settings));
        var access = new AccessRepository(db, QueryHelper.DialectFor(db), refresher, users);
        var hasher = new PasswordHasher(Options.Create(settings), NullLogger<PasswordHasher>.Instance);

        var plan = Plan(LdifReader.Read(File.ReadAllText(file)), hasher, settings.ParseUserMailDomains());
        foreach (var note in plan.Notes) Console.WriteLine(note);
        if (!apply)
        {
            Console.WriteLine($"dry run: {plan.Users.Count} account(s), {plan.Groups.Count} group(s). Re-run with --apply to write.");
            return 0;
        }

        var failures = 0;
        foreach (var g in plan.Groups)
        {
            if (await access.GetGroupAsync(g.Name) is not null) continue;
            var now = TimeUtils.Now();
            await access.UpsertGroupAsync(new Group
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = g.Name, SpaceName = settings.ManagementSpace,
                // Owned by the bootstrap admin, which every instance has.
                Subpath = "/groups", OwnerShortname = "dmart", IsActive = true,
                Displayname = new Translation(En: g.Name), CreatedAt = now, UpdatedAt = now,
            });
            Console.WriteLine($"group {g.Name}: created");
        }

        foreach (var u in plan.Users)
        {
            var existing = await users.GetByShortnameAsync(u.Shortname);
            if (existing is not null && !update)
            {
                Console.WriteLine($"{u.Shortname}: exists, left alone (--update replaces its directory fields and password)");
                continue;
            }
            var now = TimeUtils.Now();
            var row = existing is null
                ? new User
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = u.Shortname, SpaceName = settings.ManagementSpace,
                    Subpath = "/users", OwnerShortname = u.Shortname, Type = u.Bot ? UserType.Bot : UserType.Web,
                    Language = Language.En, Roles = new(), CreatedAt = now,
                    IsActive = u.Active, Password = u.Password, Displayname = new Translation(En: u.DisplayName),
                    Msisdn = u.Msisdn, Mailbox = u.Mailbox, MailAliases = u.Aliases, Services = u.Services,
                    // The directory's own address for someone it does not
                    // host mail for: as good as verified.
                    Email = u.Email, IsEmailVerified = u.Email is not null,
                    Groups = u.Groups, UpdatedAt = now,
                }
                : existing with
                {
                    IsActive = u.Active, Password = u.Password ?? existing.Password,
                    Displayname = existing.Displayname ?? new Translation(En: u.DisplayName),
                    Email = existing.Email ?? u.Email,
                    IsEmailVerified = existing.Email is null ? u.Email is not null : existing.IsEmailVerified,
                    Mailbox = u.Mailbox, MailAliases = u.Aliases, Services = u.Services,
                    Groups = existing.Groups.Union(u.Groups, StringComparer.Ordinal).ToList(), UpdatedAt = now,
                };
            try
            {
                await users.UpsertAsync(row);
                Console.WriteLine($"{u.Shortname}: {(existing is null ? "created" : "updated")}");
            }
            catch (System.Data.Common.DbException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                Console.Error.WriteLine($"{u.Shortname}: NOT imported: its mailbox, an alias or its phone number belongs to another account");
                failures++;
            }
        }
        return failures == 0 ? 0 : 2;
    }

    internal sealed record PlannedUser(
        string Shortname, bool Bot, bool Active, string DisplayName, string? Password, string? Msisdn,
        string? Mailbox, List<string> Aliases, List<string> Services, List<string> Groups, string? Email = null);

    internal sealed record PlannedGroup(string Name, List<string> Members);

    internal sealed record ImportPlan(List<PlannedUser> Users, List<PlannedGroup> Groups, List<string> Notes);

    // Pure: entries in, accounts and groups out, with a note for everything
    // that was skipped or needs the operator. Separate from the writing so it
    // can be tested without a database.
    // `mailDomains` is USER_MAIL_DOMAINS: a `mail` outside them is not a
    // mailbox this deployment hosts, so it becomes the contact email. Empty:
    // every `mail` is a mailbox.
    internal static ImportPlan Plan(IReadOnlyList<LdifReader.Entry> entries, PasswordHasher hasher, string[]? mailDomains = null)
    {
        var notes = new List<string>();
        var groups = new List<PlannedGroup>();
        foreach (var e in entries.Where(IsGroup))
        {
            var name = e.First("cn");
            if (name is null || !RequestRegex.IsValidShortname(name)) { notes.Add($"skipped group {e.Dn}: no usable cn"); continue; }
            var members = e.All("member").Concat(e.All("uniqueMember")).Select(RdnValue).OfType<string>()
                .Concat(e.All("memberUid")).Distinct(StringComparer.Ordinal).ToList();
            groups.Add(new PlannedGroup(name, members));
        }

        var users = new List<PlannedUser>();
        var serviceAccounts = new List<string>();
        foreach (var e in entries)
        {
            var isService = IsService(e);
            var shortname = isService ? e.First("cn") : e.First("uid");
            if (shortname is null || IsGroup(e) || (!isService && !IsPerson(e))) continue;
            if (!RequestRegex.IsValidShortname(shortname)) { notes.Add($"skipped {e.Dn}: '{shortname}' is not a valid dmart shortname"); continue; }

            var password = Password(e.First("userPassword"), shortname, hasher, notes);
            var mailbox = DirectoryFields.NormalizeAddress(e.First("mail"));
            string? contact = null;
            if (mailbox is not null && mailDomains is { Length: > 0 }
                && !mailDomains.Contains(DirectoryFields.DomainOf(mailbox), StringComparer.Ordinal))
                (contact, mailbox) = (mailbox, null);
            var aliases = DirectoryFields.NormalizeAddresses(e.All("mailAlias"));
            if (mailbox is not null) aliases.Remove(mailbox);
            else if (aliases.Count > 0 && !isService)
            {
                notes.Add($"{shortname}: dropped {aliases.Count} mail alias(es); aliases need a hosted mailbox to deliver to");
                aliases.Clear();
            }
            var services = DirectoryFields.NormalizeServices(e.All("authorizedService"));
            foreach (var bad in services.Where(s => !DirectoryFields.IsValidService(s)).ToList())
            {
                notes.Add($"{shortname}: dropped service '{bad}', not a valid service name");
                services.Remove(bad);
            }
            var msisdn = e.First("mobile") is { } m ? new string(m.Where(c => char.IsDigit(c) || c == '+').ToArray()) : null;
            var active = !string.Equals(e.First("isActive"), "FALSE", StringComparison.OrdinalIgnoreCase);

            users.Add(new PlannedUser(
                shortname, isService, active,
                e.First("displayName") ?? e.First("cn") ?? shortname,
                password, string.IsNullOrEmpty(msisdn) ? null : msisdn,
                isService ? null : mailbox, isService ? [] : aliases, isService ? [] : services,
                groups.Where(g => g.Members.Contains(shortname, StringComparer.Ordinal)).Select(g => g.Name).ToList(),
                isService ? null : contact));
            if (isService) serviceAccounts.Add(shortname);
        }
        if (serviceAccounts.Count > 0)
            notes.Add($"service accounts: {string.Join(",", serviceAccounts)} -- list them in LDAP_SERVICE_ACCOUNTS");
        if (users.Any(u => !u.Bot && u.Email is null && u.Msisdn is null))
            notes.Add("accounts without a contact email or phone number cannot reset a password themselves: set one for each");
        return new ImportPlan(users, groups, notes);
    }

    private static string? Password(string? value, string who, PasswordHasher hasher, List<string> notes)
    {
        if (string.IsNullOrEmpty(value)) { notes.Add($"{who}: no password in the directory; set one or reset it"); return null; }
        if (LegacyLdapHash.IsLegacy(value)) return value;
        if (value.StartsWith('{'))
        {
            var scheme = value[..(value.IndexOf('}', StringComparison.Ordinal) + 1)];
            notes.Add($"{who}: password hash {scheme} cannot be imported; the account needs a password reset");
            return null;
        }
        notes.Add($"{who}: the directory held this password in CLEAR TEXT; hashed with Argon2id on import. Change it.");
        return hasher.Hash(value);
    }

    private static bool Has(LdifReader.Entry e, params string[] classes)
        => e.All("objectClass").Any(c => classes.Contains(c, StringComparer.OrdinalIgnoreCase));

    private static bool IsPerson(LdifReader.Entry e)
        => Has(e, "person", "inetOrgPerson", "organizationalPerson", "posixAccount", "freexPerson", "freexUser");

    private static bool IsGroup(LdifReader.Entry e) => Has(e, "groupOfNames", "groupOfUniqueNames", "posixGroup");

    private static bool IsService(LdifReader.Entry e)
        => e.Dn.Contains(",ou=services,", StringComparison.OrdinalIgnoreCase) && e.First("cn") is not null && !IsPerson(e);

    // The first RDN's value of a member DN: uid=alice,ou=people,... -> alice.
    private static string? RdnValue(string dn)
    {
        var rdn = dn.Split(',', 2)[0];
        var eq = rdn.IndexOf('=');
        return eq > 0 ? rdn[(eq + 1)..].Trim() : null;
    }
}

// A small reader for LDIF content records (RFC 2849) as slapcat writes them:
// folded lines, base64 values (`attr:: ...`), comments, blank-line separated
// entries. Attribute options (`cn;lang-ar`) read as the base attribute.
// Change records and URL values (`attr:< file://...`) are not supported.
public static class LdifReader
{
    public sealed record Entry(string Dn, Dictionary<string, List<string>> Attributes)
    {
        public string? First(string name) => Attributes.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;
        public IEnumerable<string> All(string name) => Attributes.TryGetValue(name, out var v) ? v : [];
    }

    public static List<Entry> Read(string text)
    {
        var entries = new List<Entry>();
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith(' ') && lines.Count > 0) { lines[^1] += raw[1..]; continue; }   // folded
            lines.Add(raw);
        }

        string? dn = null;
        var attrs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (dn is not null) entries.Add(new Entry(dn, attrs));
            dn = null;
            attrs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var line in lines)
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith('#') || line.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Split(';')[0].Trim();
            string value;
            if (line.Length > colon + 1 && line[colon + 1] == ':')
            {
                try { value = Encoding.UTF8.GetString(Convert.FromBase64String(line[(colon + 2)..].Trim())); }
                catch (FormatException) { continue; }
            }
            else if (line.Length > colon + 1 && line[colon + 1] == '<') continue;
            else value = line[(colon + 1)..].TrimStart(' ');

            if (name.Equals("dn", StringComparison.OrdinalIgnoreCase)) { Flush(); dn = value; continue; }
            if (!attrs.TryGetValue(name, out var list)) attrs[name] = list = new List<string>();
            list.Add(value);
        }
        Flush();
        return entries;
    }
}
