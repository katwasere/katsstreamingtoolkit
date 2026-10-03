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
        var thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "twitch-chat" };
        thread.Start();
    }

    private async void Run(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("connecting...");
                using var tcp = new TcpClient();
                ct.Register(() => tcp.Close());
                await tcp.ConnectAsync(Host, Port, ct);
                await using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsClientAsync(Host);
                using var reader = new StreamReader(ssl, Encoding.UTF8);
                await using var writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

                await writer.WriteLineAsync("CAP REQ :twitch.tv/tags");
                await writer.WriteLineAsync($"NICK justinfan{Random.Shared.Next(10000, 99999)}");
                await writer.WriteLineAsync($"JOIN #{_channel}");

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (line.StartsWith("PING"))
                    {
                        await writer.WriteLineAsync("PONG :tmi.twitch.tv");
                        continue;
                    }
                    if (line.Contains(" JOIN "))
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

    private static string UnescapeTag(string s) => s
        .Replace("\\s", " ")
        .Replace("\\:", ";")
        .Replace("\\\\", "\\")
        .Replace("\\r", "")
        .Replace("\\n", "");

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
