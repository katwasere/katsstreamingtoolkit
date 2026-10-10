using System.Text;
using System.Text.Json;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit.Services;

// Twitch moderation through the Helix API, using the logged-in account's token
// (works when that account is the broadcaster or a moderator of the channel).
// Every method returns null on success or a readable error string - callers
// print it straight into the overlay.
public static class ModerationService
{
    private const string Helix = "https://api.twitch.tv/helix";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, string> BroadcasterIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string?> ResolveBroadcasterIdAsync(string login, CancellationToken ct = default)
    {
        login = login.Trim().TrimStart('#').ToLowerInvariant();
        await Gate.WaitAsync(ct);
        try
        {
            if (BroadcasterIds.TryGetValue(login, out var cached))
                return cached;
        }
        finally { Gate.Release(); }

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{Helix}/users?login={Uri.EscapeDataString(login)}");
        using var resp = await SendAsync(req, ct);
        if (resp == null) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;
        string id = data[0].TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (id.Length == 0) return null;
        await Gate.WaitAsync(ct);
        try { BroadcasterIds[login] = id; } finally { Gate.Release(); }
        return id;
    }

    public static Task<string?> TimeoutAsync(TwitchAuthData auth, string broadcasterId,
        string targetUserId, int seconds, string reason, CancellationToken ct = default) =>
        BanOrTimeoutAsync(auth, broadcasterId, targetUserId, seconds, reason, ct);

    public static Task<string?> BanAsync(TwitchAuthData auth, string broadcasterId,
        string targetUserId, string reason, CancellationToken ct = default) =>
        BanOrTimeoutAsync(auth, broadcasterId, targetUserId, null, reason, ct);

    private static async Task<string?> BanOrTimeoutAsync(TwitchAuthData auth, string broadcasterId,
        string targetUserId, int? seconds, string reason, CancellationToken ct)
    {
        var data = new Dictionary<string, object> { ["user_id"] = targetUserId };
        if (seconds is int d) data["duration"] = d;
        if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason.Trim();
        var body = JsonSerializer.Serialize(new { data });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{Helix}/moderation/bans?broadcaster_id={broadcasterId}&moderator_id={auth.UserId}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct);
        return await DescribeAsync(resp, "timeout/ban");
    }

    public static async Task<string?> UnbanAsync(TwitchAuthData auth, string broadcasterId,
        string targetUserId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{Helix}/moderation/bans?broadcaster_id={broadcasterId}&moderator_id={auth.UserId}&user_id={targetUserId}");
        using var resp = await SendAsync(req, ct);
        return await DescribeAsync(resp, "unban");
    }

    public static async Task<string?> DeleteMessageAsync(TwitchAuthData auth, string broadcasterId,
        string messageId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{Helix}/moderation/chat?broadcaster_id={broadcasterId}&moderator_id={auth.UserId}&message_id={Uri.EscapeDataString(messageId)}");
        using var resp = await SendAsync(req, ct);
        return await DescribeAsync(resp, "delete");
    }

    public static async Task<string?> SetSlowModeAsync(TwitchAuthData auth, string broadcasterId,
        bool on, int waitSeconds = 30, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            slow_mode = on,
            slow_mode_wait_time = Math.Clamp(waitSeconds, 3, 120),
        });
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{Helix}/chat/settings?broadcaster_id={broadcasterId}&moderator_id={auth.UserId}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct);
        return await DescribeAsync(resp, "slow mode");
    }

    // Shared request plumbing: auth headers + a readable error for the common
    // failure cases (expired token, not a moderator, target user gone).
    private static async Task<HttpResponseMessage?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var auth = ChatAuthStore.Twitch;
        if (auth == null) return null;
        req.Headers.Add("Client-Id", ClientId);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        try
        {
            return await Http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    // The Twitch application Client ID (not a secret); MainViewModel keeps this
    // in sync with the config field.
    public static string? ClientId { get; set; }

    private static async Task<string?> DescribeAsync(HttpResponseMessage? resp, string action)
    {
        if (resp == null) return $"no Twitch connection for {action}";
        if (resp.IsSuccessStatusCode) return null;
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(); } catch { }
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                detail = m.GetString() ?? body;
        }
        catch { }
        return resp.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                $"{action}: the Twitch login expired - log in again (Chat Overlays tab)",
            System.Net.HttpStatusCode.Forbidden =>
                $"{action}: this account is not a moderator of the channel",
            System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.BadRequest =>
                $"{action}: {detail}",
            _ => $"{action} failed ({(int)resp.StatusCode}): {detail}",
        };
    }
}
