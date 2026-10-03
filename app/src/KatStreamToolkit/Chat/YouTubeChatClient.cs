using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KatStreamToolkit.Chat;

// YouTube live chat without an API key. Resolves a video URL or @channel handle to the
// current live video, scrapes the live-chat continuation token from the watch page, then
// polls the public innertube live_chat endpoint. Read-only, no login.
public sealed partial class YouTubeChatClient : IChatClient
{
    private const string InnerTubeKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = false };

    private readonly string _input;
    private CancellationTokenSource? _cts;

    public string PlatformName => "YouTube";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public YouTubeChatClient(string urlOrChannel)
    {
        _input = urlOrChannel.Trim();
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+cb.20210328-17-p0.en+FX+678; SOCS=CAI");
        return client;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "yt-chat" };
        thread.Start();
    }

    private async void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("finding live stream...");
                string videoId = await ResolveVideoId(_input, ct)
                                 ?? throw new Exception("no live stream found (is the channel live?)");

                StatusChanged?.Invoke("opening chat...");
                string continuation = await GetInitialContinuation(videoId, ct)
                                      ?? throw new Exception("chat not available for this stream");

                while (!ct.IsCancellationRequested)
                {
                    StatusChanged?.Invoke("connected");
                    var (nextToken, delay) = await PollOnce(continuation, ct);
                    if (nextToken == null)
                    {
                        StatusChanged?.Invoke("chat ended");
                        return;
                    }
                    continuation = nextToken;
                    await Task.Delay(delay, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"reconnecting ({ex.Message})");
                try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<(string? token, TimeSpan delay)> PollOnce(string continuation, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20240701.00.00" } },
            continuation,
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(
            $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={InnerTubeKey}", content, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var root = doc.RootElement;
        if (!root.TryGetProperty("continuationContents", out var cc) ||
            !cc.TryGetProperty("liveChatContinuation", out var lcc))
            return (null, TimeSpan.FromSeconds(5));

        var delay = TimeSpan.FromSeconds(5);
        if (lcc.TryGetProperty("pollingIntervalMillis", out var pollEl) && pollEl.TryGetInt32(out int pollMs))
            delay = TimeSpan.FromMilliseconds(Math.Clamp(pollMs, 2000, 15000));
        if (delay < TimeSpan.FromSeconds(2)) delay = TimeSpan.FromSeconds(2);

        if (lcc.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
            {
                if (!action.TryGetProperty("addChatItemAction", out var add)) continue;
                if (!add.TryGetProperty("item", out var item)) continue;
                ParseItem(item);
            }
        }

        string? next = null;
        if (lcc.TryGetProperty("continuations", out var conts) && conts.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conts.EnumerateArray())
            {
                next = FindFirstString(c, "continuation");
                if (next != null) break;
            }
        }
        return (next, delay);
    }

    private void ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name == "liveChatTextMessageRenderer")
            {
                Emit(prop.Value, null);
                return;
            }
            if (prop.Name == "liveChatPaidMessageRenderer")
            {
                string amount = prop.Value.TryGetProperty("purchaseAmountText", out var pa) && pa.TryGetProperty("simpleText", out var st)
                    ? st.GetString() ?? ""
                    : "";
                Emit(prop.Value, $"SUPERCHAT {amount} ");
                return;
            }
            if (prop.Name == "liveChatMembershipItemRenderer")
            {
                var header = prop.Value.TryGetProperty("header", out var h) ? h : prop.Value;
                string text = CollectRuns(header);
                MessageReceived?.Invoke(new ChatMessage
                {
                    Platform = PlatformName,
                    Author = "member",
                    Color = "#2BA640",
                    Text = text,
                });
                return;
            }
        }
    }

    private void Emit(JsonElement renderer, string? prefix)
    {
        string author = "YouTube";
        if (renderer.TryGetProperty("authorName", out var an))
        {
            if (an.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                author = st.GetString() ?? author;
        }

        var text = new StringBuilder(prefix);
        if (renderer.TryGetProperty("message", out var msg))
            text.Append(CollectRuns(msg));
        if (text.Length == 0)
            return;

        MessageReceived?.Invoke(new ChatMessage
        {
            Platform = PlatformName,
            Author = author,
            Color = null,
            Text = text.ToString(),
        });
    }

    private static string CollectRuns(JsonElement parent)
    {
        var sb = new StringBuilder();
        if (!parent.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            if (parent.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                sb.Append(st.GetString());
            return ChatText.DecodeEntities(sb.ToString());
        }
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                sb.Append(t.GetString());
            else if (run.TryGetProperty("emoji", out var emoji) && emoji.TryGetProperty("emojiId", out var eid) && eid.ValueKind == JsonValueKind.String)
                sb.Append(eid.GetString());
        }
        return ChatText.DecodeEntities(sb.ToString());
    }

    public static async Task<string?> ResolveVideoId(string input, CancellationToken ct)
    {
        // Direct video URL / id
        if (input.Length == 11 && !input.Contains('/') && !input.Contains('.'))
            return input;
        var m = Regex.Match(input, @"[?&]v=([A-Za-z0-9_-]{11})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(input, @"youtu\.be/([A-Za-z0-9_-]{11})");
        if (m.Success) return m.Groups[1].Value;

        // Channel handle / channel URL -> live video
        string channelUrl = input.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? input
            : $"https://www.youtube.com/{(input.StartsWith('@') ? "" : "@")}{input.TrimStart('@')}/live";
        using var resp = await Http.GetAsync(channelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        m = Regex.Match(finalUrl, @"[?&]v=([A-Za-z0-9_-]{11})");
        if (m.Success) return m.Groups[1].Value;

        if (!resp.IsSuccessStatusCode) return null;
        string html = await resp.Content.ReadAsStringAsync(ct);
        m = VideoIdRegex().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task<string?> GetInitialContinuation(string videoId, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://www.youtube.com/watch?v={videoId}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        string html = await resp.Content.ReadAsStringAsync(ct);

        int idx = html.IndexOf("ytInitialData", StringComparison.Ordinal);
        if (idx >= 0)
        {
            int brace = html.IndexOf('{', idx);
            int end = html.IndexOf("</script>", brace, StringComparison.Ordinal);
            if (brace > 0 && end > brace)
            {
                string jsonText = html[brace..end].TrimEnd().TrimEnd(';');
                try
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    string? token = FindLiveChatContinuation(doc.RootElement);
                    if (token != null) return token;
                }
                catch
                {
                    // fall through to regex
                }
            }
        }

        var m = ContinuationRegex().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? FindLiveChatContinuation(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name == "itemSectionRenderer" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in prop.Value.EnumerateArray())
                {
                    var token = FindFirstString(section, "continuation");
                    if (token != null) return token;
                }
            }
        }
        // Fallback: any continuations array
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var token = FindLiveChatContinuation(prop.Value);
                if (token != null) return token;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in prop.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        var token = FindLiveChatContinuation(child);
                        if (token != null) return token;
                    }
                }
            }
        }
        return null;
    }

    private static string? FindFirstString(JsonElement el, string key)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
                var nested = FindFirstString(prop.Value, key);
                if (nested != null) return nested;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                var nested = FindFirstString(child, key);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    [GeneratedRegex(@"""videoId"":""([A-Za-z0-9_-]{11})""")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"""continuation"":""([^""]{20,})""")]
    private static partial Regex ContinuationRegex();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
