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
        return new ChatSourceSpec("Kick", $"kick:{manual ?? ch}",
            () => new KickChatClient(ch, manual));
    }

    public static ChatSourceSpec YouTube(string urlOrChannel)
    {
        var url = urlOrChannel.Trim();
        return new ChatSourceSpec("YouTube", $"yt:{url.ToLowerInvariant()}",
            () => new YouTubeChatClient(url));
    }

    public static ChatSourceSpec TikTok(string handle)
    {
        var h = handle.Trim().TrimStart('@').ToLowerInvariant();
        return new ChatSourceSpec("TikTok", $"tiktok:{h}",
            () => new TikTokChatClient(h));
    }
}
