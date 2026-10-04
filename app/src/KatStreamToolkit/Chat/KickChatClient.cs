using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KatStreamToolkit.Chat;

// Kick chat via the public Pusher websocket. Channel names resolve to a numeric
// chatroom id through Kick's public API; if that fails you can paste the id manually.
public sealed class KickChatClient : IChatClient
{
    private const string PusherUrl =
        "wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0-rc2&flash=false";

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _channel;
    private readonly string? _manualChatroomId;
    private CancellationTokenSource? _cts;

    public string PlatformName => "Kick";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public KickChatClient(string channel, string? manualChatroomId)
    {
        _channel = channel.Trim().TrimStart('/').ToLowerInvariant();
        _manualChatroomId = string.IsNullOrWhiteSpace(manualChatroomId) ? null : manualChatroomId.Trim();
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var thread = new Thread(() => Run(cts)) { IsBackground = true, Name = "kick-chat" };
        thread.Start();
    }

    private async void Run(CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        string? roomId = _manualChatroomId;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (roomId == null)
                    {
                        StatusChanged?.Invoke("resolving channel...");
                        roomId = await ResolveChatroomId(_channel, ct)
                                 ?? throw new Exception("could not resolve channel id (Kick may be blocking - paste the chatroom id in the toolkit)");
                    }

                    StatusChanged?.Invoke("connecting...");
                    using var ws = new System.Net.WebSockets.ClientWebSocket();
                    using var closeOnCancel = ct.Register(() => ws.Dispose());
                    await ws.ConnectAsync(new Uri(PusherUrl), ct);
                    StatusChanged?.Invoke("connected");

                    var subscribe = JsonSerializer.Serialize(new
                    {
                        @event = "pusher:subscribe",
                        data = new { auth = "", channel = $"chatrooms.{roomId}.v2" },
                    });
                    await ws.SendAsync(Encoding.UTF8.GetBytes(subscribe),
                        System.Net.WebSockets.WebSocketMessageType.Text, true, ct);

                    var buffer = new byte[64 * 1024];
                    while (!ct.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        System.Net.WebSockets.WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                                throw new Exception("socket closed");
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);


                        HandleFrame(Encoding.UTF8.GetString(ms.ToArray()), ws, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"reconnecting ({ex.Message})");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            // Disposed here (on the loop's own thread) - see TwitchChatClient.Run.
            try { cts.Dispose(); } catch { }
        }
    }

    private void HandleFrame(string json, System.Net.WebSockets.ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;

            if (evt == "pusher:ping")
            {
                _ = SendText(ws, "{\"event\":\"pusher:pong\",\"data\":{}}", ct);
                return;
            }
            if (evt != "App\\Events\\ChatMessageEvent")
                return;

            if (!root.TryGetProperty("data", out var dataEl))
                return;
            var innerJson = dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() : dataEl.GetRawText();
            if (string.IsNullOrEmpty(innerJson))
                return;

            using var inner = JsonDocument.Parse(innerJson);
            var m = inner.RootElement;

            string author = "kick";
            string? color = null;
            if (m.TryGetProperty("sender", out var sender))
            {
                if (sender.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String)
                    author = un.GetString() ?? author;
                if (sender.TryGetProperty("identity", out var identity) &&
                    identity.TryGetProperty("color", out var col) &&
                    col.ValueKind == JsonValueKind.String)
                    color = col.GetString();
            }

            var text = new StringBuilder();
            if (m.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text.Append(t.GetString());
                    else if (chunk.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        text.Append('[').Append(n.GetString()).Append(']');
                }
            }
            if (text.Length == 0 && m.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                text.Append(msg.GetString());
            if (text.Length == 0)
                return;

            MessageReceived?.Invoke(new ChatMessage
            {
                Platform = PlatformName,
                Author = author,
                Color = color,
                Text = text.ToString(),
            });
        }
        catch
        {
            // Ignore malformed frames.
        }
    }

    private static async Task SendText(System.Net.WebSockets.ClientWebSocket ws, string payload, CancellationToken ct)
    {
        if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
    }

    public static async Task<string?> ResolveChatroomId(string channel, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}", ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("chatroom", out var chatroom) &&
                chatroom.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number)
                return id.GetRawText();
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Cancel only; the Run loop disposes the CTS itself (see Run).
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
