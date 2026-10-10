using System.Formats.Asn1;
using System.Text;
using Dmart.Ldap;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Ldap;

// DN handling and filter evaluation for the LDAP directory face. Both decide
// which entries a client sees, so each test pins a case where a naive
// implementation would hand back the wrong set.
public sealed class LdapDnAndFilterTests
{
    // ----- DNs -----

    [Theory]
    [InlineData("uid=Alice,ou=People,dc=imx,dc=sh", "uid=alice,ou=people,dc=imx,dc=sh")]
    [InlineData(" UID = alice , OU=people , DC=imx,DC=sh ", "uid=alice,ou=people,dc=imx,dc=sh")]
    [InlineData("userid=alice,organizationalUnitName=people,domainComponent=imx,dc=sh", "uid=alice,ou=people,dc=imx,dc=sh")]
    [InlineData("cn=Doe\\, John,ou=people,dc=x", "cn=doe\\, john,ou=people,dc=x")]
    [InlineData("cn=caf\\C3\\A9,dc=x", "cn=café,dc=x")]
    [InlineData("", "")]
    public void Normalize_Folds_Case_Aliases_Spaces_And_Escapes(string dn, string expected)
        => LdapDn.Normalize(dn).ShouldBe(expected);

    [Theory]
    [InlineData("uid=alice")]                 // no separator problem, but valid
    [InlineData("cn=a+sn=b,dc=x")]            // multi-valued RDN parses
    public void Valid_Dns_Parse(string dn) => LdapDn.Normalize(dn).ShouldNotBeNull();

    [Theory]
    [InlineData("not a dn")]
    [InlineData("=alice,dc=x")]
    [InlineData("uid=alice\\")]
    public void Malformed_Dns_Do_Not_Normalize(string dn) => LdapDn.Normalize(dn).ShouldBeNull();

    [Fact]
    public void Escape_Prevents_A_Value_From_Adding_An_Rdn()
    {
        // A shortname is turned into `uid=<it>,ou=people,...`. If its comma
        // survived unescaped, the value would end early and the remainder
        // would parse as a second RDN.
        var dn = $"uid={LdapDn.Escape("evil,ou=services")},ou=people,dc=x";
        LdapDn.TryParse(dn, out var avas).ShouldBeTrue();
        avas[0].Value.ShouldBe("evil,ou=services");
        avas.Count(a => a.Rdn == 0).ShouldBe(1);
        avas.Max(a => a.Rdn).ShouldBe(2);
    }

    [Fact]
    public void Escape_Round_Trips_Leading_And_Trailing_Spaces()
    {
        var dn = $"cn={LdapDn.Escape(" padded ")},dc=x";
        LdapDn.TryParse(dn, out var avas).ShouldBeTrue();
        avas[0].Value.ShouldBe(" padded ");
    }

    // ----- filters -----

    [Fact]
    public void Positive_Equalities_On_An_Attribute_Are_Recognised_With_Their_Values()
    {
        var named = new List<string>();
        Parse(F.And(F.Eq("objectClass", "groupOfNames"), F.Or(F.Eq("member", "uid=a,dc=x"), F.Eq("MEMBER", "uid=b,dc=x"))))
            .TestsOnlyByEquality("member", named).ShouldBeTrue();
        named.ShouldBe(new[] { "uid=a,dc=x", "uid=b,dc=x" });

        // Not mentioned at all: trivially true, nothing named.
        var none = new List<string>();
        Parse(F.Eq("cn", "staff")).TestsOnlyByEquality("member", none).ShouldBeTrue();
        none.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("present")]
    [InlineData("ordering")]
    [InlineData("negated")]
    public void Any_Other_Test_Of_The_Attribute_Needs_Its_Full_Value_Set(string form)
    {
        var filter = form switch
        {
            "present" => F.And(F.Eq("cn", "staff"), F.Present("member")),
            "ordering" => F.Ge("member", "uid=a,dc=x"),
            _ => F.And(F.Eq("cn", "staff"), F.Not(F.Eq("member", "uid=a,dc=x"))),
        };
        Parse(filter).TestsOnlyByEquality("member", new List<string>()).ShouldBeFalse();
    }

    private static LdapEntry Alice() => new LdapEntry("uid=alice,ou=people,dc=x")
        .Add("objectClass", "inetOrgPerson").Add("objectClass", "freexUser")
        .Add("uid", "alice")
        .Add("cn", "Alice  Example")
        .Add("mail", "Alice@Example.org")
        .Add("mobile", "+964 770-123-4567")
        .Add("isActive", "TRUE")
        .Add("authorizedService", "matrix").Add("authorizedService", "mail")
        .Add("memberOf", "cn=mail,ou=groups,dc=x");

    private static LdapFilter Parse(byte[] ber)
        => LdapFilter.Decode(new AsnReader(ber, AsnEncodingRules.BER), messageId: 1);

    // Builds the BER of a filter so the tests exercise the real decoder, not
    // a hand-made AST.
    private static class F
    {
        private static byte[] Wrap(Action<AsnWriter> write)
        {
            var w = new AsnWriter(AsnEncodingRules.BER);
            write(w);
            return w.Encode();
        }

        private static void Ava(AsnWriter w, int tag, string attr, string value)
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, tag, isConstructed: true)))
            {
                w.WriteOctetString(Encoding.UTF8.GetBytes(attr));
                w.WriteOctetString(Encoding.UTF8.GetBytes(value));
            }
        }

        public static byte[] Eq(string a, string v) => Wrap(w => Ava(w, 3, a, v));
        public static byte[] Ge(string a, string v) => Wrap(w => Ava(w, 5, a, v));
        public static byte[] Present(string a)
            => Wrap(w => w.WriteOctetString(Encoding.UTF8.GetBytes(a), new Asn1Tag(TagClass.ContextSpecific, 7)));

        public static byte[] Set(int tag, params byte[][] children) => Wrap(w =>
        {
            using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, tag, isConstructed: true)))
                foreach (var c in children) w.WriteEncodedValue(c);
        });

        public static byte[] And(params byte[][] c) => Set(0, c);
        public static byte[] Or(params byte[][] c) => Set(1, c);

        public static byte[] Not(byte[] child) => Wrap(w =>
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
                w.WriteEncodedValue(child);
        });

        public static byte[] Substr(string a, string? initial, string[] any, string? final) => Wrap(w =>
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
            {
                w.WriteOctetString(Encoding.UTF8.GetBytes(a));
                using (w.PushSequence())
                {
                    if (initial is not null) w.WriteOctetString(Encoding.UTF8.GetBytes(initial), new Asn1Tag(TagClass.ContextSpecific, 0));
                    foreach (var x in any) w.WriteOctetString(Encoding.UTF8.GetBytes(x), new Asn1Tag(TagClass.ContextSpecific, 1));
                    if (final is not null) w.WriteOctetString(Encoding.UTF8.GetBytes(final), new Asn1Tag(TagClass.ContextSpecific, 2));
                }
            }
        });
    }

    [Fact]
    public void The_Dex_Filter_Matches_An_Active_Authorized_User()
    {
        // The exact shape matrix-deploy's Dex connector sends, with uid=%s
        // appended by Dex itself.
        var f = Parse(F.And(
            F.And(F.Eq("objectClass", "freexUser"), F.Eq("isActive", "TRUE"), F.Eq("authorizedService", "matrix")),
            F.Eq("uid", "alice")));
        f.Evaluate(Alice()).ShouldBe(Tri.True);
    }

    [Fact]
    public void Matching_Is_Case_Insensitive_And_Ignores_Insignificant_Spaces()
    {
        Parse(F.Eq("MAIL", "alice@example.ORG")).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Eq("commonName", "alice example")).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Eq("isActive", "true")).Evaluate(Alice()).ShouldBe(Tri.True);
    }

    [Fact]
    public void Telephone_Numbers_Match_Without_Spaces_And_Hyphens()
        => Parse(F.Eq("mobile", "+9647701234567")).Evaluate(Alice()).ShouldBe(Tri.True);

    [Fact]
    public void Dn_Valued_Attributes_Match_On_The_Normalized_Dn()
        => Parse(F.Eq("memberOf", "CN=Mail, OU=Groups, DC=x")).Evaluate(Alice()).ShouldBe(Tri.True);

    [Fact]
    public void An_Absent_Attribute_Is_Undefined_And_Its_Negation_Matches_Nothing()
    {
        // The three-valued rule: `(!(missing=1))` must NOT match every entry.
        Parse(F.Eq("employeeNumber", "1")).Evaluate(Alice()).ShouldBe(Tri.Undefined);
        Parse(F.Not(F.Eq("employeeNumber", "1"))).Evaluate(Alice()).ShouldBe(Tri.Undefined);
        Parse(F.Or(F.Eq("employeeNumber", "1"), F.Eq("uid", "alice"))).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.And(F.Eq("employeeNumber", "1"), F.Eq("uid", "bob"))).Evaluate(Alice()).ShouldBe(Tri.False);
    }

    // RFC 4511 §4.5.1.7: Undefined is for what the server cannot judge. An
    // attribute it knows, absent from the entry, simply does not match.
    [Fact]
    public void A_Known_Attribute_The_Entry_Lacks_Is_False_And_Its_Negation_Matches()
    {
        Parse(F.Eq("mailAlias", "a@example.org")).Evaluate(Alice()).ShouldBe(Tri.False);
        Parse(F.Not(F.Eq("mailAlias", "a@example.org"))).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Not(F.Eq("rfc822Mailbox", "x@example.org"))).Evaluate(Alice()).ShouldBe(Tri.True);
        LdapSubschema.IsKnownAttribute("commonName").ShouldBeTrue();
        LdapSubschema.IsKnownAttribute("userid").ShouldBeTrue();
        LdapSubschema.IsKnownAttribute("employeeNumber").ShouldBeFalse();
    }

    [Fact]
    public void A_Filter_With_Too_Many_Terms_Is_Refused()
    {
        var terms = Enumerable.Range(0, LdapFilter.MaxNodes).Select(i => F.Eq("uid", "u" + i)).ToArray();
        Should.Throw<LdapProtocolException>(() => Parse(F.Or(terms)));
        Parse(F.Or(terms[..^1])).ShouldNotBeNull();
    }

    [Fact]
    public void An_Or_Over_Too_Many_Values_Is_Not_Anchored()
    {
        bool Indexed(string a) => a == "uid";
        var many = Enumerable.Range(0, LdapFilter.MaxAnchors + 1).Select(i => F.Eq("uid", "u" + i)).ToArray();
        Parse(F.Or(many)).Anchors(Indexed).ShouldBeNull();
        Parse(F.Or(many[..^1])).Anchors(Indexed)!.Count.ShouldBe(LdapFilter.MaxAnchors);
    }

    [Fact]
    public void Disabled_Users_Fail_The_IsActive_Clause()
    {
        var disabled = Alice();
        var entry = new LdapEntry(disabled.Dn).Add("uid", "alice").Add("isActive", "FALSE");
        Parse(F.Eq("isActive", "TRUE")).Evaluate(entry).ShouldBe(Tri.False);
    }

    [Fact]
    public void Presence_And_Substrings()
    {
        Parse(F.Present("objectClass")).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Present("jpegPhoto")).Evaluate(Alice()).ShouldBe(Tri.False);
        Parse(F.Substr("mail", "ali", [], "example.org")).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Substr("cn", null, ["ce ex"], null)).Evaluate(Alice()).ShouldBe(Tri.True);
        Parse(F.Substr("mail", "bob", [], null)).Evaluate(Alice()).ShouldBe(Tri.False);
        // initial and final must not overlap: "alice" is not "alic*ice" twice over.
        Parse(F.Substr("uid", "alic", [], "lice")).Evaluate(Alice()).ShouldBe(Tri.False);
    }

    [Fact]
    public void Ordering_Compares_Normalized_Values()
        => Parse(F.Ge("uid", "ALI")).Evaluate(Alice()).ShouldBe(Tri.True);

    [Fact]
    public void Anchors_Pick_An_Indexed_Equality_From_An_And()
    {
        var f = Parse(F.And(F.Eq("objectClass", "freexUser"), F.Eq("mail", "a@b.c")));
        f.Anchors(a => a is "uid" or "mail").ShouldBe([("mail", "a@b.c")]);
    }

    [Fact]
    public void Anchors_Need_Every_Branch_Of_An_Or()
    {
        Parse(F.Or(F.Eq("uid", "a"), F.Eq("mail", "a@b.c")))
            .Anchors(a => a is "uid" or "mail")!.Count.ShouldBe(2);
        // One unanchored branch means some matches would be missed by the
        // lookups, so the whole OR has to scan.
        Parse(F.Or(F.Eq("uid", "a"), F.Eq("mailAlias", "a@b.c")))
            .Anchors(a => a is "uid" or "mail").ShouldBeNull();
        // A negated equality anchors nothing.
        Parse(F.Not(F.Eq("uid", "a"))).Anchors(a => a is "uid").ShouldBeNull();
    }

    [Fact]
    public void A_Filter_Nested_Past_The_Limit_Is_A_Protocol_Error()
    {
        var f = F.Eq("uid", "a");
        for (var i = 0; i <= LdapFilter.MaxDepth + 1; i++) f = F.Not(f);
        Should.Throw<LdapProtocolException>(() => Parse(f));
    }

    [Fact]
    public void Attribute_Selection_Follows_Rfc4511()
    {
        new AttributeSelection([]).Includes("mail").ShouldBeTrue();
        new AttributeSelection([]).Includes("memberOf").ShouldBeFalse();     // operational
        new AttributeSelection(["+"]).Includes("memberOf").ShouldBeTrue();
        new AttributeSelection(["+"]).Includes("mail").ShouldBeFalse();
        new AttributeSelection(["mail", "memberOf"]).Includes("memberOf").ShouldBeTrue();
        new AttributeSelection(["commonName"]).Includes("cn").ShouldBeTrue();  // alias
        new AttributeSelection(["1.1"]).Includes("mail").ShouldBeFalse();
    }
}
