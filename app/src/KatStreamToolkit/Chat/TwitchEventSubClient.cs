using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KatStreamToolkit.Services;

namespace KatStreamToolkit.Chat;

// Twitch EventSub over websocket: follows, subs, resubs and raids as
// AlertEvents. One connection hosts the subscriptions for every watched
// channel. Reuses the account login from ChatAuthStore (the same OAuth the
// moderation work introduced) - the connection restarts itself when auth
// changes, drops, or Twitch asks for a reconnect.
public sealed class TwitchEventSubClient : IDisposable
{
    private const string WsUrl = "wss://eventsub.wss.twitch.tv/ws";

    private readonly object _gate = new();
    private readonly HashSet<string> _desired = new(StringComparer.OrdinalIgnoreCase);
    // login -> subscription ids created for the CURRENT session; used to
    // remove subscriptions when a channel stops being watched.
    private readonly Dictionary<string, List<string>> _active = new(StringComparer.OrdinalIgnoreCase);
    // Capped at one: a pulse means "wake now", never "wake N times" - queued
    // pulses would each skip the reconnect backoff and hammer Twitch with one
    // real reconnect attempt apiece.
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _sessionId;

    // Raised on background threads; the subscriber marshals to the UI.
    public event Action<AlertEvent>? Alert;
    public event Action<string>? Status;

    public void SetChannels(IEnumerable<string> logins)
    {
        var set = logins
            .Select(c => c.Trim().TrimStart('#').ToLowerInvariant())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed;
        lock (_gate)
        {
            changed = !set.SetEquals(_desired);
            if (changed)
            {
                var removed = _desired.Except(set).ToList();
                _desired.Clear();
                foreach (var c in set) _desired.Add(c);
                foreach (var login in removed) RemoveSubscriptionsFor(login);
            }
        }
        if (changed) Wake();
    }

    // Auth changed / settings changed - wake the loop so it retries promptly.
    public void Nudge() => Wake();

    // Starts the loop if needed and wakes it. This is the only place the loop
    // is started; without it the client would pulse a semaphore no thread ever
    // waits on and the whole alert feature would stay inert while the local
    // test buttons (which bypass this client) still "work".
    private void Wake()
    {
        EnsureLoop();
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    // Wait for the given time OR a pulse; true means pulsed (restart promptly,
    // backoff reset), false means the timeout elapsed.
    private async Task<bool> WaitOrPulseAsync(TimeSpan time, CancellationToken ct)
    {
        try { return await _wake.WaitAsync(time, ct); }
        catch (OperationCanceledException) { return false; }
    }

    private void EnsureLoop()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts ??= new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int backoff = 0;
        while (!ct.IsCancellationRequested)
        {
            var auth = ChatAuthStore.Twitch;
            if (auth == null)
            {
                Status?.Invoke("alerts waiting: log in to Twitch (Chat Overlays tab)");
                if (await WaitOrPulseAsync(TimeSpan.FromSeconds(15), ct)) backoff = 0;
                continue;
            }
            if (string.IsNullOrWhiteSpace(ModerationService.ClientId))
            {
                Status?.Invoke("alerts waiting: paste the Twitch app Client ID (Chat Overlays tab)");
                if (await WaitOrPulseAsync(TimeSpan.FromSeconds(15), ct)) backoff = 0;
                continue;
            }

            try
            {
                await ConnectAndRunAsync(auth, ct);
                if (ct.IsCancellationRequested) break;
                Status?.Invoke("alerts: connection closed - reconnecting");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"alerts: {ex.Message} - reconnecting");
            }
            backoff = backoff == 0 ? 5 : Math.Min(backoff * 2, 60);
            if (await WaitOrPulseAsync(TimeSpan.FromSeconds(backoff), ct)) backoff = 0;
        }
    }

    private async Task ConnectAndRunAsync(TwitchAuthData auth, CancellationToken ct)
    {
        _sessionId = null;
        lock (_gate) _active.Clear();
        using var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri(WsUrl), ct);

        bool subscribedForSession = false;
        bool subscriptionsFatal = false;
        var subscriptionsTask = Task.CompletedTask;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Twitch keepalives arrive every few seconds; 45s of silence means
            // the connection is a black hole - treat it as a drop.
            using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recvCts.CancelAfter(TimeSpan.FromSeconds(45));
            string message;
            try
            {
                message = await ReceiveAsync(ws, recvCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("no data from Twitch (keepalive timeout)");
            }
            if (message.Length == 0) continue;

            // One parse per frame; every handler below reads from it.
            JsonDocument doc;
            try { doc = JsonDocument.Parse(message); }
            catch { continue; } // a malformed frame must never kill the loop
            using (doc)
            {
                string? type = Prop(doc.RootElement, "metadata", "message_type");
                switch (type)
                {
                    case "session_welcome":
                        _sessionId = Prop(doc.RootElement, "payload", "session", "id");
                        subscribedForSession = false;
                        var authNow = ChatAuthStore.Twitch;
                        var sessionId = _sessionId;
                        if (authNow != null && sessionId != null && !subscribedForSession)
                        {
                            subscribedForSession = true;
                            // Created off the receive loop so slow HTTP round
                            // trips and per-channel id resolution never starve
                            // the keepalives. The session id is captured: a
                            // reconnect mid-setup can never create
                            // subscriptions against a stale or empty session.
                            subscriptionsTask = Task.Run(async () =>
                            {
                                try { subscriptionsFatal = !await CreateSubscriptionsAsync(authNow, sessionId, ct); }
                                catch (Exception ex)
                                {
                                    subscriptionsFatal = true;
                                    Status?.Invoke($"alerts: subscription setup failed ({ex.Message})");
                                }
                                if (subscriptionsFatal)
                                {
                                    // Only a dead login aborts the session: the
                                    // outer loop waits on its backoff and any
                                    // Nudge (the re-login) retries immediately.
                                    try { ws.Abort(); } catch { }
                                }
                            }, ct);
                        }
                        Status?.Invoke("alerts: connected to Twitch");
                        break;

                    case "notification":
                        HandleNotification(doc.RootElement);
                        break;

                    case "session_reconnect":
                        // Twitch moves the socket; a clean reconnect re-creates
                        // the subscriptions on the new session - a few seconds
                        // gap.
                        Status?.Invoke("alerts: Twitch asked for a reconnect");
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None); } catch { }
                        return;

                    case "revocation":
                        string subType = Prop(doc.RootElement, "payload", "subscription", "type") ?? "subscription";
                        Status?.Invoke($"alerts: Twitch revoked {subType} - usually a login change; log out and back in if alerts stop");
                        break;
                }
            }
        }
        try { await subscriptionsTask; } catch { }
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // BUG-73: decoding each chunk separately corrupted any multi-byte UTF-8
        // sequence split at the 16 KB boundary into U+FFFD (alert text). The
        // bytes accumulate and are decoded once at EndOfMessage.
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                return "";
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    // ---------- subscriptions ----------

    // Creates every subscription for the desired channels. Returns false only
    // for a dead login (401): per-type failures (missing scopes on an old
    // token, not a moderator of that channel, dead ingest) are reported and
    // skipped so raids - which need no scope - and the other channels keep
    // working.
    private async Task<bool> CreateSubscriptionsAsync(TwitchAuthData auth, string sessionId, CancellationToken ct)
    {
        string[] logins;
        lock (_gate) logins = _desired.ToArray();
        if (logins.Length == 0)
        {
            Status?.Invoke("alerts: connected - no Twitch channel configured");
            return true;
        }

        foreach (var login in logins)
        {
            // A reconnect while this setup runs hands the session to a newer
            // task - abandon the stale one silently.
            if (ct.IsCancellationRequested || _sessionId != sessionId) return true;
            string? broadcasterId = await ModerationService.ResolveBroadcasterIdAsync(login, ct);
            if (broadcasterId == null)
            {
                Status?.Invoke($"alerts: could not resolve the channel id for '{login}'");
                continue;
            }

            var wanted = new (string type, string version, object condition)[]
            {
                ("channel.follow", "2", new { broadcaster_user_id = broadcasterId, moderator_user_id = auth.UserId }),
                ("channel.subscribe", "1", (object)new { broadcaster_user_id = broadcasterId }),
                ("channel.subscription.message", "1", (object)new { broadcaster_user_id = broadcasterId }),
                ("channel.raid", "1", (object)new { to_broadcaster_user_id = broadcasterId }),
            };
            var ids = new List<string>();
            foreach (var (type, version, condition) in wanted)
            {
                if (ct.IsCancellationRequested || _sessionId != sessionId) return true;
                var (result, error) = await SubscribeOnceAsync(auth, sessionId, type, version, condition, ids, ct);
                if (result == SubResult.Fatal)
                {
                    Status?.Invoke($"alerts: {error}");
                    return false;
                }
                if (result == SubResult.Skipped)
                    Status?.Invoke($"alerts: {type} skipped for '{login}' - {error}");
            }
            lock (_gate) _active[login] = ids;
            Status?.Invoke($"alerts: watching '{login}'");
        }
        return true;
    }

    private enum SubResult { Created, Skipped, Fatal }

    // One subscription POST. 409 (already subscribed) deletes the stale one
    // from a dead session and retries once, so a fast reconnect can never
    // strand alerts on it.
    private async Task<(SubResult Result, string? Error)> SubscribeOnceAsync(TwitchAuthData auth, string sessionId,
        string type, string version, object condition, List<string> ids, CancellationToken ct, bool retry = true)
    {
        var body = JsonSerializer.Serialize(new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{ModerationService.Helix}/eventsub/subscriptions")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await ModerationService.SendAsync(req, auth, ct);
        if (resp == null) return (SubResult.Skipped, "no connection to Twitch");
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                    data[0].TryGetProperty("id", out var idEl))
                    ids.Add(idEl.GetString() ?? "");
            }
            catch { }
            return (SubResult.Created, null);
        }
        if ((int)resp.StatusCode == 409 && retry)
        {
            string? conflictId = await FindExistingSubscriptionIdAsync(auth, type, condition, ct);
            if (conflictId != null)
            {
                using var del = new HttpRequestMessage(HttpMethod.Delete,
                    $"{ModerationService.Helix}/eventsub/subscriptions?id={Uri.EscapeDataString(conflictId)}");
                using var delResp = await ModerationService.SendAsync(del, auth, ct);
                if (delResp is { IsSuccessStatusCode: true })
                    return await SubscribeOnceAsync(auth, sessionId, type, version, condition, ids, ct, retry: false);
            }
            return (SubResult.Created, null); // treat as subscribed - the existing one covers us
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return (SubResult.Fatal, "the Twitch login expired - log in again (Chat Overlays tab)");
        string error = await ModerationService.DescribeAsync(resp, $"{type} subscription")
                       ?? $"{type} subscription failed ({(int)resp.StatusCode})";
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            error += " - if this login predates the alerts feature, log out and back in once to grant the new permissions";
        return (SubResult.Skipped, error);
    }

    private static async Task<string?> FindExistingSubscriptionIdAsync(TwitchAuthData auth, string type,
        object condition, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ModerationService.Helix}/eventsub/subscriptions?first=100");
            using var resp = await ModerationService.SendAsync(req, auth, ct);
            if (resp is not { IsSuccessStatusCode: true }) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string conditionJson = JsonSerializer.Serialize(condition);
            foreach (var sub in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (sub.TryGetProperty("type", out var t) && t.GetString() == type &&
                    sub.TryGetProperty("condition", out var cond) &&
                    JsonSerializer.Serialize(cond) == conditionJson &&
                    sub.TryGetProperty("id", out var idEl))
                    return idEl.GetString();
            }
        }
        catch { }
        return null;
    }

    private void RemoveSubscriptionsFor(string login)
    {
        // Best effort, from wherever we are called: deletes only matter while
        // a session is live; dead sessions are cleaned up by Twitch anyway.
        string[] ids;
        lock (_gate)
        {
            if (!_active.Remove(login, out var list)) return;
            ids = list.ToArray();
        }
        var auth = ChatAuthStore.Twitch;
        if (auth == null) return;
        _ = Task.Run(async () =>
        {
            foreach (var id in ids.Where(i => i.Length > 0))
            {
                try
                {
                    using var del = new HttpRequestMessage(HttpMethod.Delete,
                        $"{ModerationService.Helix}/eventsub/subscriptions?id={Uri.EscapeDataString(id)}");
                    using var resp = await ModerationService.SendAsync(del, auth, CancellationToken.None);
                }
                catch { }
            }
        });
    }

    // ---------- notifications ----------

    private void HandleNotification(JsonElement root)
    {
        try
        {
            string subType = Prop(root, "metadata", "subscription_type") ?? "";
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("event", out var ev))
                return;
            string Name(string loginProp, string displayProp) =>
                ev.TryGetProperty(displayProp, out var d) && d.GetString() is { Length: > 0 } s ? s :
                ev.TryGetProperty(loginProp, out var l) ? l.GetString() ?? "" : "";

            AlertEvent? alert = subType switch
            {
                "channel.follow" => new AlertEvent
                {
                    Platform = "Twitch",
                    Type = AlertType.Follow,
                    User = Name("user_login", "user_name"),
                },
                "channel.subscribe" => new AlertEvent
                {
                    Platform = "Twitch",
                    Type = AlertType.Sub,
                    User = Name("user_login", "user_name"),
                    Tier = ev.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
                    IsGift = ev.TryGetProperty("is_gift", out var gift) && gift.ValueKind == JsonValueKind.True,
                },
                "channel.subscription.message" => new AlertEvent
                {
                    Platform = "Twitch",
                    Type = AlertType.Sub,
                    User = Name("user_login", "user_name"),
                    Tier = ev.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
                    Months = ev.TryGetProperty("cumulative_months", out var months) && months.TryGetInt32(out int m) ? m : 0,
                    Message = ev.TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("text", out var text)
                        ? text.GetString()
                        : null,
                },
                "channel.raid" => new AlertEvent
                {
                    Platform = "Twitch",
                    Type = AlertType.Raid,
                    User = Name("from_login", "from_name"),
                    Viewers = ev.TryGetProperty("viewers", out var viewers) && viewers.TryGetInt32(out int v) ? v : 0,
                },
                _ => null,
            };
            if (alert != null && alert.User.Length > 0)
                Alert?.Invoke(alert);
        }
        catch
        {
            // A malformed frame must never kill the connection loop.
        }
    }

    // Walks a parsed JSON path; null when any step is missing or not a string.
    private static string? Prop(JsonElement node, params string[] path)
    {
        foreach (var part in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return null;
        }
        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _ws = null;
    }
}
