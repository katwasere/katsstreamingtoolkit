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
