using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace KatStreamToolkit.Chat;

// TikTok live chat via Euler Stream (https://eulerstream.com). TikTok signs its
// webcast websockets, so anonymous reads are impossible: the toolkit asks Euler's
// API to mint a signed wss URL for the room (free API key required), connects to
// it and parses the chat events. TikTok *video* needs none of this - that is the
// relay's normal RTMP push.
public sealed class TikTokChatClient : IChatClient
{
    private const string EulerBase = "https://online.eulerstream.com/api";

    // Free-tier key created on eulerstream.com. Lives in secrets.json; the
    // MainViewModel pushes it in here on load/change (same static style as ChatHub).
    public static string? ApiKey { get; set; }

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _handle;
    private CancellationTokenSource? _cts;

    public string PlatformName => "TikTok";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public TikTokChatClient(string handle)
    {
        _handle = handle.Trim().TrimStart('@').ToLowerInvariant();
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var thread = new Thread(() => Run(cts)) { IsBackground = true, Name = "tiktok-chat" };
        thread.Start();
    }

    private async void Run(CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ApiKey))
                    {
                        StatusChanged?.Invoke("needs an Euler Stream API key - free at eulerstream.com, paste it in the toolkit's Chat Overlays tab");
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        continue;
                    }

                    // Best-effort pre-check: without it a live-not-started error
                    // from the sign endpoint looks like any other failure.
                    StatusChanged?.Invoke("finding live stream...");
                    if (await GetRoomStatus(ct) == false)
                    {
                        StatusChanged?.Invoke("waiting for the live to start...");
                        await Task.Delay(TimeSpan.FromSeconds(20), ct);
                        continue;
                    }

                    StatusChanged?.Invoke("connecting...");
                    string wsUrl = await SignWebsocket(ct);
                    using var ws = new ClientWebSocket();
                    using var closeOnCancel = ct.Register(() => ws.Dispose());
                    await ws.ConnectAsync(new Uri(wsUrl), ct);
                    StatusChanged?.Invoke("connected");

                    var buffer = new byte[512 * 1024];
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            if (result.MessageType == WebSocketMessageType.Close)
                                throw new IOException("relay closed the socket (stream probably ended)");
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        HandleFrame(Encoding.UTF8.GetString(ms.ToArray()));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (NotLiveException)
                {
                    StatusChanged?.Invoke("waiting for the live to start...");
                    try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
                    continue;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"reconnecting ({Shorten(ex.Message)})");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(6), ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            // Disposed here (on the loop's own thread) - see TwitchChatClient.Run.
            try { cts.Dispose(); } catch { }
        }
    }

    private sealed class NotLiveException : Exception
    {
        public NotLiveException(string message) : base(message) { }
    }

    // roomInfo.status: 2 = live, 4 = ended. Unknown result (null) just means the
    // endpoint could not be read - the sign call decides then.
    private async Task<bool?> GetRoomStatus(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(
                $"{EulerBase}/room/info?uniqueId={Uri.EscapeDataString(_handle)}&apiKey={Uri.EscapeDataString(ApiKey!)}", ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (FindKey(doc.RootElement, "roomInfo") is { } room &&
                room.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number)
                return st.GetInt32() == 2;
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> SignWebsocket(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"{EulerBase}/websocket/sign?uniqueId={Uri.EscapeDataString(_handle)}&apiKey={Uri.EscapeDataString(ApiKey!)}", ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string reason = DescribeEulerError(body, resp.StatusCode.ToString());
            if (ReasonMeansNotLive(reason))
                throw new NotLiveException(reason);
            throw new Exception(reason);
        }
        using var doc = JsonDocument.Parse(body);
        string? url = FindFirstString(doc.RootElement, "websocketUrl");
        return string.IsNullOrEmpty(url)
            ? throw new Exception("Euler Stream answered but returned no websocket URL")
            : url;
    }

    private static bool ReasonMeansNotLive(string reason) =>
        reason.Contains("LIVE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("LIVE_ALREADY_ENDED", StringComparison.OrdinalIgnoreCase) ||
        (reason.Contains("not live", StringComparison.OrdinalIgnoreCase) &&
         !reason.Contains("api key", StringComparison.OrdinalIgnoreCase));

    private static string DescribeEulerError(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? message = FindFirstString(doc.RootElement, "message")
                              ?? FindFirstString(doc.RootElement, "error");
            if (!string.IsNullOrWhiteSpace(message))
                return message!;
        }
        catch
        {
            // Not JSON - fall through.
        }
        body = body.Trim();
        return body.Length == 0 ? $"Euler Stream error ({fallback})" : Shorten(body);
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 100 ? s : s[..100] + "...";
    }

    private void HandleFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? evt = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;
            if (evt != "chat" || !root.TryGetProperty("data", out var dataEl))
                return;

            // data arrives as an object (sometimes string-encoded, like Pusher).
            var innerJson = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() : dataEl.GetRawText();
            if (string.IsNullOrEmpty(innerJson))
                return;
            using var inner = JsonDocument.Parse(innerJson);
            var m = inner.RootElement;

            string text = m.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            if (text.Length == 0)
                return;

            string author = "tiktok";
            if (m.TryGetProperty("user", out var user))
            {
                if (user.TryGetProperty("nickname", out var n) && n.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(n.GetString()))
                    author = n.GetString()!;
                else if (user.TryGetProperty("uniqueId", out var u) && u.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(u.GetString()))
                    author = u.GetString()!;
            }

            MessageReceived?.Invoke(new ChatMessage
            {
                Platform = PlatformName,
                Author = author,
                Color = null,
                Text = text,
            });
        }
        catch
        {
            // Ignore malformed frames.
        }
    }

    private static JsonElement? FindKey(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.NameEquals(key))
                return prop.Value;
            var nested = FindKey(prop.Value, key);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static string? FindFirstString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
            var nested = FindFirstString(prop.Value, key);
            if (nested != null)
                return nested;
        }
        return null;
    }

    public void Dispose()
    {
        // Cancel only; the Run loop disposes the CTS itself (see Run).
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
