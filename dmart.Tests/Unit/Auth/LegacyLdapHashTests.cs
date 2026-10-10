using System.Security.Cryptography;
using System.Text;
using Dmart.Auth;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// The {SCHEME} hashes `dmart import-ldif` carries over from OpenLDAP, which
// dmart verifies once and replaces with Argon2id.
public class LegacyLdapHashTests
{
    private const string Password = "correct horse battery";
    private static readonly byte[] Salt = [0x5a, 0x17, 0x93, 0x01, 0xfe, 0x42, 0x00, 0x7c];

    // Built the way OpenLDAP's slappasswd builds them:
    // base64(digest(password + salt) + salt).
    private static string Hash(string scheme, Func<byte[], byte[]> digest, bool salted)
    {
        var input = Encoding.UTF8.GetBytes(Password).Concat(salted ? Salt : []).ToArray();
        return $"{{{scheme}}}" + Convert.ToBase64String(digest(input).Concat(salted ? Salt : []).ToArray());
    }

    public static TheoryData<string> Hashes() => new()
    {
        Hash("SSHA", SHA1.HashData, salted: true),
        Hash("SHA", SHA1.HashData, salted: false),
        Hash("SSHA256", SHA256.HashData, salted: true),
        Hash("SHA256", SHA256.HashData, salted: false),
        Hash("SSHA512", SHA512.HashData, salted: true),
        Hash("SHA512", SHA512.HashData, salted: false),
        // The scheme is case-insensitive, as OpenLDAP reads it.
        Hash("ssha", SHA1.HashData, salted: true),
    };

    [Theory]
    [MemberData(nameof(Hashes))]
    public void Every_Sha_Scheme_Verifies_The_Password_And_Nothing_Else(string encoded)
    {
        LegacyLdapHash.IsSha(encoded).ShouldBeTrue();
        LegacyLdapHash.IsLegacy(encoded).ShouldBeTrue();
        LegacyLdapHash.Verify(Password, encoded).ShouldBeTrue();
        LegacyLdapHash.Verify(Password + "x", encoded).ShouldBeFalse();
        LegacyLdapHash.Verify("", encoded).ShouldBeFalse();
    }

    // "secret" with the salt "foobar", computed outside .NET (Python's
    // hashlib, as slappasswd lays it out) and pinned, so the byte order
    // (digest, then salt) cannot drift with the code that builds Hashes().
    [Fact]
    public void An_Independently_Computed_Hash_Verifies()
    {
        LegacyLdapHash.Verify("secret", "{SSHA}+RFhh23pLIFdl5hpUJ03I8bQSnBmb29iYXI=").ShouldBeTrue();
        LegacyLdapHash.Verify("secrets", "{SSHA}+RFhh23pLIFdl5hpUJ03I8bQSnBmb29iYXI=").ShouldBeFalse();
    }

    [Theory]
    [InlineData("{SSHA}not base64!")]
    [InlineData("{SSHA}AAAA")]                   // shorter than a SHA-1 digest
    [InlineData("{SHA}" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // unsalted, but the wrong length
    [InlineData("{CRYPT}$6$abc$def")]
    [InlineData("{MD5}Xr4ilOzQ4PCOq3aQ0qbuaQ==")]
    [InlineData("plain")]
    public void Malformed_And_Unsupported_Values_Never_Verify(string encoded)
        => LegacyLdapHash.Verify("anything", encoded).ShouldBeFalse();

    [Fact]
    public async Task The_Hasher_Verifies_Legacy_Hashes_And_Asks_For_Them_To_Be_Replaced()
    {
        var hasher = new PasswordHasher(8192, 1, 1);
        var ssha = Hash("SSHA", SHA1.HashData, salted: true);
        (await hasher.VerifyAsync(Password, ssha)).ShouldBeTrue();
        (await hasher.VerifyAsync("wrong", ssha)).ShouldBeFalse();
        hasher.NeedsRehash(ssha).ShouldBeTrue();

        // OpenLDAP's argon2 module: a PHC string behind {ARGON2}.
        var argon = "{ARGON2}" + hasher.Hash(Password);
        LegacyLdapHash.IsSha(argon).ShouldBeFalse();
        LegacyLdapHash.IsLegacy(argon).ShouldBeTrue();
        (await hasher.VerifyAsync(Password, argon)).ShouldBeTrue();
        hasher.Verify(Password, argon).ShouldBeTrue();
        hasher.NeedsRehash(argon).ShouldBeTrue("stored in LDAP's form; dmart writes its own");

        hasher.NeedsRehash(hasher.Hash(Password)).ShouldBeFalse();
    }
}
