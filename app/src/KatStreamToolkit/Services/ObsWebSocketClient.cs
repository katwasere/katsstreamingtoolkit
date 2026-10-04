using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KatStreamToolkit.Services;

// obs-websocket 5.x client (OBS 28+): JSON over a local websocket with a
// challenge/salt auth handshake. Powers go-live orchestration; the scene
// requests are already here so chat-event scene changes can hook in later
// without touching the transport.
public sealed class ObsWebSocketClient : IDisposable
{
    private readonly string _url;
    private readonly string _password;
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();

    public event Action<string>? StatusChanged;
    public event Action<bool>? StreamActiveChanged;
    public bool IsConnected { get; private set; }

    public ObsWebSocketClient(string url, string password)
    {
        _url = NormalizeUrl(url);
        _password = password ?? "";
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) url = "ws://127.0.0.1:4455";
        if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url;
        return url;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var thread = new Thread(() => Run(cts)) { IsBackground = true, Name = "obs-websocket" };
        thread.Start();
    }

    private async void Run(CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int closeCode = -1;
                try
                {
                    StatusChanged?.Invoke("connecting to OBS...");
                    using var ws = new ClientWebSocket();
                    _ws = ws;
                    using var closeReg = ct.Register(() => { try { ws.Dispose(); } catch { } });
                    await ws.ConnectAsync(new Uri(_url), ct);

                    // Hello (op 0): carries the auth challenge when a password is set.
                    var hello = await ReadFrameAsync(ws, ct);
                    string? challenge = null, salt = null;
                    if (hello.TryGetProperty("d", out var hd) &&
                        hd.TryGetProperty("authentication", out var authEl) &&
                        authEl.ValueKind == JsonValueKind.Object)
                    {
                        challenge = authEl.TryGetProperty("challenge", out var ch) ? ch.GetString() : null;
                        salt = authEl.TryGetProperty("salt", out var sa) ? sa.GetString() : null;
                    }

                    var identify = new Dictionary<string, object?>
                    {
                        ["rpcVersion"] = 1,
                        ["eventSubscriptions"] = 1, // OutputStates (StreamStateChanged)
                    };
                    if (challenge != null && salt != null)
                        identify["authentication"] = ComputeAuth(_password, salt, challenge);
                    await SendOpAsync(ws, 1, identify, ct);

                    // Identified (op 2) - a wrong password closes the socket (code 4009).
                    await ReadFrameAsync(ws, ct);
                    IsConnected = true;
                    StatusChanged?.Invoke("connected");

                    var buffer = new byte[256 * 1024];
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        var frame = await ReadFrameAsync(ws, ct, buffer);
                        HandleFrame(frame);
                    }                    throw new IOException("connection closed");
                }
                catch (ObsClosedException ex)
                {
                    closeCode = ex.CloseCode;
                    FailPending("OBS closed the connection");
                    IsConnected = false;
                    StatusChanged?.Invoke(ex.CloseCode == 4009
                        ? "wrong password (check the obs-websocket password)"
                        : $"connection closed by OBS ({ex.CloseCode})");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    FailPending(ex.Message);
                    IsConnected = false;
                    StatusChanged?.Invoke(
                        $"not connected ({Shorten(ex.Message)}) - is OBS running with obs-websocket enabled?");
                }
                finally
                {
                    _ws = null;
                    IsConnected = false;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            try { cts.Dispose(); } catch { }
        }
    }

    private sealed class ObsClosedException : Exception
    {
        public ObsClosedException(int closeCode) : base($"closed ({closeCode})") => CloseCode = closeCode;
        public int CloseCode { get; }
    }

    private static string ComputeAuth(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    // Sends a request (op 6) and awaits the matching response (op 7).
    public async Task<JsonElement?> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        var ws = _ws;
        if (!IsConnected || ws is not { State: WebSocketState.Open })
            throw new Exception("OBS is not connected");
        ct = ct == default ? CtsToken() : ct;

        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        var d = new Dictionary<string, object?>
        {
            ["requestType"] = requestType,
            ["requestId"] = id,
        };
        if (requestData != null) d["requestData"] = requestData;

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = 6, d });
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(6), ct));
            if (done != tcs.Task)
                throw new TimeoutException($"OBS did not answer '{requestType}'");
            return await tcs.Task;
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    private CancellationToken CtsToken() => _cts?.Token ?? CancellationToken.None;

    private void HandleFrame(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("op", out var opEl) || !root.TryGetProperty("d", out var d))
                return;
            int op = opEl.ValueKind == JsonValueKind.Number ? opEl.GetInt32() : -1;

            if (op == 7) // RequestResponse
            {
                string? id = d.TryGetProperty("requestId", out var idEl) ? idEl.GetString() : null;
                if (id == null) return;
                TaskCompletionSource<JsonElement?>? tcs;
                lock (_pending) _pending.TryGetValue(id, out tcs);
                if (tcs == null) return;

                bool result = d.TryGetProperty("requestStatus", out var status) &&
                              status.TryGetProperty("result", out var res) &&
                              res.ValueKind == JsonValueKind.True;
                if (!result)
                {
                    string comment = status.ValueKind == JsonValueKind.Object &&
                                     status.TryGetProperty("comment", out var c)
                        ? c.GetString() ?? ""
                        : "";
                    tcs.TrySetException(new Exception(
                        $"OBS rejected the request ({(comment.Length > 0 ? comment : "see obs-websocket log")})"));
                    return;
                }
                tcs.TrySetResult(
                    d.TryGetProperty("responseData", out var rd) && rd.ValueKind == JsonValueKind.Object
                        ? rd.Clone()
                        : null);
            }
            else if (op == 5) // Event
            {
                string? type = d.TryGetProperty("eventType", out var t) ? t.GetString() : null;
                if (type == "StreamStateChanged" &&
                    d.TryGetProperty("eventData", out var ed) &&
                    ed.TryGetProperty("outputActive", out var oa) &&
                    (oa.ValueKind == JsonValueKind.True || oa.ValueKind == JsonValueKind.False))
                {
                    StreamActiveChanged?.Invoke(oa.ValueKind == JsonValueKind.True);
                }
            }
        }
        catch
        {
            // Ignore malformed frames.
        }
    }

    private static async Task<JsonElement> ReadFrameAsync(ClientWebSocket ws, CancellationToken ct, byte[]? buffer = null)
    {
        buffer ??= new byte[256 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new ObsClosedException((int?)ws.CloseStatus ?? -1);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }

    private static async Task SendOpAsync(ClientWebSocket ws, int op, object d, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { op, d });
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void FailPending(string reason)
    {
        TaskCompletionSource<JsonElement?>[] pending;
        lock (_pending)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var tcs in pending)
            tcs.TrySetException(new Exception("OBS connection lost - " + reason));
    }

    // ---------- convenience wrappers ----------

    public async Task<bool> GetStreamingStatus(CancellationToken ct = default)
    {
        var data = await RequestAsync("GetStreamingStatus", null, ct);
        return data != null &&
               data.Value.TryGetProperty("outputActive", out var oa) &&
               oa.ValueKind == JsonValueKind.True;
    }

    public Task StartStream(CancellationToken ct = default) => RequestAsync("StartStream", null, ct);

    public Task StopStream(CancellationToken ct = default) => RequestAsync("StopStream", null, ct);

    public Task SetCurrentProgramScene(string sceneName, CancellationToken ct = default)
        => RequestAsync("SetCurrentProgramScene", new { sceneName }, ct);

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 90 ? s : s[..90] + "...";
    }

    public void Dispose()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _ws?.Dispose(); } catch { }
    }
}
