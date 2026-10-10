namespace KatStreamToolkit.Chat;

// One Twitch account login (the streamer or a mod account). Tokens live in
// secrets.json; this in-memory copy is what chat clients and the moderation
// service read.
public sealed record TwitchAuthData(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresUtc,
    string Login,
    string UserId);

// Carries the current Twitch auth to every chat client and overlay without
// threading it through constructors. MainViewModel keeps this in sync with
// secrets.json; overlays re-resolve their sources when it changes.
public static class ChatAuthStore
{
    public static TwitchAuthData? Twitch { get; private set; }

    public static event Action? TwitchChanged;

    public static void SetTwitch(TwitchAuthData? auth)
    {
        Twitch = auth;
        TwitchChanged?.Invoke();
    }
}
