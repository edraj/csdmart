namespace Dmart.Ldap;

// The subschema subentry (RFC 4512 §4.2), published at cn=Subschema and named
// by the root DSE's subschemaSubentry. Nothing in the face checks entries
// against it: entries are built by the face itself. It exists for schema-aware
// clients, mostly GUI browsers, which read it to know how to display and
// compare the attributes they are shown.
//
// It describes exactly what the face serves: the standard definitions from
// RFC 4512, 4519, 4524, 2798, 3045 and 4530 for the attributes and classes it
// uses, and the site schema it emits. freexPerson / freexUser and their
// attributes repeat matrix-deploy's freex.schema verbatim, OIDs included, so
// a client sees the same definitions here as on the slapd it replaces.
// dmartPerson / dmartUser take the next free numbers in that same arc (1.1 is
// the experimental arc; a deployment that needs registered OIDs should not
// rely on these).
internal static class LdapSubschema
{
    public const string Dn = "cn=Subschema";

    private const string DirectoryString = "1.3.6.1.4.1.1466.115.121.1.15";
    private const string Ia5String = "1.3.6.1.4.1.1466.115.121.1.26";
    private const string BooleanSyntax = "1.3.6.1.4.1.1466.115.121.1.7";
    private const string DnSyntax = "1.3.6.1.4.1.1466.115.121.1.12";
    private const string GeneralizedTime = "1.3.6.1.4.1.1466.115.121.1.24";
    private const string TelephoneNumber = "1.3.6.1.4.1.1466.115.121.1.50";
    private const string Oid = "1.3.6.1.4.1.1466.115.121.1.38";
    private const string Integer = "1.3.6.1.4.1.1466.115.121.1.27";
    private const string Uuid = "1.3.6.1.1.16.1";

    private static readonly string[] Syntaxes =
    [
        $"( {DirectoryString} DESC 'Directory String' )",
        $"( {Ia5String} DESC 'IA5 String' )",
        $"( {BooleanSyntax} DESC 'Boolean' )",
        $"( {DnSyntax} DESC 'Distinguished Name' )",
        $"( {GeneralizedTime} DESC 'Generalized Time' )",
        $"( {TelephoneNumber} DESC 'Telephone Number' )",
        $"( {Oid} DESC 'OID' )",
        $"( {Integer} DESC 'INTEGER' )",
        $"( {Uuid} DESC 'UUID' )",
    ];

    private static readonly string[] MatchingRules =
    [
        $"( 2.5.13.0 NAME 'objectIdentifierMatch' SYNTAX {Oid} )",
        $"( 2.5.13.1 NAME 'distinguishedNameMatch' SYNTAX {DnSyntax} )",
        $"( 2.5.13.2 NAME 'caseIgnoreMatch' SYNTAX {DirectoryString} )",
        $"( 2.5.13.3 NAME 'caseIgnoreOrderingMatch' SYNTAX {DirectoryString} )",
        "( 2.5.13.4 NAME 'caseIgnoreSubstringsMatch' SYNTAX 1.3.6.1.4.1.1466.115.121.1.58 )",
        $"( 2.5.13.13 NAME 'booleanMatch' SYNTAX {BooleanSyntax} )",
        $"( 2.5.13.20 NAME 'telephoneNumberMatch' SYNTAX {TelephoneNumber} )",
        "( 2.5.13.21 NAME 'telephoneNumberSubstringsMatch' SYNTAX 1.3.6.1.4.1.1466.115.121.1.58 )",
        $"( 2.5.13.27 NAME 'generalizedTimeMatch' SYNTAX {GeneralizedTime} )",
        $"( 2.5.13.28 NAME 'generalizedTimeOrderingMatch' SYNTAX {GeneralizedTime} )",
        $"( 1.3.6.1.4.1.1466.109.114.1 NAME 'caseExactIA5Match' SYNTAX {Ia5String} )",
        $"( 1.3.6.1.4.1.1466.109.114.2 NAME 'caseIgnoreIA5Match' SYNTAX {Ia5String} )",
        "( 1.3.6.1.4.1.1466.109.114.3 NAME 'caseIgnoreIA5SubstringsMatch' SYNTAX 1.3.6.1.4.1.1466.115.121.1.58 )",
        $"( 1.3.6.1.1.16.2 NAME 'UUIDMatch' SYNTAX {Uuid} )",
    ];

    private static readonly string[] AttributeTypes =
    [
        // RFC 4512: operational attributes and the root DSE.
        $"( 2.5.4.0 NAME 'objectClass' EQUALITY objectIdentifierMatch SYNTAX {Oid} )",
        $"( 2.5.21.9 NAME 'structuralObjectClass' EQUALITY objectIdentifierMatch SYNTAX {Oid} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 2.5.18.1 NAME 'createTimestamp' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX {GeneralizedTime} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 2.5.18.2 NAME 'modifyTimestamp' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX {GeneralizedTime} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 2.5.18.9 NAME 'hasSubordinates' EQUALITY booleanMatch SYNTAX {BooleanSyntax} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 2.5.18.10 NAME 'subschemaSubentry' EQUALITY distinguishedNameMatch SYNTAX {DnSyntax} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 1.3.6.1.4.1.1466.101.120.5 NAME 'namingContexts' SYNTAX {DnSyntax} USAGE dSAOperation )",
        $"( 1.3.6.1.4.1.1466.101.120.13 NAME 'supportedControl' SYNTAX {Oid} USAGE dSAOperation )",
        $"( 1.3.6.1.4.1.1466.101.120.7 NAME 'supportedExtension' SYNTAX {Oid} USAGE dSAOperation )",
        $"( 1.3.6.1.4.1.1466.101.120.15 NAME 'supportedLDAPVersion' SYNTAX {Integer} USAGE dSAOperation )",
        $"( 1.3.6.1.1.4 NAME 'vendorName' EQUALITY caseExactIA5Match SYNTAX {DirectoryString} SINGLE-VALUE NO-USER-MODIFICATION USAGE dSAOperation )",
        $"( 2.5.21.5 NAME 'attributeTypes' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.3 USAGE directoryOperation )",
        $"( 2.5.21.6 NAME 'objectClasses' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.37 USAGE directoryOperation )",
        $"( 2.5.21.4 NAME 'matchingRules' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.30 USAGE directoryOperation )",
        $"( 1.3.6.1.4.1.1466.101.120.16 NAME 'ldapSyntaxes' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.54 USAGE directoryOperation )",
        // RFC 5020 and RFC 4530.
        $"( 1.3.6.1.1.20 NAME 'entryDN' EQUALITY distinguishedNameMatch SYNTAX {DnSyntax} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        $"( 1.3.6.1.1.16.4 NAME 'entryUUID' EQUALITY UUIDMatch SYNTAX {Uuid} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        // RFC 4519.
        $"( 2.5.4.41 NAME 'name' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} )",
        "( 2.5.4.3 NAME ( 'cn' 'commonName' ) SUP name )",
        "( 2.5.4.4 NAME ( 'sn' 'surname' ) SUP name )",
        "( 2.5.4.42 NAME ( 'givenName' 'gn' ) SUP name )",
        "( 2.5.4.10 NAME ( 'o' 'organizationName' ) SUP name )",
        "( 2.5.4.11 NAME ( 'ou' 'organizationalUnitName' ) SUP name )",
        $"( 2.5.4.13 NAME 'description' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} )",
        "( 2.5.4.31 NAME 'member' SUP distinguishedName )",
        $"( 2.5.4.49 NAME 'distinguishedName' EQUALITY distinguishedNameMatch SYNTAX {DnSyntax} )",
        $"( 2.5.4.20 NAME 'telephoneNumber' EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX {TelephoneNumber} )",
        $"( 0.9.2342.19200300.100.1.1 NAME ( 'uid' 'userid' ) EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} )",
        $"( 0.9.2342.19200300.100.1.25 NAME ( 'dc' 'domainComponent' ) EQUALITY caseIgnoreIA5Match SUBSTR caseIgnoreIA5SubstringsMatch SYNTAX {Ia5String} SINGLE-VALUE )",
        // RFC 4524.
        $"( 0.9.2342.19200300.100.1.3 NAME ( 'mail' 'rfc822Mailbox' ) EQUALITY caseIgnoreIA5Match SUBSTR caseIgnoreIA5SubstringsMatch SYNTAX {Ia5String} )",
        $"( 0.9.2342.19200300.100.1.41 NAME ( 'mobile' 'mobileTelephoneNumber' ) EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX {TelephoneNumber} )",
        // RFC 2798.
        $"( 2.16.840.1.113730.3.1.241 NAME 'displayName' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} SINGLE-VALUE )",
        $"( 2.16.840.1.113730.3.1.39 NAME 'preferredLanguage' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} SINGLE-VALUE )",
        // The memberof overlay's attribute, as OpenLDAP defines it.
        $"( 1.2.840.113556.1.2.102 NAME 'memberOf' EQUALITY distinguishedNameMatch SYNTAX {DnSyntax} NO-USER-MODIFICATION USAGE dSAOperation )",
        // matrix-deploy's freex.schema.
        $"( 1.1.2.1.1 NAME 'isActive' DESC 'Account activation status : true = active, false = disabled' EQUALITY booleanMatch SYNTAX {BooleanSyntax} SINGLE-VALUE )",
        "( 1.1.2.1.2 NAME 'mailAlias' DESC 'Email alias, another name by which the user can receive emails to' SUP mail )",
        $"( 1.1.2.1.4 NAME 'authorizedService' DESC 'Allowed service name' EQUALITY caseIgnoreMatch ORDERING caseIgnoreOrderingMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX {DirectoryString} )",
        $"( 1.1.2.1.6 NAME 'changePasswordRequired' EQUALITY booleanMatch SYNTAX {BooleanSyntax} SINGLE-VALUE )",
        $"( 1.1.2.1.7 NAME ( 'dob' 'dateOfBirth' ) DESC 'Date of Birth' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX {GeneralizedTime} SINGLE-VALUE )",
    ];

    private static readonly string[] ObjectClasses =
    [
        "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
        "( 2.5.17.0 NAME 'subentry' SUP top STRUCTURAL MUST ( cn ) )",
        "( 2.5.20.1 NAME 'subschema' AUXILIARY MAY ( ldapSyntaxes $ matchingRules $ attributeTypes $ objectClasses ) )",
        "( 2.5.6.4 NAME 'organization' SUP top STRUCTURAL MUST o MAY description )",
        "( 2.5.6.5 NAME 'organizationalUnit' SUP top STRUCTURAL MUST ou MAY description )",
        "( 1.3.6.1.4.1.1466.344 NAME 'dcObject' SUP top AUXILIARY MUST dc )",
        "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) MAY ( telephoneNumber $ description ) )",
        "( 2.5.6.7 NAME 'organizationalPerson' SUP person STRUCTURAL MAY ( ou ) )",
        "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP organizationalPerson STRUCTURAL "
            + "MAY ( displayName $ givenName $ mail $ mobile $ preferredLanguage $ uid ) )",
        "( 2.5.6.9 NAME 'groupOfNames' SUP top STRUCTURAL MUST ( member $ cn ) MAY ( description ) )",
        "( 2.5.6.11 NAME 'applicationProcess' SUP top STRUCTURAL MUST cn MAY description )",
        "( 1.1.2.2.1 NAME 'freexPerson' DESC 'freex Person' SUP inetOrgPerson STRUCTURAL MUST ( cn ) "
            + "MAY ( sn $ givenName $ telephoneNumber $ mobile $ displayName $ mail $ description $ preferredLanguage $ o $ dateOfBirth ) )",
        "( 1.1.2.2.2 NAME 'freexUser' DESC 'freex User' SUP freexPerson STRUCTURAL "
            + "MAY ( mailAlias $ authorizedService $ isActive $ changePasswordRequired ) )",
        "( 1.1.2.2.4 NAME 'dmartPerson' DESC 'a dmart user, as the LDAP face serves it' SUP top AUXILIARY "
            + "MAY ( uid $ mail $ mobile $ preferredLanguage $ isActive ) )",
        "( 1.1.2.2.5 NAME 'dmartUser' DESC 'a dmart user: hosted mail and the services granted' SUP top AUXILIARY "
            + "MAY ( mailAlias $ authorizedService ) )",
    ];

    // Every attribute type defined above, under each of its names. A filter
    // testing one of these on an entry that lacks it is False; only a name the
    // face does not know at all is Undefined (RFC 4511 §4.5.1.7). Declared
    // after AttributeTypes, which static initialization reads first.
    private static readonly HashSet<string> Known = AttributeTypes.SelectMany(NamesOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownAttribute(string attribute) => Known.Contains(LdapSchema.Canonical(attribute));

    // "( oid NAME 'a' ..." or "( oid NAME ( 'a' 'b' ) ...".
    private static IEnumerable<string> NamesOf(string definition)
    {
        var at = definition.IndexOf(" NAME ", StringComparison.Ordinal);
        if (at < 0) yield break;
        var rest = definition[(at + 6)..];
        var end = rest.StartsWith('(') ? rest.IndexOf(')', StringComparison.Ordinal) : rest.IndexOf('\'', 1);
        var names = rest[..(end + 1)];
        var parts = names.Split('\'');
        for (var i = 1; i < parts.Length; i += 2) yield return parts[i];
    }

    public static LdapEntry Entry() => new LdapEntry(Dn)
        .AddRange("objectClass", ["top", "subentry", "subschema"])
        .Add("cn", "Subschema")
        .AddRange("ldapSyntaxes", Syntaxes)
        .AddRange("matchingRules", MatchingRules)
        .AddRange("attributeTypes", AttributeTypes)
        .AddRange("objectClasses", ObjectClasses);
}
