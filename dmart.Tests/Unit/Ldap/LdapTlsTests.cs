using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dmart.Config;
using Dmart.Ldap;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Ldap;

// The certificate holder's reload rules: a renewal on disk is picked up at the
// next check, and a renewal that does not load keeps the old certificate.
public sealed class LdapTlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ldaptls-").FullName;
    private string CertFile => Path.Combine(_dir, "fullchain.pem");
    private string KeyFile => Path.Combine(_dir, "privkey.pem");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Writes a fresh self-signed certificate and returns its thumbprint. Each
    // write gets a later timestamp so the change is visible whatever the
    // filesystem's timestamp resolution.
    private string WriteCertificate(DateTime stamp)
    {
        var pem = TestCertificates.SelfSigned();
        File.WriteAllText(CertFile, pem.CertificatePem);
        File.WriteAllText(KeyFile, pem.KeyPem);
        File.SetLastWriteTimeUtc(CertFile, stamp);
        File.SetLastWriteTimeUtc(KeyFile, stamp);
        return pem.Thumbprint;
    }

    private LdapTls Tls(TimeProvider clock) => new(
        Options.Create(new DmartSettings { LdapTlsCertFile = CertFile, LdapTlsKeyFile = KeyFile }),
        NullLogger<LdapTls>.Instance, clock);

    [Fact]
    public void A_Renewed_Certificate_Is_Served_After_The_Next_Check()
    {
        var clock = new ManualClock();
        var first = WriteCertificate(DateTime.UtcNow.AddMinutes(-10));
        var tls = Tls(clock);
        tls.Certificate.Thumbprint.ShouldBe(first);

        var second = WriteCertificate(DateTime.UtcNow.AddMinutes(-5));
        tls.Certificate.Thumbprint.ShouldBe(first, "the files are checked at most once a minute");

        clock.Now += TimeSpan.FromSeconds(61);
        tls.Certificate.Thumbprint.ShouldBe(second);
    }

    [Fact]
    public void A_Renewal_That_Does_Not_Load_Keeps_The_Previous_Certificate()
    {
        var clock = new ManualClock();
        var good = WriteCertificate(DateTime.UtcNow.AddMinutes(-10));
        var tls = Tls(clock);
        tls.Certificate.Thumbprint.ShouldBe(good);

        // Half-written renewal: a new key that does not match the old certificate.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(KeyFile, otherKey.ExportPkcs8PrivateKeyPem());
        File.SetLastWriteTimeUtc(KeyFile, DateTime.UtcNow.AddMinutes(-5));
        clock.Now += TimeSpan.FromSeconds(61);
        tls.Certificate.Thumbprint.ShouldBe(good);
    }

    [Fact]
    public void A_Certificate_That_Does_Not_Load_At_Startup_Stops_Startup()
    {
        File.WriteAllText(CertFile, "not a certificate");
        File.WriteAllText(KeyFile, "not a key");
        Should.Throw<Exception>(() => Tls(new ManualClock()).EnsureLoaded());
    }
}
