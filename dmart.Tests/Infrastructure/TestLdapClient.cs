using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dmart.Ldap;

namespace Dmart.Tests.Infrastructure;

// A minimal LDAP client for the directory face's integration tests: BER over a
// socket, plain, LDAPS or upgraded with StartTLS, from a chosen loopback source
// address so per-address limits can be told apart within one host.
internal sealed class TestLdapClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly X509Certificate2? _trust;
    private Stream _stream;
    private int _nextId = 1;

    private TestLdapClient(TcpClient tcp, Stream stream, X509Certificate2? trust)
    {
        _tcp = tcp;
        _stream = stream;
        _trust = trust;
    }

    // `trust` is the server certificate a TLS handshake must present (pinned:
    // the tests' certificates are self-signed).
    public static async Task<TestLdapClient> ConnectAsync(int port, IPAddress? source = null, X509Certificate2? trust = null)
    {
        var tcp = source is null ? new TcpClient() : new TcpClient(new IPEndPoint(source, 0));
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return new TestLdapClient(tcp, tcp.GetStream(), trust);
    }

    public static async Task<TestLdapClient> ConnectTlsAsync(int port, X509Certificate2 trust, IPAddress? source = null)
    {
        var client = await ConnectAsync(port, source, trust);
        await client.HandshakeAsync();
        return client;
    }

    public bool Secure => _stream is SslStream;

    private async Task HandshakeAsync()
    {
        var expected = _trust ?? throw new InvalidOperationException("no certificate to trust");
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false,
            (_, cert, _, _) => cert is not null && cert.GetCertHashString() == expected.GetCertHashString());
        await ssl.AuthenticateAsClientAsync("localhost");
        _stream = ssl;
    }

    // Sends StartTLS; on success performs the handshake. Returns the result code.
    public async Task<int> StartTlsAsync()
    {
        var (code, _) = await ExtendedAsync(LdapOid.StartTls);
        if (code == LdapResult.Success) await HandshakeAsync();
        return code;
    }

    public async Task<(int Code, string? ResponseName)> ExtendedAsync(string oid)
    {
        await SendAsync(LdapOp.ExtendedRequest, w =>
            w.WriteOctetString(Encoding.UTF8.GetBytes(oid), new Asn1Tag(TagClass.ContextSpecific, 0)));
        var (_, op, _) = await ReceiveAsync();
        var code = ResultCode(op);
        op.ReadOctetString();   // matchedDN
        op.ReadOctetString();   // diagnosticMessage
        string? name = null;
        if (op.HasData && op.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 10)))
            name = Encoding.UTF8.GetString(op.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 10)));
        return (code, name);
    }

    private async Task SendAsync(int opTag, Action<AsnWriter> body, byte[]? pagedCookie = null, int pageSize = 0)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteInteger(_nextId++);
            using (w.PushSequence(new Asn1Tag(TagClass.Application, opTag, isConstructed: true))) body(w);
            if (pagedCookie is not null)
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                using (w.PushSequence())
                {
                    w.WriteOctetString(Encoding.UTF8.GetBytes(LdapOid.PagedResults));
                    var v = new AsnWriter(AsnEncodingRules.BER);
                    using (v.PushSequence())
                    {
                        v.WriteInteger(pageSize);
                        v.WriteOctetString(pagedCookie);
                    }
                    w.WriteOctetString(v.Encode());
                }
            }
        }
        await _stream.WriteAsync(w.Encode());
    }

    // The op, plus the paged-results cookie when the message carries one.
    private async Task<(int Tag, AsnReader Op, byte[]? Cookie)> ReceiveAsync()
    {
        var frame = await LdapCodec.ReadFrameAsync(_stream, 1 << 20, CancellationToken.None)
            ?? throw new InvalidOperationException("server closed the connection");
        var msg = new AsnReader(frame, AsnEncodingRules.BER).ReadSequence();
        msg.TryReadInt32(out _);
        var tag = msg.PeekTag();
        var op = msg.ReadSequence(tag);
        byte[]? cookie = null;
        if (msg.HasData)
        {
            var controls = msg.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            while (controls.HasData)
            {
                var control = controls.ReadSequence();
                var oid = Encoding.UTF8.GetString(control.ReadOctetString());
                if (control.HasData && control.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)) control.ReadBoolean();
                if (oid != LdapOid.PagedResults || !control.HasData) continue;
                var value = new AsnReader(control.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
                value.TryReadInt32(out _);
                cookie = value.ReadOctetString();
            }
        }
        return (tag.TagValue, op, cookie);
    }

    private static int ResultCode(AsnReader op)
    {
        var bytes = op.ReadEnumeratedBytes().Span;
        var code = 0;
        foreach (var b in bytes) code = (code << 8) | b;
        return code;
    }

    public async Task<int> BindAsync(string dn, string password)
    {
        await SendAsync(LdapOp.BindRequest, w =>
        {
            w.WriteInteger(3);
            w.WriteOctetString(Encoding.UTF8.GetBytes(dn));
            w.WriteOctetString(Encoding.UTF8.GetBytes(password), new Asn1Tag(TagClass.ContextSpecific, 0));
        });
        var (_, op, _) = await ReceiveAsync();
        return ResultCode(op);
    }

    public async Task<(int Code, List<TestLdapEntry> Entries)> SearchAsync(string baseDn, byte[] filter, params string[] attrs)
        => await SearchAsync(baseDn, filter, 2, attrs);

    public async Task<(int Code, List<TestLdapEntry> Entries)> SearchAsync(string baseDn, byte[] filter, int scope, params string[] attrs)
    {
        var (code, entries, _) = await SearchPageAsync(baseDn, filter, scope, attrs, null, 0);
        return (code, entries);
    }

    // A search carrying a time limit; paged (one page) when pageSize > 0.
    public async Task<(int Code, List<TestLdapEntry> Entries)> SearchTimedAsync(
        string baseDn, byte[] filter, int timeLimitSeconds, int pageSize = 0)
    {
        var (code, entries, _) = await SearchPageAsync(baseDn, filter, 2, [],
            pageSize > 0 ? [] : null, pageSize, timeLimitSeconds);
        return (code, entries);
    }

    public async Task<(int Code, List<TestLdapEntry> Entries, int Pages)> SearchPagedAsync(
        string baseDn, byte[] filter, int pageSize, params string[] attrs)
    {
        var all = new List<TestLdapEntry>();
        byte[] cookie = [];
        for (var pages = 1; ; pages++)
        {
            var (code, entries, next) = await SearchPageAsync(baseDn, filter, 2, attrs, cookie, pageSize);
            all.AddRange(entries);
            if (code != LdapResult.Success || next is not { Length: > 0 }) return (code, all, pages);
            if (pages > 10_000) throw new InvalidOperationException("paged search never ended");
            cookie = next;
        }
    }

    private async Task<(int Code, List<TestLdapEntry> Entries, byte[]? Cookie)> SearchPageAsync(
        string baseDn, byte[] filter, int scope, string[] attrs, byte[]? pagedCookie, int pageSize, int timeLimit = 0)
    {
        await SendAsync(LdapOp.SearchRequest, w =>
        {
            w.WriteOctetString(Encoding.UTF8.GetBytes(baseDn));
            w.WriteEncodedValue([0x0A, 0x01, (byte)scope]);
            w.WriteEncodedValue([0x0A, 0x01, 0x00]);
            w.WriteInteger(0);
            w.WriteInteger(timeLimit);
            w.WriteBoolean(false);
            w.WriteEncodedValue(filter);
            using (w.PushSequence())
                foreach (var a in attrs) w.WriteOctetString(Encoding.UTF8.GetBytes(a));
        }, pagedCookie, pageSize);

        var entries = new List<TestLdapEntry>();
        while (true)
        {
            var (tag, op, cookie) = await ReceiveAsync();
            if (tag == LdapOp.SearchResultDone) return (ResultCode(op), entries, cookie);
            var dn = Encoding.UTF8.GetString(op.ReadOctetString());
            var attrMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var list = op.ReadSequence();
            while (list.HasData)
            {
                var a = list.ReadSequence();
                var name = Encoding.UTF8.GetString(a.ReadOctetString());
                var values = new List<string>();
                var set = a.ReadSetOf();
                while (set.HasData) values.Add(Encoding.UTF8.GetString(set.ReadOctetString()));
                attrMap[name] = values;
            }
            entries.Add(new TestLdapEntry(dn, attrMap));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}

internal sealed record TestLdapEntry(string Dn, Dictionary<string, List<string>> Attrs);

internal static class TestLdapFilter
{
    private static byte[] Encode(Action<AsnWriter> write)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        write(w);
        return w.Encode();
    }

    public static byte[] Eq(string attr, string value) => Encode(w =>
    {
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true)))
        {
            w.WriteOctetString(Encoding.UTF8.GetBytes(attr));
            w.WriteOctetString(Encoding.UTF8.GetBytes(value));
        }
    });

    public static byte[] Present(string attr)
        => Encode(w => w.WriteOctetString(Encoding.UTF8.GetBytes(attr), new Asn1Tag(TagClass.ContextSpecific, 7)));

    public static byte[] Not(byte[] child) => Encode(w =>
    {
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
            w.WriteEncodedValue(child);
    });

    public static byte[] And(params byte[][] children) => Encode(w =>
    {
        using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            foreach (var c in children) w.WriteEncodedValue(c);
    });
}
