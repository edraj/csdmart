using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Dmart.Services;

// Port of dmart/websocket.py::ConnectionManager. Tracks connected WebSocket
// clients by user shortname and manages channel subscriptions for real-time
// notifications. The realtime_updates_notifier plugin POSTs to
// /broadcast-to-channels which fans out to subscribed clients.
public sealed class WsConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    // Channels are protected by _channelsLock on all writes because subscribe,
    // unsubscribe, and disconnect all need to walk and mutate multiple keys
    // atomically — a ConcurrentBag replacement pattern isn't enough.
    private readonly Dictionary<string, HashSet<string>> _channels = new();
    private readonly object _channelsLock = new();

    public async Task ConnectAsync(WebSocket ws, string userShortname)
    {
        // Replace any existing connection for this user (Python does the same).
        if (_connections.TryRemove(userShortname, out var old))
        {
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { /* already closed */ }
        }
        _connections[userShortname] = ws;
    }

    // Identity-aware disconnect. The caller is WebSocketHandler's `finally`,
    // which runs on the socket whose read loop just ended — and that may be a
    // STALE socket whose entry ConnectAsync has already replaced with the
    // user's new one (reconnect while the old half-open socket is still
    // unwinding). An unconditional TryRemove(key) there would unregister the
    // LIVE connection and the client would silently stop receiving. The
    // KeyValuePair overload is an atomic compare-and-remove: it only removes
    // when the stored socket is still this exact instance.
    public void Disconnect(string userShortname, WebSocket ws)
    {
        // Same reason the subscriptions only go when the removal won: they
        // belong to whichever socket currently owns the entry.
        if (_connections.TryRemove(new KeyValuePair<string, WebSocket>(userShortname, ws)))
            RemoveAllSubscriptions(userShortname);
    }

    // Upper bound on a single send. A client that stops reading (TCP
    // zero-window) never completes SendAsync, so an untimed send pins the
    // calling task forever — and on the broadcast path it used to pin the
    // whole fan-out. Dropping a peer that can't drain 5s worth of backlog is
    // safe: it reconnects and re-subscribes.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public Task<bool> SendMessageAsync(string userShortname, string message)
        => SendMessageAsync(userShortname, Encoding.UTF8.GetBytes(message));

    private async Task<bool> SendMessageAsync(string userShortname, byte[] payload)
    {
        if (!_connections.TryGetValue(userShortname, out var ws)) return false;
        if (ws.State != WebSocketState.Open) return false;
        using var cts = new CancellationTokenSource(SendTimeout);
        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Timed out — the peer is wedged. Abort() rather than CloseAsync()
            // because a graceful close writes a frame down the same stalled
            // socket and would block just as long. Unregister so later
            // broadcasts skip it instead of paying the timeout again.
            try { ws.Abort(); } catch { /* already torn down */ }
            Disconnect(userShortname, ws);
            return false;
        }
        catch { return false; }
    }

    public async Task<bool> BroadcastToChannelAsync(string channelName, string message)
    {
        // Snapshot subscribers under the lock so the iteration below doesn't race
        // with concurrent subscribe/unsubscribe calls.
        string[] users;
        lock (_channelsLock)
        {
            if (!_channels.TryGetValue(channelName, out var set)) return false;
            users = set.ToArray();
        }
        // Encode once per broadcast rather than once per subscriber — every
        // recipient on the channel gets byte-identical bytes.
        var payload = Encoding.UTF8.GetBytes(message);
        // Fan out concurrently: awaited one at a time, a single slow (or
        // timing-out) recipient delays everyone queued behind it.
        var sends = new Task<bool>[users.Length];
        for (var i = 0; i < users.Length; i++)
            sends[i] = SendMessageAsync(users[i], payload);
        var results = await Task.WhenAll(sends);
        return results.Any(r => r);
    }

    public void Subscribe(string userShortname, string channelName)
    {
        lock (_channelsLock)
        {
            RemoveAllSubscriptionsLocked(userShortname);
            if (!_channels.TryGetValue(channelName, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _channels[channelName] = set;
            }
            set.Add(userShortname);
        }
    }

    public void RemoveAllSubscriptions(string userShortname)
    {
        lock (_channelsLock)
        {
            RemoveAllSubscriptionsLocked(userShortname);
        }
    }

    // Caller must hold _channelsLock.
    private void RemoveAllSubscriptionsLocked(string userShortname)
    {
        // Collect empty channels so we can delete them after the walk — modifying
        // the dictionary inside the foreach would throw.
        List<string>? empties = null;
        foreach (var (key, users) in _channels)
        {
            if (users.Remove(userShortname) && users.Count == 0)
                (empties ??= new List<string>()).Add(key);
        }
        if (empties is not null)
            foreach (var key in empties) _channels.Remove(key);
    }

    // Python's generate_channel_name: "space:subpath:schema:action:state"
    public static string? GenerateChannelName(JsonElement msg)
    {
        if (!msg.TryGetProperty("space_name", out var sn) || !msg.TryGetProperty("subpath", out var sp))
            return null;
        var schema = msg.TryGetProperty("schema_shortname", out var ss) ? ss.GetString() ?? "__ALL__" : "__ALL__";
        var action = msg.TryGetProperty("action_type", out var at) ? at.GetString() ?? "__ALL__" : "__ALL__";
        var state = msg.TryGetProperty("ticket_state", out var ts) ? ts.GetString() ?? "__ALL__" : "__ALL__";
        return $"{sn.GetString()}:{sp.GetString()}:{schema}:{action}:{state}";
    }

    public int ConnectionCount => _connections.Count;

    // Returns a snapshot of (channel → subscribers) for read-only callers
    // (WebSocketHandler's /ws-info). Copied under the lock so iteration by the
    // caller can't race with mutations.
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Channels
    {
        get
        {
            lock (_channelsLock)
            {
                return _channels.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyCollection<string>)kv.Value.ToArray());
            }
        }
    }
}
