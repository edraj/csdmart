using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
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
// TLS (LdapTls): LDAP_PORT offers StartTLS and LDAPS_PORT is TLS from the
// first byte, both once a certificate is configured. Brute-force limits beyond
// the account lockout are LdapBindGuard's.
internal sealed class LdapServer(
    IOptions<DmartSettings> settings,
    LdapDirectory directory,
    UserService userService,
    PasswordHasher hasher,
    LdapTls tls,
    LdapBindGuard guard,
    DirectoryIndexStatus index,
    ILogger<LdapServer> log,
    // For tests; the host leaves it to the system clock.
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Bind and search requests are a few hundred bytes. Anything near this is
    // not a client this face serves; before a bind, far less is.
    private const int MaxMessageBytes = 1 << 20;
    private const int PreBindMaxMessageBytes = 64 << 10;
    private const int MaxConnections = 512;
    // Open connections from one address (an IPv6 /64) outside
    // LDAP_TRUSTED_PEERS, so one host cannot take every slot.
    private const int MaxConnectionsPerAddress = 32;
    // Dovecot and Postfix hold connections open between lookups; a connection
    // idle this long is closed and they reconnect on next use. One that has
    // not bound, from outside the trusted peers, gets far less.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PreBindIdleTimeout = TimeSpan.FromSeconds(30);
    // From a message's first byte to its last, and for each response write:
    // a client that trickles a request or stops reading its results gives
    // its slot back.
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);
    // How long a bind is taken on trust before the account is read again: a
    // pooled connection outlives a deactivation by at most this.
    private static readonly TimeSpan RecheckBindEvery = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<Guid, Task> _connections = new();
    private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();

    public IPEndPoint? BoundEndpoint { get; private set; }
    public IPEndPoint? BoundTlsEndpoint { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = settings.Value;
        if (s.LdapPort <= 0 && s.LdapsPort <= 0) return;

        // A certificate that does not load should stop startup here, not
        // fail every handshake after it.
        tls.EnsureLoaded();
        var host = IPAddress.Parse(s.LdapHost);
        if (!tls.Configured && !IPAddress.IsLoopback(host))
            log.LogWarning("LDAP directory face on {Host} without TLS: passwords cross the network in the clear "
                + "unless that network is private (WireGuard). Set LDAP_TLS_CERT_FILE and LDAP_TLS_KEY_FILE.", host);

        var listeners = new List<TcpListener>();
        try
        {
            var loops = new List<Task>();
            if (s.LdapPort > 0)
            {
                var plain = Listen(host, s.LdapPort, listeners);
                BoundEndpoint = (IPEndPoint)plain.LocalEndpoint;
                log.LogInformation("LDAP directory face listening on {Endpoint}{StartTls}, base {Base}",
                    BoundEndpoint, tls.Configured ? " (StartTLS)" : "", directory.BaseDn);
                loops.Add(AcceptAsync(plain, implicitTls: false, stoppingToken));
            }
            if (s.LdapsPort > 0)
            {
                var secure = Listen(host, s.LdapsPort, listeners);
                BoundTlsEndpoint = (IPEndPoint)secure.LocalEndpoint;
                log.LogInformation("LDAPS directory face listening on {Endpoint}, base {Base}", BoundTlsEndpoint, directory.BaseDn);
                loops.Add(AcceptAsync(secure, implicitTls: true, stoppingToken));
            }
            await Task.WhenAll(loops);
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
            await Task.WhenAny(Task.WhenAll(_connections.Values), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    private static TcpListener Listen(IPAddress host, int port, List<TcpListener> started)
    {
        var listener = new TcpListener(host, port);
        listener.Start(backlog: 128);
        started.Add(listener);
        return listener;
    }

    // Both listeners share the connection slots.
    private async Task AcceptAsync(TcpListener listener, bool implicitTls, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (SocketException ex)
                {
                    // A connection reset before it was accepted, or a burst
                    // past the descriptor limit: that one connection is lost,
                    // not the listener.
                    log.LogInformation("LDAP accept failed: {Error}", ex.SocketErrorCode);
                    continue;
                }
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var key = address is null || guard.IsTrusted(address) ? null : LdapBindGuard.Subscriber(address);
                if (key is not null && _perAddress.AddOrUpdate(key, 1, (_, n) => n + 1) > MaxConnectionsPerAddress)
                {
                    Release(key);
                    log.LogWarning("LDAP connection from {Peer} refused: {Max} connections open from that address",
                        client.Client.RemoteEndPoint, MaxConnectionsPerAddress);
                    client.Dispose();
                    continue;
                }
                if (!_slots.Wait(0, CancellationToken.None))
                {
                    if (key is not null) Release(key);
                    log.LogWarning("LDAP connection from {Peer} refused: {Max} connections open",
                        client.Client.RemoteEndPoint, MaxConnections);
                    client.Dispose();
                    continue;
                }
                var id = Guid.NewGuid();
                _connections[id] = Task.Run(async () =>
                {
                    try { await ServeAsync(client, implicitTls, stoppingToken); }
                    finally
                    {
                        _slots.Release();
                        if (key is not null) Release(key);
                        _connections.TryRemove(id, out _);
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Release(IPAddress key)
    {
        // Decrement, and drop the entry at zero so the table holds only
        // addresses with connections open.
        while (_perAddress.TryGetValue(key, out var n))
        {
            if (n <= 1 ? _perAddress.TryRemove(new KeyValuePair<IPAddress, int>(key, n))
                       : _perAddress.TryUpdate(key, n - 1, n))
                return;
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private LdapPrincipal _principal = LdapPrincipal.Anonymous;
        public LdapPrincipal Principal
        {
            get => _principal;
            set { _principal = value; CheckedAt = DateTime.UtcNow; }
        }
        // When the bound account was last read and found usable.
        public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
        private PagedSearch? _paged;
        public PagedSearch? Paged => _paged;

        // Replacing or clearing a paged search disposes the one before it: its
        // enumerator is a suspended stream over the users table.
        public async ValueTask SetPagedAsync(PagedSearch? next)
        {
            var previous = _paged;
            _paged = next;
            if (previous is not null && !ReferenceEquals(previous, next)) await previous.Results.DisposeAsync();
        }

        public ValueTask DisposeAsync() => SetPagedAsync(null);
    }

    // A paged search in progress: the cookie handed to the client, the
    // request it answers, and the streaming search it resumes. One per
    // connection — a new paged search replaces it, which is what every client
    // does anyway.
    private sealed record PagedSearch(
        byte[] Cookie, string Request, LdapDirectory.Search Search, IAsyncEnumerator<LdapEntry?> Results);

    // RFC 2696: a continuation repeats the original request, cookie aside.
    private static string RequestKey(LdapSearchRequest s)
        => string.Join('\0', s.BaseDn, ((int)s.Scope).ToString(System.Globalization.CultureInfo.InvariantCulture),
            s.Filter.ToString(), string.Join(',', s.Attributes), s.TypesOnly ? "1" : "0");

    // One client connection. The stream is replaced when StartTLS upgrades it.
    private sealed class Connection(string peer, IPAddress? address, bool trusted, Stream stream)
    {
        public string Peer { get; } = peer;
        public IPAddress? Address { get; } = address;
        public bool Trusted { get; } = trusted;
        public Stream Stream { get; set; } = stream;
        public bool Secure => Stream is SslStream;

        // Every response goes through here: a client that stops reading loses
        // the connection after WriteTimeout instead of holding it forever.
        public async Task WriteAsync(byte[] message, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WriteTimeout);
            await Stream.WriteAsync(message, timeout.Token);
        }
    }

    private async Task ServeAsync(TcpClient client, bool implicitTls, CancellationToken stop)
    {
        using var _ = client;
        client.NoDelay = true;
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        var conn = new Connection(endpoint?.ToString() ?? "?", endpoint?.Address,
            endpoint is not null && guard.IsTrusted(endpoint.Address), client.GetStream());
        await using var session = new Session();

        try
        {
            if (implicitTls) conn.Stream = await tls.AuthenticateAsync(conn.Stream, stop);
            while (!stop.IsCancellationRequested)
            {
                byte[]? frame;
                // Until a bind succeeds, a connection from outside the
                // trusted peers is held to small messages and a short idle.
                var established = conn.Trusted || session.Principal.Kind != PrincipalKind.Anonymous;
                LdapRequest req;
                try
                {
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        idle.CancelAfter(established ? IdleTimeout : PreBindIdleTimeout);
                        frame = await LdapCodec.ReadFrameAsync(conn.Stream,
                            established ? MaxMessageBytes : PreBindMaxMessageBytes, FrameTimeout, idle.Token, stop);
                    }
                    if (frame is null) return;
                    req = LdapCodec.Decode(frame);
                }
                catch (LdapProtocolException ex)
                {
                    log.LogInformation("LDAP protocol error from {Peer}: {Error}", conn.Peer, ex.Message);
                    await conn.WriteAsync(LdapCodec.NoticeOfDisconnection(LdapResult.ProtocolError, ex.Message), stop);
                    return;
                }

                if (req is LdapUnbindRequest) return;
                if (req is LdapExtendedRequest { Name: LdapOid.StartTls } startTls)
                {
                    if (!await StartTlsAsync(startTls, conn, session, stop)) return;
                    continue;
                }
                await DispatchAsync(req, session, conn, stop);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or LdapProtocolException
                                       or SocketException or AuthenticationException)
        {
            // Client went away, idled out, sent an unframeable message or
            // failed the TLS handshake: nothing useful left to say to it.
        }
        finally
        {
            if (conn.Stream is SslStream ssl) await ssl.DisposeAsync();
        }
    }

    // RFC 4511 §4.14 / RFC 4513 §3: answer, then hand the socket to TLS. The
    // next bytes on the wire are the client's handshake. False means the
    // connection is finished.
    private async Task<bool> StartTlsAsync(LdapExtendedRequest e, Connection conn, Session session, CancellationToken ct)
    {
        Task Reply(int code, string message)
            => conn.WriteAsync(LdapCodec.Extended(e.MessageId, code, message, LdapOid.StartTls), ct);

        if (!tls.Configured)
        {
            await Reply(LdapResult.Unavailable, "TLS is not configured on this listener");
            return true;
        }
        if (conn.Secure)
        {
            await Reply(LdapResult.OperationsError, "TLS is already established");
            return true;
        }

        await Reply(LdapResult.Success, "");
        // Whatever was bound before was bound in the clear; start over anonymous,
        // and drop any paged search begun before the upgrade.
        session.Principal = LdapPrincipal.Anonymous;
        await session.SetPagedAsync(null);
        try
        {
            conn.Stream = await tls.AuthenticateAsync(conn.Stream, ct);
            return true;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            log.LogInformation("LDAP StartTLS from {Peer} failed: {Error}", conn.Peer, ex.Message);
            return false;
        }
    }

    private async Task DispatchAsync(LdapRequest req, Session session, Connection conn, CancellationToken ct)
    {
        var peer = conn.Peer;
        Task Send(byte[] message) => conn.WriteAsync(message, ct);

        // A connection bound long ago answers as that account only while the
        // account is still usable.
        if (req is not LdapBindRequest && session.Principal.Kind != PrincipalKind.Anonymous
            && DateTime.UtcNow - session.CheckedAt > RecheckBindEvery)
            await RecheckAsync(session, conn, ct);

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
                    await Send(await BindAsync(b, session, conn, ct));
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

    private async Task RecheckAsync(Session session, Connection conn, CancellationToken ct)
    {
        var p = session.Principal;
        var u = await directory.FindUserAsync(p.Shortname!, ct);
        var still = u is { IsUsable: true }
            && (p.Kind != PrincipalKind.Service || u.Type == Models.Enums.UserType.Bot);
        if (still)
        {
            session.CheckedAt = DateTime.UtcNow;
            return;
        }
        log.LogInformation("LDAP connection from {Peer}: {Shortname} is no longer usable; the connection is anonymous now",
            conn.Peer, p.Shortname);
        session.Principal = LdapPrincipal.Anonymous;
        await session.SetPagedAsync(null);
    }

    private static byte[] Unsupported(int messageId, int opTag, LdapControl control)
        => LdapCodec.Result(messageId, opTag, LdapResult.UnavailableCriticalExtension,
            $"critical control {control.Oid} is not supported");

    // ----- bind -----

    private async Task<byte[]> BindAsync(LdapBindRequest b, Session session, Connection conn, CancellationToken ct)
    {
        var peer = conn.Peer;
        session.Principal = LdapPrincipal.Anonymous;
        await session.SetPagedAsync(null);
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

        // With TLS available, a password over a cleartext connection is
        // refused unless it comes from one of the deployment's own clients.
        // It has already crossed the wire by now; refusing it is what stops a
        // misconfigured client from quietly depending on that.
        if (tls.Configured && !conn.Secure && !guard.IsTrusted(conn.Address))
            return Reply(LdapResult.ConfidentialityRequired, "use StartTLS or LDAPS to bind with a password");
        if (guard.IsThrottled(conn.Address))
        {
            log.LogInformation("LDAP bind from {Peer} as {Dn} refused: too many failed binds from this address", peer, b.Name);
            return Reply(LdapResult.Busy, "too many failed binds from this address; try again in a minute");
        }

        // An unsynced replica knows nobody yet; "invalid credentials" would
        // be a lie a mail client shows the user as a wrong password.
        if (!index.ReplicaSynced)
            return Reply(LdapResult.Unavailable, "this directory replica has not completed its first sync with the primary");
        if (index.ReplicaStale)
        {
            log.LogWarning("LDAP bind from {Peer} as {Dn} refused: this replica last synced at {SyncedAt:u}, over {Hours} h ago",
                peer, b.Name, index.ReplicaSyncedAt, index.ReplicaMaxStaleness.TotalHours);
            return Reply(LdapResult.Unavailable, "this directory replica has not reached its primary for too long to vouch for a password");
        }

        var principal = directory.ClassifyBindDn(b.Name);
        try
        {
            // The account the DN names, by its stored shortname: uid is
            // case-insensitive (uid=Alice binds as 'alice'). A service
            // account is a service only under ou=services; under ou=people it
            // names nobody, so its read-everything credential cannot pass as
            // a person's.
            var refusal = "no such account";
            if (principal.Kind == PrincipalKind.User)
            {
                var named = await directory.FindUserAsync(principal.Shortname!, ct);
                principal = named is null || directory.IsServiceAccount(named.Shortname)
                    ? LdapPrincipal.Anonymous
                    : new LdapPrincipal(PrincipalKind.User, named.Shortname);
            }
            // A service account reads every user; it binds only from the
            // deployment's own hosts.
            else if (principal.Kind == PrincipalKind.Service && !guard.IsTrusted(conn.Address))
            {
                principal = LdapPrincipal.Anonymous;
                refusal = "service accounts bind from LDAP_TRUSTED_PEERS only";
            }

            if (principal.Kind == PrincipalKind.Anonymous)
            {
                // A DN that names nobody costs what a wrong password costs.
                _ = await hasher.VerifyAsync(password, hasher.DecoyHash, ct);
                guard.RecordFailure(conn.Address);
                log.LogInformation("LDAP bind from {Peer} as {Dn} failed: {Reason}", peer, b.Name, refusal);
                return Reply(LdapResult.InvalidCredentials);
            }

            var shortname = principal.Shortname!;
            var repeated = guard.IsRepeatedFailure(shortname, password);
            var result = await userService.VerifyDirectoryBindAsync(shortname, password, countFailure: !repeated, ct);
            // A service account must be a bot: a person's account listed in
            // LDAP_SERVICE_ACCOUNTS by mistake would read the whole directory.
            if (result.User is { } svc && principal.Kind == PrincipalKind.Service
                && svc.Type != Models.Enums.UserType.Bot)
            {
                log.LogWarning("LDAP bind from {Peer} as {Dn} refused: {Shortname} is listed in LDAP_SERVICE_ACCOUNTS but is not a bot",
                    peer, b.Name, svc.Shortname);
                return Reply(LdapResult.InvalidCredentials);
            }
            if (result.User is null)
            {
                guard.RecordFailure(conn.Address);
                // Only a password that was checked and was wrong: one sent to a
                // locked or deactivated account proves nothing about staleness.
                if (result.WrongPassword) guard.RememberFailure(shortname, password);
                log.LogInformation("LDAP bind from {Peer} as {Dn} failed{Repeat}", peer, b.Name,
                    repeated ? " (the same password as a recent failure; not counted toward the lockout again)" : "");
                return Reply(LdapResult.InvalidCredentials);
            }
            guard.Forget(shortname);
        }
        catch (PasswordHashingCapacityException ex)
        {
            return Reply(LdapResult.Busy, $"password hashing is at capacity; retry in {ex.RetryAfterSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bind that errors is charged like a failed one, or a request
            // that reliably errors would be a free guess.
            guard.RecordFailure(conn.Address);
            throw;
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
        byte[] Done(int code, string message, string matchedDn = "", IReadOnlyList<LdapControl>? controls = null)
            => LdapCodec.Result(s.MessageId, LdapOp.SearchResultDone, code, message, matchedDn, controls);
        // The client's time limit, in seconds; 0 is none. Checked between
        // entries and at the scan's checkpoints (LdapDirectory.Search), so a
        // search that has run out of time ends with what it has sent.
        var started = _clock.GetTimestamp();
        bool OutOfTime() => s.TimeLimit > 0 && _clock.GetElapsedTime(started).TotalSeconds >= s.TimeLimit;

        if (pagedControl is null)
        {
            var max = settings.Value.LdapSizeLimit;
            var limit = s.SizeLimit > 0 ? Math.Min(s.SizeLimit, max) : max;
            var search = directory.Start(s, session.Principal);
            int? stoppedWith = null;
            var sent = 0;
            await using (var results = search.Results.GetAsyncEnumerator(ct))
            {
                while (await results.MoveNextAsync())
                {
                    if (OutOfTime())
                    {
                        stoppedWith = LdapResult.TimeLimitExceeded;
                        break;
                    }
                    if (results.Current is not { } entry)
                    {
                        if (!search.Budget.Exhausted) continue;
                        stoppedWith = LdapResult.AdminLimitExceeded;
                        break;
                    }
                    if (sent == limit)
                    {
                        stoppedWith = LdapResult.SizeLimitExceeded;
                        break;
                    }
                    await send(LdapCodec.SearchEntry(s.MessageId, entry, selection, s.TypesOnly));
                    sent++;
                }
            }
            await send(stoppedWith switch
            {
                LdapResult.AdminLimitExceeded => Done(LdapResult.AdminLimitExceeded,
                    $"examined {settings.Value.LdapMaxScan} rows; narrow the filter to an indexed attribute "
                    + "(uid, mail, mailAlias, mobile, authorizedService) or use paged results"),
                { } code => Done(code, ""),
                null => Done(search.Code, search.Message, search.MatchedDn),
            });
            return;
        }

        int size;
        byte[] cookie;
        try
        {
            (size, cookie) = LdapCodec.ReadPagedControl(pagedControl.Value, s.MessageId);
        }
        catch (LdapProtocolException ex)
        {
            log.LogInformation("LDAP search with a malformed paged results control: {Error}", ex.Message);
            await send(Done(LdapResult.ProtocolError, ex.Message));
            return;
        }
        PagedSearch paged;
        if (cookie.Length > 0)
        {
            if (session.Paged is not { } current || !CryptographicOperations.FixedTimeEquals(cookie, current.Cookie))
            {
                await send(Done(LdapResult.UnwillingToPerform, "unknown paged results cookie"));
                return;
            }
            if (!string.Equals(current.Request, RequestKey(s), StringComparison.Ordinal))
            {
                await send(Done(LdapResult.UnwillingToPerform, "a paged results continuation must repeat the search that began it"));
                return;
            }
            paged = current;
            if (size == 0)
            {
                // Page size 0 with a cookie abandons the paged search.
                await session.SetPagedAsync(null);
                await send(Done(LdapResult.Success, "", controls: [LdapCodec.PagedControl([])]));
                return;
            }
        }
        else
        {
            var search = directory.Start(s, session.Principal);
            paged = new PagedSearch(RandomNumberGenerator.GetBytes(16), RequestKey(s), search, search.Results.GetAsyncEnumerator(ct));
            await session.SetPagedAsync(paged);
        }

        // Each page gets a fresh scan budget. A page that runs out of budget
        // before it fills is sent short, with a cookie: RFC 2696 allows fewer
        // entries than asked for, and the client simply asks again.
        paged.Search.Budget.Reset(settings.Value.LdapMaxScan);
        var pageSize = size == 0 ? int.MaxValue : size;
        var count = 0;
        var finished = false;
        while (count < pageSize)
        {
            if (!await paged.Results.MoveNextAsync())
            {
                finished = true;
                break;
            }
            if (OutOfTime())
            {
                // A time limit ends the search outright, as on slapd: no
                // cookie, and the paged state is dropped.
                await session.SetPagedAsync(null);
                await send(Done(LdapResult.TimeLimitExceeded, "", controls: [LdapCodec.PagedControl([])]));
                return;
            }
            if (paged.Results.Current is not { } entry)
            {
                if (paged.Search.Budget.Exhausted) break;
                continue;
            }
            await send(LdapCodec.SearchEntry(s.MessageId, entry, selection, s.TypesOnly));
            count++;
        }

        if (!finished)
        {
            await send(Done(LdapResult.Success, "", controls: [LdapCodec.PagedControl(paged.Cookie)]));
            return;
        }
        await session.SetPagedAsync(null);
        await send(Done(paged.Search.Code, paged.Search.Message, paged.Search.MatchedDn,
            [LdapCodec.PagedControl([])]));
    }

    // ----- extended -----

    private byte[] Extended(LdapExtendedRequest e, Session session) => e.Name switch
    {
        LdapOid.WhoAmI => LdapCodec.Extended(e.MessageId, LdapResult.Success, "", responseValue:
            Encoding.UTF8.GetBytes(session.Principal.Kind == PrincipalKind.Anonymous
                ? "" : "dn:" + directory.DnOf(session.Principal))),
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
