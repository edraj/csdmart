using Dmart.Auth;
using Dmart.Cli;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Cli;

// `dmart import-ldif`'s pure half: slapcat output in, accounts and groups out.
public class ImportLdifTests
{
    // Shaped like matrix-deploy's directory (freex schema), with the cases the
    // importer must tell apart: SSHA, clear text and an unusable scheme; a
    // folded base64 display name; a service account; a group; a person whose
    // mail is not a hosted domain.
    private const string Ldif = """
        version: 1

        # people
        dn: uid=alice,ou=people,dc=imx,dc=sh
        objectClass: top
        objectClass: freexUser
        uid: alice
        cn: Alice Example
        displayName:: QWxpY2Ug2LnZhNmK
        mail: Alice@IMX.sh
        mailAlias: help@imx.sh
        mailAlias: alice@imx.sh
        authorizedService: mail
        authorizedService: Matrix
        mobile: +964 770 123 4567
        isActive: TRUE
        userPassword: {SSHA}+RFhh23pLIFdl5hpUJ03I8bQSnBmb29iYXI=

        dn: uid=bob,ou=people,dc=imx,dc=sh
        objectClass: inetOrgPerson
        uid: bob
        cn: Bob
        mail: bob@gmail.com
        mailAlias: bob@imx.sh
        isActive: FALSE
        userPassword: plain-text-pw

        dn: uid=carol,ou=people,dc=imx,dc=sh
        objectClass: inetOrgPerson
        uid: carol
        cn: Car
         ol
        userPassword: {CRYPT}$6$salt$hash

        dn: cn=dovecot,ou=services,dc=imx,dc=sh
        objectClass: applicationProcess
        objectClass: simpleSecurityObject
        cn: dovecot
        userPassword: {SSHA}+RFhh23pLIFdl5hpUJ03I8bQSnBmb29iYXI=

        dn: cn=family,ou=groups,dc=imx,dc=sh
        objectClass: groupOfNames
        cn: family
        member: uid=alice,ou=people,dc=imx,dc=sh
        member: uid=bob,ou=people,dc=imx,dc=sh

        """;

    private static ImportLdifCommand.ImportPlan Plan(string[]? domains = null)
        => ImportLdifCommand.Plan(LdifReader.Read(Ldif), new PasswordHasher(8192, 1, 1), domains ?? ["imx.sh"]);

    [Fact]
    public void People_Services_And_Groups_Map_To_Users_Bots_And_Groups()
    {
        var plan = Plan();
        plan.Groups.ShouldHaveSingleItem().Name.ShouldBe("family");

        var alice = plan.Users.Single(u => u.Shortname == "alice");
        alice.Bot.ShouldBeFalse();
        alice.Active.ShouldBeTrue();
        alice.DisplayName.ShouldBe("Alice علي");
        alice.Mailbox.ShouldBe("alice@imx.sh");
        alice.Aliases.ShouldBe(["help@imx.sh"], "the mailbox itself is not also an alias");
        alice.Services.ShouldBe(["mail", "matrix"]);
        alice.Msisdn.ShouldBe("+9647701234567");
        alice.Groups.ShouldBe(["family"]);
        alice.Password.ShouldBe("{SSHA}+RFhh23pLIFdl5hpUJ03I8bQSnBmb29iYXI=", "kept, to be replaced at first sign-in");

        var svc = plan.Users.Single(u => u.Shortname == "dovecot");
        svc.Bot.ShouldBeTrue();
        svc.Mailbox.ShouldBeNull();
        plan.Notes.ShouldContain(n => n.Contains("LDAP_SERVICE_ACCOUNTS") && n.Contains("dovecot"));
    }

    [Fact]
    public void Clear_Text_Is_Hashed_And_An_Unusable_Scheme_Imports_Without_A_Password()
    {
        var plan = Plan();
        var bob = plan.Users.Single(u => u.Shortname == "bob");
        bob.Active.ShouldBeFalse();
        bob.Password.ShouldNotBeNull();
        bob.Password!.ShouldStartWith("$argon2id$");
        new PasswordHasher(8192, 1, 1).Verify("plain-text-pw", bob.Password).ShouldBeTrue();
        plan.Notes.ShouldContain(n => n.StartsWith("bob:") && n.Contains("CLEAR TEXT"));

        var carol = plan.Users.Single(u => u.Shortname == "carol");
        carol.DisplayName.ShouldBe("Carol", "a folded line continues the one before");
        carol.Password.ShouldBeNull();
        plan.Notes.ShouldContain(n => n.StartsWith("carol:") && n.Contains("{CRYPT}"));
    }

    // A `mail` this deployment does not host is somebody's address elsewhere:
    // the contact email, not a mailbox, and then aliases have nowhere to go.
    [Fact]
    public void A_Mail_Outside_The_Hosted_Domains_Becomes_The_Contact_Email()
    {
        var bob = Plan().Users.Single(u => u.Shortname == "bob");
        bob.Mailbox.ShouldBeNull();
        bob.Email.ShouldBe("bob@gmail.com");
        bob.Aliases.ShouldBeEmpty();

        // With no hosted domains configured, every mail is a mailbox.
        Plan([]).Users.Single(u => u.Shortname == "bob").Mailbox.ShouldBe("bob@gmail.com");
    }
}
