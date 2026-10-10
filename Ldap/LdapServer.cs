using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Ldap;

// A read-only LDAPv3 listener (LDAP_PORT) that serves LdapDirectory. It speaks
// the subset that mail servers, Gitea and Dex actually use: simple bind,
// search with the full filter grammar, paged results, Who Am I, and the root
// DSE. Every write operation is refused with unwillingToPerform — users are
// changed through dmart, never through LDAP.
//
// A plain socket listener rather than a Kestrel endpoint on purpose: adding a
// Listen() to Kestrel makes it ignore the URLs the HTTP side is configured
// with, and LDAP needs nothing from the HTTP pipeline anyway.
//
// No TLS yet. LdapHost defaults to loopback and the settings say why.
internal sealed class LdapServer(
    IOptions<DmartSettings> settings,
    LdapDirectory directory,
    UserService userService,
    PasswordHasher hasher,
    ILogger<LdapServer> log) : BackgroundService
{
    // Bind and search requests are a few hundred bytes. Anything near this is
    // not a client this face serves.
    private const int MaxMessageBytes = 1 << 20;
    private const int MaxConnections = 512;
    // Dovecot and Postfix hold connections open between lookups; a connection
    // idle this long is closed and they reconnect on next use.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<Guid, Task> _connections = new();

    public IPEndPoint? BoundEndpoint { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = settings.Value;
        if (s.LdapPort <= 0) return;

        var listener = new TcpListener(IPAddress.Parse(s.LdapHost), s.LdapPort);
        listener.Start(backlog: 128);
        BoundEndpoint = (IPEndPoint)listener.LocalEndpoint;
        log.LogInformation("LDAP directory face listening on {Endpoint}, base {Base}", BoundEndpoint, directory.BaseDn);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (!_slots.Wait(0, CancellationToken.None))
                {
                    log.LogWarning("LDAP connection from {Peer} refused: {Max} connections open",
                        client.Client.RemoteEndPoint, MaxConnections);
                    client.Dispose();
                    continue;
                }
                var id = Guid.NewGuid();
                _connections[id] = Task.Run(async () =>
                {
                    try { await ServeAsync(client, stoppingToken); }
                    finally
                    {
                        _slots.Release();
                        _connections.TryRemove(id, out _);
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(Task.WhenAll(_connections.Values), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    private sealed class Session
    {
        public LdapPrincipal Principal { get; set; } = LdapPrincipal.Anonymous;
        public PagedSearch? Paged { get; set; }
    }

    // The rest of a paged search: the cookie handed to the client and the
    // entries it has not been sent yet. One per connection — a new paged search
    // replaces it, which is what every client does anyway.
    private sealed record PagedSearch(byte[] Cookie, List<LdapEntry> Remaining, int FinalCode, string FinalMessage);

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        client.NoDelay = true;
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var stream = client.GetStream();
        var session = new Session();

        try
        {
            while (!stop.IsCancellationRequested)
            {
                byte[]? frame;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    idle.CancelAfter(IdleTimeout);
                    frame = await LdapCodec.ReadFrameAsync(stream, MaxMessageBytes, idle.Token);
                }
                if (frame is null) return;

                LdapRequest req;
                try
                {
                    req = LdapCodec.Decode(frame);
                }
                catch (LdapProtocolException ex)
                {
                    log.LogInformation("LDAP protocol error from {Peer}: {Error}", peer, ex.Message);
                    await stream.WriteAsync(LdapCodec.NoticeOfDisconnection(LdapResult.ProtocolError, ex.Message), stop);
                    return;
                }

                if (req is LdapUnbindRequest) return;
                await DispatchAsync(req, session, stream, peer, stop);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or LdapProtocolException or SocketException)
        {
            // Client went away, idled out, or sent an unframeable message:
            // nothing useful left to say to it.
        }
    }

    private async Task DispatchAsync(LdapRequest req, Session session, Stream stream, string peer, CancellationToken ct)
    {
        Task Send(byte[] message) => stream.WriteAsync(message, ct).AsTask();

        // A critical control the face does not implement must fail the
        // operation (RFC 4511 §4.1.11). Paged results is the one it does.
        var unknownCritical = req.Controls.FirstOrDefault(c => c.Critical
            && !(req is LdapSearchRequest && c.Oid == LdapOid.PagedResults));

        try
        {
            switch (req)
            {
                case LdapBindRequest b:
                    if (unknownCritical is not null) { await Send(Unsupported(b.MessageId, LdapOp.BindResponse, unknownCritical)); return; }
                    await Send(await BindAsync(b, session, peer, ct));
                    return;

                case LdapSearchRequest s:
                    if (unknownCritical is not null) { await Send(Unsupported(s.MessageId, LdapOp.SearchResultDone, unknownCritical)); return; }
                    await SearchAsync(s, session, Send, ct);
                    return;

                case LdapExtendedRequest e:
                    await Send(Extended(e, session));
                    return;

                case LdapAbandonRequest:
                    // Operations run one at a time per connection, so by the
                    // time an abandon is read its target has already finished.
                    return;

                case LdapUnsupportedRequest u:
                    await Send(LdapCodec.Result(u.MessageId, u.OpTag + 1, LdapResult.UnwillingToPerform,
                        "this directory is read-only; change users through dmart"));
                    return;
            }
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException or SocketException))
        {
            log.LogError(ex, "LDAP operation from {Peer} failed", peer);
            var opTag = req switch
            {
                LdapBindRequest => LdapOp.BindResponse,
                LdapSearchRequest => LdapOp.SearchResultDone,
                _ => LdapOp.ExtendedResponse,
            };
            await Send(LdapCodec.Result(req.MessageId, opTag, LdapResult.OperationsError, "internal error"));
        }
    }

    private static byte[] Unsupported(int messageId, int opTag, LdapControl control)
        => LdapCodec.Result(messageId, opTag, LdapResult.UnavailableCriticalExtension,
            $"critical control {control.Oid} is not supported");

    // ----- bind -----

    private async Task<byte[]> BindAsync(LdapBindRequest b, Session session, string peer, CancellationToken ct)
    {
        session.Principal = LdapPrincipal.Anonymous;
        session.Paged = null;
        byte[] Reply(int code, string message = "") => LdapCodec.Result(b.MessageId, LdapOp.BindResponse, code, message);

        if (b.Version != 3) return Reply(LdapResult.ProtocolError, "only LDAPv3 is supported");
        if (b.SaslMechanism is not null)
            return Reply(LdapResult.AuthMethodNotSupported, "SASL is not supported; use a simple bind");

        var password = b.SimplePassword ?? "";
        if (b.Name.Length == 0)
        {
            // Anonymous bind succeeds and grants nothing past the root DSE.
            // Refusing it outright (slapd's `disallow bind_anon`) breaks
            // `ldapsearch -x` and every tool that probes the root DSE first.
            return password.Length == 0
                ? Reply(LdapResult.Success)
                : Reply(LdapResult.UnwillingToPerform, "a password needs a bind DN");
        }
        if (password.Length == 0)
        {
            // RFC 4513 §5.1.2: a DN with an empty password is an
            // "unauthenticated" bind, which servers should refuse — the
            // classic hole where a client treats it as a successful login.
            return Reply(LdapResult.UnwillingToPerform, "unauthenticated bind (DN without a password) is not allowed");
        }

        var principal = directory.ClassifyBindDn(b.Name);
        try
        {
            if (principal.Kind == PrincipalKind.Anonymous)
            {
                // A DN that names nobody costs what a wrong password costs.
                _ = await hasher.VerifyAsync(password, hasher.DecoyHash, ct);
                log.LogInformation("LDAP bind from {Peer} as {Dn} failed: no such account", peer, b.Name);
                return Reply(LdapResult.InvalidCredentials);
            }

            var user = await userService.VerifyDirectoryBindAsync(principal.Shortname!, password, ct);
            if (user is null)
            {
                log.LogInformation("LDAP bind from {Peer} as {Dn} failed", peer, b.Name);
                return Reply(LdapResult.InvalidCredentials);
            }
        }
        catch (PasswordHashingCapacityException ex)
        {
            return Reply(LdapResult.Busy, $"password hashing is at capacity; retry in {ex.RetryAfterSeconds}s");
        }

        session.Principal = principal;
        log.LogDebug("LDAP bind from {Peer} as {Dn}", peer, b.Name);
        return Reply(LdapResult.Success);
    }

    // ----- search -----

    private async Task SearchAsync(LdapSearchRequest s, Session session, Func<byte[], Task> send, CancellationToken ct)
    {
        var selection = new AttributeSelection(s.Attributes);
        var pagedControl = s.Controls.FirstOrDefault(c => c.Oid == LdapOid.PagedResults);

        if (pagedControl is null)
        {
            var max = settings.Value.LdapSizeLimit;
            var limit = s.SizeLimit > 0 ? Math.Min(s.SizeLimit, max) : max;
            var outcome = await directory.SearchAsync(s, session.Principal, limit, ct);
            var code = outcome.Code;
            var entries = outcome.Entries;
            if (entries.Count > limit)
            {
                entries = entries.Take(limit).ToList();
                if (code == LdapResult.Success) code = LdapResult.SizeLimitExceeded;
            }
            foreach (var e in entries) await send(LdapCodec.SearchEntry(s.MessageId, e, selection, s.TypesOnly));
            await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, code, outcome.Message, outcome.MatchedDn));
            return;
        }

        var (size, cookie) = LdapCodec.ReadPagedControl(pagedControl.Value, s.MessageId);
        PagedSearch state;
        if (cookie.Length > 0)
        {
            if (session.Paged is null || !CryptographicOperations.FixedTimeEquals(cookie, session.Paged.Cookie))
            {
                await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, LdapResult.UnwillingToPerform,
                    "unknown paged results cookie"));
                return;
            }
            state = session.Paged;
            if (size == 0)
            {
                // Page size 0 with a cookie abandons the paged search.
                session.Paged = null;
                await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, LdapResult.Success,
                    controls: [LdapCodec.PagedControl([])]));
                return;
            }
        }
        else
        {
            // Paged searches are bounded by LdapMaxScan, not LdapSizeLimit: the
            // client is asking for everything, a page at a time.
            var outcome = await directory.SearchAsync(s, session.Principal, int.MaxValue - 1, ct);
            if (outcome.Code is not (LdapResult.Success or LdapResult.AdminLimitExceeded))
            {
                await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, outcome.Code, outcome.Message, outcome.MatchedDn));
                return;
            }
            state = new PagedSearch(RandomNumberGenerator.GetBytes(16), outcome.Entries.ToList(), outcome.Code, outcome.Message);
        }

        var pageSize = size == 0 ? state.Remaining.Count : size;
        var page = state.Remaining.Take(pageSize).ToList();
        state.Remaining.RemoveRange(0, page.Count);
        foreach (var e in page) await send(LdapCodec.SearchEntry(s.MessageId, e, selection, s.TypesOnly));

        if (state.Remaining.Count > 0)
        {
            session.Paged = state;
            await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, LdapResult.Success,
                controls: [LdapCodec.PagedControl(state.Cookie)]));
        }
        else
        {
            session.Paged = null;
            await send(LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, state.FinalCode, state.FinalMessage,
                controls: [LdapCodec.PagedControl([])]));
        }
    }

    // ----- extended -----

    private byte[] Extended(LdapExtendedRequest e, Session session) => e.Name switch
    {
        LdapOid.WhoAmI => LdapCodec.Extended(e.MessageId, LdapResult.Success, "", responseValue:
            Encoding.UTF8.GetBytes(session.Principal.Kind == PrincipalKind.Anonymous
                ? "" : "dn:" + directory.DnOf(session.Principal))),
        LdapOid.StartTls => LdapCodec.Extended(e.MessageId, LdapResult.Unavailable,
            "TLS is not configured on this listener"),
        LdapOid.PasswordModify => LdapCodec.Extended(e.MessageId, LdapResult.UnwillingToPerform,
            "change passwords through dmart"),
        _ => LdapCodec.Extended(e.MessageId, LdapResult.ProtocolError, $"unsupported extended operation {e.Name}"),
    };

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
