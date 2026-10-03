namespace KatStreamToolkit.Chat;

public interface IChatClient : IDisposable
{
    string PlatformName { get; }
    event Action<ChatMessage>? MessageReceived;
    event Action<string>? StatusChanged;
    void Start();
}
