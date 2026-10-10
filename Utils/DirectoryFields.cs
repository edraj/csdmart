using System.Text.RegularExpressions;

namespace Dmart.Utils;

// Normalization for the directory fields on users — mailbox, mail_aliases,
// services (docs/user-directory-fields.md). Pure functions, applied on every
// write by UserRepository so that what is stored, what is indexed and what an
// LDAP lookup searches for are the same string.
public static partial class DirectoryFields
{
    // Upper bound on aliases per user. Each one is a row in user_addresses and
    // a uniqueness check on write; nobody needs more, and an unbounded list is
    // an unbounded write.
    public const int MaxAliases = 100;
    public const int MaxServices = 64;

    // Addresses compare case-insensitively everywhere — Postfix, Dovecot, the
    // user_addresses primary key — so they are stored folded. Folded in C#,
    // never by SQL lower(), whose behaviour on non-ASCII text differs between
    // PostgreSQL and SQLite.
    public static string? NormalizeAddress(string? address)
    {
        var a = address?.Trim();
        return string.IsNullOrEmpty(a) ? null : a.ToLowerInvariant();
    }

    public static List<string> NormalizeAddresses(IEnumerable<string>? addresses)
    {
        var result = new List<string>();
        if (addresses is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in addresses)
            if (NormalizeAddress(raw) is { } a && seen.Add(a)) result.Add(a);
        return result;
    }

    public static List<string> NormalizeServices(IEnumerable<string>? services)
    {
        var result = new List<string>();
        if (services is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in services)
        {
            var s = raw?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(s) && seen.Add(s)) result.Add(s);
        }
        return result;
    }

    // A service is a slug: it travels as an LDAP attribute value and in config.
    public static bool IsValidService(string service) => ServicePattern().IsMatch(service);

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServicePattern();

    public static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 ? address[(at + 1)..] : null;
    }
}
