namespace Dmart.Ldap;

// The slice of LDAP schema the directory face has to honour for clients to get
// the answers a real server would give: attribute aliases (a filter on
// `commonName` must match `cn`), which attributes are operational (returned
// only when asked for), and how values compare.
//
// There is deliberately no objectClass schema and no schema checking. The face
// only ever reads entries it builds itself, so the only consumers of schema are
// filters and attribute selection, and these tables cover both.
internal static class LdapSchema
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["commonName"] = "cn",
        ["surname"] = "sn",
        ["gn"] = "givenName",
        ["userid"] = "uid",
        ["rfc822Mailbox"] = "mail",
        ["mobileTelephoneNumber"] = "mobile",
        ["organizationName"] = "o",
        ["organizationalUnitName"] = "ou",
        ["domainComponent"] = "dc",
        ["dateOfBirth"] = "dob",
    };

    // Returned for "+" or when named, never for "*" (RFC 4511 §4.5.1.8).
    private static readonly HashSet<string> Operational = new(StringComparer.OrdinalIgnoreCase)
    {
        "entryUUID", "entryDN", "createTimestamp", "modifyTimestamp",
        "memberOf", "structuralObjectClass", "hasSubordinates", "subschemaSubentry",
    };

    // Values are DNs: compared on the normalized DN, so `member=UID=x,ou=People,...`
    // matches however the client spelled it.
    private static readonly HashSet<string> DnValued = new(StringComparer.OrdinalIgnoreCase)
    {
        "member", "memberOf", "entryDN", "owner", "namingContexts",
    };

    // booleanMatch: TRUE / FALSE, case-insensitively.
    private static readonly HashSet<string> Boolean = new(StringComparer.OrdinalIgnoreCase)
    {
        "isActive", "changePasswordRequired", "hasSubordinates",
    };

    // telephoneNumberMatch ignores spaces and hyphens.
    private static readonly HashSet<string> Telephone = new(StringComparer.OrdinalIgnoreCase)
    {
        "mobile", "telephoneNumber", "homePhone",
    };

    // `cn;lang-ar` → `cn`. Options are not modelled; an option-qualified name
    // reads as the base attribute, which is what a client without language tags
    // would see anyway.
    public static string Canonical(string attribute)
    {
        var semicolon = attribute.IndexOf(';', StringComparison.Ordinal);
        var name = semicolon >= 0 ? attribute[..semicolon] : attribute;
        return Aliases.TryGetValue(name, out var canonical) ? canonical : name;
    }

    public static bool IsOperational(string attribute) => Operational.Contains(Canonical(attribute));

    // The form two values are compared in. Equality, ordering and substring
    // filters all match on this, never on the stored value.
    public static string NormalizeValue(string attribute, string value)
    {
        var name = Canonical(attribute);
        if (DnValued.Contains(name)) return LdapDn.Normalize(value) ?? LdapDn.Fold(value);
        if (Boolean.Contains(name)) return value.Trim().ToUpperInvariant();
        if (Telephone.Contains(name)) return value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return LdapDn.Fold(value);
    }
}

// One entry as the face serves it. Attribute names are stored under their
// canonical spelling and looked up through LdapSchema.Canonical, so every alias
// of a name finds the same values.
internal sealed class LdapEntry(string dn)
{
    private readonly Dictionary<string, List<string>> _attributes = new(StringComparer.OrdinalIgnoreCase);

    public string Dn { get; } = dn;

    public IEnumerable<KeyValuePair<string, List<string>>> Attributes => _attributes;

    public LdapEntry Add(string attribute, string? value)
    {
        if (string.IsNullOrEmpty(value)) return this;
        var name = LdapSchema.Canonical(attribute);
        if (!_attributes.TryGetValue(name, out var values))
            _attributes[name] = values = new List<string>();
        values.Add(value);
        return this;
    }

    public LdapEntry AddRange(string attribute, IEnumerable<string> values)
    {
        foreach (var v in values) Add(attribute, v);
        return this;
    }

    public IReadOnlyList<string>? Get(string attribute)
        => _attributes.TryGetValue(LdapSchema.Canonical(attribute), out var v) ? v : null;
}
