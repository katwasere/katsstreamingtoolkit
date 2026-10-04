using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace KatStreamToolkit.Chat;

// Read-only anonymous Twitch IRC connection (justinfan). No account or OAuth needed.
public sealed class TwitchChatClient : IChatClient
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;

    private readonly string _channel;
    private CancellationTokenSource? _cts;

    public string PlatformName => "Twitch";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public TwitchChatClient(string channel)
    {
        _channel = channel.TrimStart('#').ToLowerInvariant();
    }

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
        string nick = $"justinfan{Random.Shared.Next(10000, 99999)}";
        var backoff = TimeSpan.FromSeconds(3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    StatusChanged?.Invoke("connecting...");
                    using var tcp = new TcpClient();
                    using var closeOnCancel = ct.Register(() => tcp.Close());
                    await tcp.ConnectAsync(Host, Port, ct);
                    await using var ssl = new SslStream(tcp.GetStream());
                    await ssl.AuthenticateAsClientAsync(Host);
                    using var reader = new StreamReader(ssl, Encoding.UTF8);
                    await using var writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

                    await writer.WriteLineAsync("CAP REQ :twitch.tv/tags");
                    await writer.WriteLineAsync($"NICK {nick}");
                    await writer.WriteLineAsync($"JOIN #{_channel}");

                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        if (line.StartsWith("PING"))
                        {
                            await writer.WriteLineAsync("PONG :tmi.twitch.tv");
                            continue;
                        }
                        // Only OUR join (or the 001 welcome) means connected - any
                        // " JOIN " line fires whenever someone else joins the room.
                        if (line.Contains(" 001 ") || line.StartsWith(':' + nick + '!'))
                            StatusChanged?.Invoke("connected");
                        if (line.Contains("RECONNECT"))
                            throw new IOException("server requested reconnect");
                        ParseAndRaise(line);
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
