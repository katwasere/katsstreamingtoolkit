using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace KatStreamToolkit.Chat;

// Twitch IRC. Anonymous (justinfan) read-only by default; with a login+token it
// connects authenticated, joins with the account's permissions and gains the
// ability to SEND messages (the foundation for !command replies).
public sealed class TwitchChatClient : IChatClient, IChatSender
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;

    private readonly string _channel;
    private readonly string? _login;
    private readonly string? _accessToken;
    private CancellationTokenSource? _cts;

    // Live only while the read loop is inside a connected session; the send
    // path takes the gate so a write can never race the writer's disposal.
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastSend = DateTime.MinValue;

    // Last line (ANY line, incl. PONGs) received from the wire. The keepalive
    // watchdog kills the socket when this goes stale - see KeepaliveLoop.
    private DateTime _lastLineUtc = DateTime.UtcNow;

    public string PlatformName => "Twitch";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public TwitchChatClient(string channel, string? login = null, string? accessToken = null)
    {
        _channel = channel.TrimStart('#').ToLowerInvariant();
        _login = string.IsNullOrWhiteSpace(login) ? null : login.Trim();
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
    }

    public bool CanSend => _login != null && _accessToken != null;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var thread = new Thread(() => Run(cts)) { IsBackground = true, Name = "twitch-chat" };
        thread.Start();
    }

    private async void Run(CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        string nick = _login ?? $"justinfan{Random.Shared.Next(10000, 99999)}";
        var backoff = TimeSpan.FromSeconds(3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                StreamWriter? writer = null;
                try
                {
                    StatusChanged?.Invoke("connecting...");
                    using var tcp = new TcpClient();
                    using var closeOnCancel = ct.Register(() => tcp.Close());
                    await tcp.ConnectAsync(Host, Port, ct);
                    await using var ssl = new SslStream(tcp.GetStream());
                    await ssl.AuthenticateAsClientAsync(Host);
                    using var reader = new StreamReader(ssl, Encoding.UTF8);
                    writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

                    // Same handshake order as the diagnostics capture (and the
                    // Twitch docs): capabilities first, then auth/nick/join.
                    await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                    if (_accessToken != null)
                        await writer.WriteLineAsync($"PASS oauth:{_accessToken}");
                    await writer.WriteLineAsync($"NICK {nick}");
                    await writer.WriteLineAsync($"JOIN #{_channel}");

                    _lastLineUtc = DateTime.UtcNow;
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    Task keepalive = KeepaliveLoop(tcp, pingCts.Token);
                    try
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync(ct)) != null)
                        {
                            _lastLineUtc = DateTime.UtcNow;
                            if (line.StartsWith("PING"))
                            {
                                await writer.WriteLineAsync("PONG :tmi.twitch.tv");
                                continue;
                            }
                            // Only OUR join (or the 001 welcome) means connected - any
                            // " JOIN " line fires whenever someone else joins the room.
                            if (line.Contains(" 001 ") || line.StartsWith(':' + nick + '!'))
                            {
                                // Publish the writer only once the room state is live,
                                // so sends never race the join.
                                await _sendGate.WaitAsync(ct);
                                try { _writer = writer; } finally { _sendGate.Release(); }
                                StatusChanged?.Invoke(_accessToken != null ? "connected (logged in)" : "connected");
                            }
                            if (line.Contains("RECONNECT"))
                                throw new IOException("server requested reconnect");
                            if (line.Contains("Login authentication failed") ||
                                line.Contains("Improperly formatted AUTH"))
                                StatusChanged?.Invoke("twitch rejected the login - log in again on the Chat Overlays tab");
                            ParseAndRaise(line);
                        }
                    }
                    finally
                    {
                        // Stop the keepalive BEFORE the writer teardown below,
                        // so it can never write to a stream being disposed.
                        pingCts.Cancel();
                        try { await keepalive; } catch { }
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
                finally
                {
                    // Take the writer back out under the send gate, THEN dispose -
                    // a concurrent TrySendAsync can never write to a dead stream.
                    try
                    {
                        await _sendGate.WaitAsync(CancellationToken.None);
                        try { _writer = null; } finally { _sendGate.Release(); }
                    }
                    catch { }
                    try { writer?.Dispose(); } catch { }
                }
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            // Dispose lives here (on the loop's own thread), NOT in Dispose() -
            // disposing the CTS from another thread while the loop waits in
            // Task.Delay(backoff, ct) threw ObjectDisposedException out of this
            // async void and crashed the process.
            try { cts.Dispose(); } catch { }
        }
    }

    // Runs alongside the read loop for the lifetime of one connection.
    // NAT routers and firewalls silently drop idle sockets - which looks
    // exactly like "connected" forever with zero messages arriving. A PING
    // every 2 minutes keeps the mapping alive; if NOTHING comes back (not
    // even a PONG) for 6 minutes, the socket is provably dead: kill it so
    // the run loop reconnects instead of starving silently.
    private async Task KeepaliveLoop(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await _sendGate.WaitAsync(ct);
                    try
                    {
                        var w = _writer;
                        if (w != null)
                            await w.WriteLineAsync("PING :tmi.twitch.tv");
                    }
                    finally { _sendGate.Release(); }
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                if (DateTime.UtcNow - _lastLineUtc > TimeSpan.FromMinutes(6))
                {
                    try { tcp.Close(); } catch { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // IRC words cannot contain CR/LF; flatten them so a config response can
    // never inject a second IRC message.
    public async Task<bool> TrySendAsync(string text)
    {
        if (!CanSend) return false;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) return false;

        // Pace sends: Twitch allows ~20 messages/30s for regular users (100 for
        // mods) and rejects beyond that with no retry hint. One message per
        // 1.2s is far inside the limit for a command bot.
        var sinceLast = DateTime.UtcNow - _lastSend;
        if (sinceLast < TimeSpan.FromMilliseconds(1200))
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(1200) - sinceLast); }
            catch { return false; }
        }
        try
        {
            await _sendGate.WaitAsync();
            try
            {
                var w = _writer;
                if (w == null) return false;
                await w.WriteLineAsync($"PRIVMSG #{_channel} :{text}");
                _lastSend = DateTime.UtcNow;
                return true;
            }
            finally { _sendGate.Release(); }
        }
        catch
        {
            return false;
        }
    }

    private void ParseAndRaise(string line)
    {
        string? tagsPart = null;
        string rest = line;
        if (line.StartsWith('@'))
        {
            int sp = line.IndexOf(' ');
            if (sp < 0) return;
            tagsPart = line[1..sp];
            rest = line[(sp + 1)..];
        }
        if (!rest.StartsWith(':'))
            return;
        int sp2 = rest.IndexOf(' ');
        if (sp2 < 0) return;
        string prefix = rest[1..sp2];
        string nick = prefix.Contains('!') ? prefix[..prefix.IndexOf('!')] : prefix;
        rest = rest[(sp2 + 1)..];

        int sp3 = rest.IndexOf(' ');
        if (sp3 < 0) return;
        string command = rest[..sp3];
        rest = rest[(sp3 + 1)..];

        if (command != "PRIVMSG")
            return;
        int msgIdx = rest.IndexOf(" :");
        if (msgIdx < 0) return;
        string text = rest[(msgIdx + 2)..];

        string? color = null;
        string author = nick;
        string? msgId = null;
        string? authorId = null;
        bool isMod = false;
        if (tagsPart != null)
        {
            foreach (var pair in tagsPart.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq];
                var val = UnescapeTag(pair[(eq + 1)..]);
                if (key == "display-name" && val.Length > 0) author = val;
                else if (key == "color" && val.Length > 1) color = val;
                else if (key == "id" && val.Length > 0) msgId = val;
                else if (key == "user-id" && val.Length > 0) authorId = val;
                else if (key == "badges" &&
                         (val.Contains("broadcaster/") || val.Contains("moderator/")))
                    isMod = true;
            }
        }

        bool isAction = false;
        if (text.StartsWith("\u0001ACTION ") && text.EndsWith('\u0001'))
        {
            isAction = true;
            text = text["\u0001ACTION ".Length..^1];
        }

        MessageReceived?.Invoke(new ChatMessage
        {
            Platform = PlatformName,
            Author = author,
            Color = color,
            Text = text,
            IsAction = isAction,
            MsgId = msgId,
            AuthorId = authorId,
            IsMod = isMod,
        });
    }

    // Sequential .Replace calls cannot unescape correctly (a literal "\s" in the
    // source garbled into a space). Parse escapes in one pass instead.
    private static string UnescapeTag(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            switch (s[i + 1])
            {
                case '\\': sb.Append('\\'); i++; break;
                case 's': sb.Append(' '); i++; break;
                case ':': sb.Append(';'); i++; break;
                case 'r': i++; break;
                case 'n': i++; break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        // Cancel only: the CTS is disposed by the Run loop when it unwinds (see
        // the finally in Run), so a mid-reconnect Task.Delay can never observe a
        // disposed token.
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
