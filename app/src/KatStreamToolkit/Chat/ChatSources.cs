using System.Text.RegularExpressions;

namespace KatStreamToolkit.Chat;

// Single place that maps channels to chat source specs, so overlays and the
// command runner create identical (and auth-aware) clients instead of each
// hand-rolling the factories.
public static class ChatSources
{
    public static ChatSourceSpec Twitch(string channel)
    {
        var ch = channel.Trim().TrimStart('#').ToLowerInvariant();
        return new ChatSourceSpec("Twitch", $"twitch:{ch}", () =>
        {
            // The client resolves auth from ChatAuthStore at every connect, so
            // logging in / token refresh upgrades existing connections on their
            // own reconnect - no entry rebuild needed.
            return new TwitchChatClient(ch);
        });
    }

    public static ChatSourceSpec Kick(string channel, string? manualChatroomId)
    {
        var ch = channel.Trim().TrimStart('/').ToLowerInvariant();
        var manual = string.IsNullOrWhiteSpace(manualChatroomId) ? null : manualChatroomId.Trim();
        // BUG-59: the key must not depend on HOW the channel was entered - the
        // manual chatroom id is just another way to reach the same chatroom, so
        // key by the channel name whenever it is present ("foo" and "foo +
        // 12345" share one connection instead of doubling every message).
        // Id-only entries key on the id (there is no name to canonicalize).
        var key = ch.Length > 0 ? $"kick:{ch}" : $"kick:id:{manual}";
        return new ChatSourceSpec("Kick", key, () => new KickChatClient(ch, manual));
    }

    public static ChatSourceSpec YouTube(string urlOrChannel)
    {
        var url = urlOrChannel.Trim();
        // BUG-59: the key was the raw lowercased input - youtube.com/watch?v=X,
        // youtu.be/X, the bare video id and the @handle spelled out the SAME
        // channel as three different keys, so two overlays connected twice and
        // every message arrived doubled (with cooldown 0, commands double-fired).
        // Canonicalize: a video id stays the id, everything else normalizes to
        // the bare handle/name/path form.
        string key = url.ToLowerInvariant();
        var m = Regex.Match(key, @"[?&]v=([a-z0-9_-]{11})");
        if (m.Success) key = m.Groups[1].Value;
        else
        {
            m = Regex.Match(key, @"youtu\.be/([a-z0-9_-]{11})");
            if (m.Success) key = m.Groups[1].Value;
            else
            {
                int idx = key.IndexOf("youtube.com/", StringComparison.Ordinal);
                if (idx >= 0) key = key[(idx + "youtube.com/".Length)..];
                key = key.TrimStart('@', '/').TrimEnd('/');
            }
        }
        return new ChatSourceSpec("YouTube", $"yt:{key}",
            () => new YouTubeChatClient(url));
    }

    public static ChatSourceSpec TikTok(string handle)
    {
        var h = handle.Trim().TrimStart('@').ToLowerInvariant();
        return new ChatSourceSpec("TikTok", $"tiktok:{h}",
            () => new TikTokChatClient(h));
    }
}
