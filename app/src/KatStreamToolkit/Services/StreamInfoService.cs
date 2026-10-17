using System.Text;
using System.Text.Json;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit.Services;

// Title/category sync, Twitch first: reads and writes the logged-in account's
// channel metadata through Helix (channel:manage:broadcast - already granted by
// the standard login; logins made before that scope landed need one re-login).
// Title/category can only be set by the channel OWNER (a moderator token is not
// enough), so every call targets auth.UserId. Kick/YouTube equivalents wait for
// their own OAuth logins.
public static class StreamInfoService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record ChannelInfo(string Title, string GameName, string GameId);
    public sealed record CategoryResult(string Id, string Name);

    // The logged-in account's current channel metadata (what the dashboard shows).
    public static async Task<ChannelInfo?> FetchCurrentAsync(TwitchAuthData auth, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ModerationService.Helix}/channels?broadcaster_id={auth.UserId}");
        using var resp = await SendAsync(req, auth, ct);
        if (resp == null || !resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;
        var c = data[0];
        return new ChannelInfo(
            c.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            c.TryGetProperty("game_name", out var g) ? g.GetString() ?? "" : "",
            c.TryGetProperty("game_id", out var gi) ? gi.GetString() ?? "" : "");
    }

    public static async Task<List<CategoryResult>> SearchCategoriesAsync(TwitchAuthData auth, string query, CancellationToken ct = default)
    {
        var list = new List<CategoryResult>();
        if (string.IsNullOrWhiteSpace(query)) return list;
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ModerationService.Helix}/search/categories?query={Uri.EscapeDataString(query.Trim())}&first=8");
        using var resp = await SendAsync(req, auth, ct);
        if (resp == null || !resp.IsSuccessStatusCode) return list;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (id.Length > 0 && name.Length > 0)
                    list.Add(new CategoryResult(id, name));
            }
        }
        return list;
    }

    // Applies title and/or category to the logged-in account's channel. Either
    // argument may be null = leave that field untouched. Returns null on
    // success, otherwise a readable error for the status line.
    public static async Task<string?> ApplyAsync(TwitchAuthData auth, string? title, string? gameId, CancellationToken ct = default)
    {
        if (title == null && gameId == null)
            return "nothing to change - type a title or pick a category first";
        var body = new Dictionary<string, object>();
        if (title != null) body["title"] = title;
        if (gameId != null) body["game_id"] = gameId;
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{ModerationService.Helix}/channels?broadcaster_id={auth.UserId}")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, auth, ct);
        if (resp == null) return "no Twitch connection - log in on the Chat Overlays tab";
        if (resp.IsSuccessStatusCode) return null;
        string detail = "";
        try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { }
        return (int)resp.StatusCode switch
        {
            401 => "the Twitch login expired - log in again (Chat Overlays tab)",
            403 => "the logged-in account is not the channel owner - Twitch only lets the broadcaster set title/category",
            404 => "Twitch rejected the category - search again and pick a suggestion",
            _ => $"apply failed ({(int)resp.StatusCode}): {detail}",
        };
    }

    // Same plumbing as moderation: Client-Id + Bearer headers, null response
    // when the login or Client ID is missing.
    private static Task<HttpResponseMessage?> SendAsync(HttpRequestMessage req, TwitchAuthData auth, CancellationToken ct)
        => ModerationService.SendAsync(req, auth, ct);
}
