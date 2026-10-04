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
        platform switch
        {
            Platform.Twitch => await CheckTwitch(handle, ct),
            Platform.Kick => await CheckKick(handle, ct),
            Platform.YouTube => await CheckYouTube(handle, ct),
            Platform.TikTok => await CheckTikTok(handle, ct),
            _ => LiveState.Unknown,
        };

    // Twitch: anonymous GraphQL with the public web client id - the same query
    // style the Twitch web app itself uses; no account or OAuth needed.
    private static async Task<LiveState> CheckTwitch(string login, CancellationToken ct)
    {
        try
        {
            login = login.Trim().TrimStart('#').ToLowerInvariant();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
            req.Headers.TryAddWithoutValidation("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    query = "query($l:String!){user(login:$l){stream{id}}}",
                    variables = new { l = login },
                }), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return LiveState.Unknown;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return LiveState.Unknown;
            if (!data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                return LiveState.Offline;
            return user.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object
                ? LiveState.Live
                : LiveState.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveState.Unknown;
        }
    }

    // Kick: the same public channel endpoint the chat client uses for room ids.
    private static async Task<LiveState> CheckKick(string slug, CancellationToken ct)
    {
        try
        {
            slug = slug.Trim().TrimStart('/').ToLowerInvariant();
            using var resp = await Http.GetAsync(
                $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(slug)}", ct);
            if (!resp.IsSuccessStatusCode)
                return LiveState.Unknown;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("livestream", out var ls))
                return LiveState.Unknown;
            return ls.ValueKind == JsonValueKind.Object ? LiveState.Live : LiveState.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveState.Unknown;
        }
    }

    // YouTube: a channel resolves to a live video id exactly when it is live -
    // the chat client uses the same resolver.
    private static async Task<LiveState> CheckYouTube(string input, CancellationToken ct)
    {
        try
        {
            string? videoId = await YouTubeChatClient.ResolveVideoId(input, ct);
            return videoId != null ? LiveState.Live : LiveState.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveState.Unknown;
        }
    }

    // TikTok: the live page embeds the room info JSON; status 2 = live, 4 = ended.
    // TikTok blocks non-browser clients aggressively, so failures land on Unknown.
    [GeneratedRegex(@"""roomInfo"":\{""status"":\s*(\d+)")]
    private static partial Regex RoomStatusRegex();

    private static async Task<LiveState> CheckTikTok(string handle, CancellationToken ct)
    {
        try
        {
            handle = handle.Trim().TrimStart('@').ToLowerInvariant();
            using var resp = await BrowserHttp.GetAsync(
                $"https://www.tiktok.com/@{Uri.EscapeDataString(handle)}/live",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return LiveState.Unknown;
            string html = await resp.Content.ReadAsStringAsync(ct);
            var m = RoomStatusRegex().Match(html);
            if (!m.Success)
                return LiveState.Unknown;
            return m.Groups[1].Value == "2" ? LiveState.Live : LiveState.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LiveState.Unknown;
        }
    }
}
