namespace KatStreamToolkit.Chat;

public interface IChatClient : IDisposable
{
    string PlatformName { get; }
    event Action<ChatMessage>? MessageReceived;
    event Action<string>? StatusChanged;
    void Start();
}

// Capability interface for clients that can also WRITE to the chat (an
// authenticated connection). Clients without it are read-only adapters.
public interface IChatSender
{
    bool CanSend { get; }
    Task<bool> TrySendAsync(string text);
}

// Optional live counters for the overlay header: raw wire lines vs parsed
// chat messages. Separates "Twitch sends nothing" (raw stuck) from
// "the app drops messages" (raw climbs, messages stuck).
public interface IChatStats
{
    int RawLines { get; }
    int ChatMessages { get; }
}
