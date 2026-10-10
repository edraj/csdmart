using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Ldap;

// The LDAP face's server certificate: LDAPS connections and StartTLS upgrades
// both take it from here. Loaded from PEM files (LdapTlsCertFile, the chain with
// the leaf first, and LdapTlsKeyFile) and re-read when either file's timestamp
// changes, checked at most once a minute, so a certificate renewed by ACME is
// picked up by the next handshake without a restart. A renewal that fails to
// load keeps the previous certificate in service and logs why.
//
// `clock` is for tests; the host leaves it to the system clock.
internal sealed class LdapTls(IOptions<DmartSettings> settings, ILogger<LdapTls> log, TimeProvider? clock = null)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);
    // A client that connects and never finishes the handshake holds a slot.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private SslStreamCertificateContext? _context;
    private (DateTime Cert, DateTime Key) _loadedStamps;
    private DateTime _checkedAt;

    public bool Configured => settings.Value.LdapTlsConfigured;

    // The leaf certificate handshakes present right now.
    public X509Certificate2 Certificate => Current().TargetCertificate;

    // Loads the certificate now, so a broken file stops startup instead of
    // failing every handshake later.
    public void EnsureLoaded()
    {
        if (Configured) _ = Current();
    }

    public async Task<SslStream> AuthenticateAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                // Protocol versions and ciphers are left to the system's
                // crypto policy (TLS 1.2 and up on current distributions).
                ServerCertificateContext = Current(),
                ClientCertificateRequired = false,
            }, timeout.Token);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    private SslStreamCertificateContext Current()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_context is not null && now - _checkedAt < RecheckEvery) return _context;
            _checkedAt = now;

            var s = settings.Value;
            var stamps = (File.GetLastWriteTimeUtc(s.LdapTlsCertFile), File.GetLastWriteTimeUtc(s.LdapTlsKeyFile));
            if (_context is not null && stamps == _loadedStamps) return _context;

            try
            {
                _context = Load(s.LdapTlsCertFile, s.LdapTlsKeyFile);
                _loadedStamps = stamps;
                log.LogInformation("LDAP TLS certificate loaded from {File}", s.LdapTlsCertFile);
            }
            catch (Exception ex) when (_context is not null && ex is IOException or UnauthorizedAccessException
                                           or System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                // Mid-renewal (one file written, the other not yet) or broken:
                // keep serving the old one and try again at the next check.
                log.LogError(ex, "reloading the LDAP TLS certificate from {File} failed; keeping the previous one",
                    s.LdapTlsCertFile);
            }
            return _context;
        }
    }

    private static SslStreamCertificateContext Load(string certFile, string keyFile)
    {
        // CreateFromPemFile takes the first certificate in the file, which in a
        // full chain is the leaf; the rest are sent as intermediates.
        var leaf = X509Certificate2.CreateFromPemFile(certFile, keyFile);
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(certFile);
        var intermediates = new X509Certificate2Collection();
        for (var i = 1; i < chain.Count; i++) intermediates.Add(chain[i]);
        return SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
    }
}
