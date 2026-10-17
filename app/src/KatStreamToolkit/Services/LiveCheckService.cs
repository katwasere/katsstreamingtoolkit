using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

public enum LiveState
{
    Unknown,
    Live,
    Offline,
}

// One live-check outcome: the state plus a viewer count where the platform's
// public API hands one out (Twitch/Kick/TikTok do; YouTube has no free path).
// Viewers is null whenever it cannot be known - never zero-faked.
public sealed record LiveCheckResult(LiveState State, int? Viewers)
{
    public static readonly LiveCheckResult Unknown = new(LiveState.Unknown, null);
    public static readonly LiveCheckResult Offline = new(LiveState.Offline, null);
}

// Phase-one "per-destination live checks": the relay only sees its own pushes -
// whether a platform actually SHOWS the stream has to be asked on the platform
// side. All checks are read-only, need no login, and degrade to Unknown on any
// failure (never throw past CheckAsync's callers except cancellation).
public static partial class LiveCheckService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly HttpClient BrowserHttp = CreateBrowserHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KatStreamToolkit/0.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static HttpClient CreateBrowserHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public static async Task<LiveState> CheckAsync(Platform platform, string handle, CancellationToken ct) =>
        (await CheckDetailedAsync(platform, handle, ct)).State;

    public static Task<LiveCheckResult> CheckDetailedAsync(Platform platform, string handle, CancellationToken ct) =>
        platform switch
        {
            Platform.Twitch => CheckTwitch(handle, ct),
            Platform.Kick => CheckKick(handle, ct),
            Platform.YouTube => CheckYouTube(handle, ct),
            Platform.TikTok => CheckTikTok(handle, ct),
            _ => Task.FromResult(LiveCheckResult.Unknown),
        };

    // Twitch: anonymous GraphQL with the public web client id - the same query
    // style the Twitch web app itself uses; no account or OAuth needed.
    private static async Task<LiveCheckResult> CheckTwitch(string login, CancellationToken ct)
    {
        try
        {
            login = login.Trim().TrimStart('#').ToLowerInvariant();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
            req.Headers.TryAddWithoutValidation("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    query = "query($l:String!){user(login:$l){stream{id viewers}}}",
                    variables = new { l = login },
                }), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return LiveCheckResult.Unknown;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return LiveCheckResult.Unknown;
            if (!data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                return LiveCheckResult.Offline;
            if (!user.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
                return LiveCheckResult.Offline;
            int? viewers = stream.TryGetProperty("viewers", out var v) && v.TryGetInt32(out int n) && n >= 0 ? n : null;
            return new LiveCheckResult(LiveState.Live, viewers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveCheckResult.Unknown;
        }
    }

    // Kick: the same public channel endpoint the chat client uses for room ids.
    private static async Task<LiveCheckResult> CheckKick(string slug, CancellationToken ct)
    {
        try
        {
            slug = slug.Trim().TrimStart('/').ToLowerInvariant();
            using var resp = await Http.GetAsync(
                $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(slug)}", ct);
            if (!resp.IsSuccessStatusCode)
                return LiveCheckResult.Unknown;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("livestream", out var ls))
                return LiveCheckResult.Unknown;
            if (ls.ValueKind != JsonValueKind.Object)
                return LiveCheckResult.Offline;
            int? viewers = ls.TryGetProperty("viewer_count", out var v) && v.TryGetInt32(out int n) && n >= 0 ? n : null;
            return new LiveCheckResult(LiveState.Live, viewers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveCheckResult.Unknown;
        }
    }

    // YouTube: a channel resolves to a live video id exactly when it is live -
    // the chat client uses the same resolver. Viewer counts need an API key
    // (no free login-free path), so they stay unknown here.
    private static async Task<LiveCheckResult> CheckYouTube(string input, CancellationToken ct)
    {
        try
        {
            string? videoId = await YouTubeChatClient.ResolveVideoId(input, ct);
            return videoId != null ? new LiveCheckResult(LiveState.Live, null) : LiveCheckResult.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveCheckResult.Unknown;
        }
    }

    // TikTok: the live page embeds the room info JSON; status 2 = live, 4 = ended.
    // TikTok blocks non-browser clients aggressively, so failures land on Unknown.
    [GeneratedRegex(@"""roomInfo"":\{""status"":\s*(\d+)")]
    private static partial Regex RoomStatusRegex();

    [GeneratedRegex(@"""userCount"":(\d+)")]
    private static partial Regex UserCountRegex();

    private static async Task<LiveCheckResult> CheckTikTok(string handle, CancellationToken ct)
    {
        try
        {
            handle = handle.Trim().TrimStart('@').ToLowerInvariant();
            using var resp = await BrowserHttp.GetAsync(
                $"https://www.tiktok.com/@{Uri.EscapeDataString(handle)}/live",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return LiveCheckResult.Unknown;
            string html = await resp.Content.ReadAsStringAsync(ct);
            var m = RoomStatusRegex().Match(html);
            if (!m.Success)
                return LiveCheckResult.Unknown;
            if (m.Groups[1].Value != "2")
                return LiveCheckResult.Offline;
            var u = UserCountRegex().Match(html);
            int? viewers = u.Success && int.TryParse(u.Groups[1].Value, out int n) && n >= 0 ? n : null;
            return new LiveCheckResult(LiveState.Live, viewers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveCheckResult.Unknown;
        }
    }
}
