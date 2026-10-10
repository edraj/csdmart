using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dmart.Tests.Infrastructure;

internal static class TestCertificates
{
    public sealed record Pem(string CertificatePem, string KeyPem, string Thumbprint, X509Certificate2 Public);

    // A self-signed certificate for localhost / 127.0.0.1, as PEM files take it.
    public static Pem SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var certPem = cert.ExportCertificatePem();
        return new Pem(certPem, key.ExportPkcs8PrivateKeyPem(), cert.Thumbprint, X509Certificate2.CreateFromPem(certPem));
    }
}
