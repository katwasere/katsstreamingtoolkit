using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace KatStreamToolkit.Services;

// Raw transcript of a fresh anonymous Twitch IRC connection. Ground truth for
// "the overlay says connected but no messages appear": shows registration, the
// JOIN exchange, room state and every PRIVMSG exactly as the wire delivers it.
public static class ChatDiagnostics
{
    public static async Task<string> CaptureTwitchAsync(string channel, TimeSpan duration, CancellationToken ct)
    {
        var ch = channel.Trim().TrimStart('#').ToLowerInvariant();
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        void Line(string s) => sb.AppendLine($"[{sw.Elapsed.TotalSeconds,5:F1}s] {s}");

        sb.AppendLine($"== Twitch IRC raw capture - #{ch} - {duration.TotalSeconds:F0}s window ==");
        string nick = $"justinfan{Random.Shared.Next(10000, 99999)}";

        using var connectCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCt.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("irc.chat.twitch.tv", 6697, connectCt.Token);
        Line($"connected to irc.chat.twitch.tv:6697 (local port {((System.Net.IPEndPoint)tcp.Client.LocalEndPoint!).Port})");

        await using var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync("irc.chat.twitch.tv");
        Line("tls established");

        using var reader = new StreamReader(ssl, Encoding.UTF8);
        await using var writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
        Line("> CAP REQ :twitch.tv/tags twitch.tv/commands");
        await writer.WriteLineAsync($"NICK {nick}");
        Line($"> NICK {nick}");
        await writer.WriteLineAsync($"JOIN #{ch}");
        Line($"> JOIN #{ch}");

        using var window = new CancellationTokenSource(duration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, window.Token);
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync(linked.Token);
                if (line == null)
                {
                    Line("< stream ended by server");
                    break;
                }
                if (line.StartsWith("PING"))
                {
                    await writer.WriteLineAsync("PONG :tmi.twitch.tv");
                    Line($"> PONG :tmi.twitch.tv   (answering: {line})");
                    continue;
                }
                Line("< " + line);
            }
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Line("== capture window ended ==");
        }
        return sb.ToString();
    }
}
