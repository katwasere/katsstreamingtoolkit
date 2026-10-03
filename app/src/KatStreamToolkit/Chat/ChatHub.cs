namespace KatStreamToolkit.Chat;

public sealed record ChatSourceSpec(string Platform, string Key, Func<IChatClient> Factory);

public sealed class ChatEntry : IDisposable
{
    private readonly List<Action<ChatMessage>> _messageSubs = new();
    private readonly List<Action<string>> _statusSubs = new();

    public required IChatClient Client { get; init; }
    public int RefCount { get; set; }
    public string Status { get; private set; } = "starting...";

    public event Action<ChatMessage>? MessageReceived;
    public event Action? StatusUpdated;

    public void Activate()
    {
        Client.MessageReceived += OnMessage;
        Client.StatusChanged += OnStatus;
        Client.Start();
    }

    private void OnMessage(ChatMessage msg) => MessageReceived?.Invoke(msg);

    private void OnStatus(string status)
    {
        Status = status;
        StatusUpdated?.Invoke();
    }

    public IDisposable Subscribe(Action<ChatMessage> onMessage, Action<string> onStatus)
    {
        _messageSubs.Add(onMessage);
        _statusSubs.Add(onStatus);
        onStatus(Status);
        return new Subscription(this, onMessage, onStatus);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ChatEntry _entry;
        private readonly Action<ChatMessage> _message;
        private readonly Action<string> _status;

        public Subscription(ChatEntry entry, Action<ChatMessage> message, Action<string> status)
        {
            _entry = entry;
            _message = message;
            _status = status;
        }

        public void Dispose()
        {
            _entry._messageSubs.Remove(_message);
            _entry._statusSubs.Remove(_status);
        }
    }

    public void Dispose() => Client.Dispose();
}

// Shares one connection per (platform, channel) across all overlays.
public static class ChatHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ChatEntry> Entries = new();

    public static ChatEntry Acquire(ChatSourceSpec spec)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(spec.Key, out var existing))
            {
                existing.RefCount++;
                return existing;
            }
            var client = spec.Factory();
            var entry = new ChatEntry { Client = client };
            Entries[spec.Key] = entry;
            entry.RefCount = 1;
            entry.Activate();
            return entry;
        }
    }

    public static void Release(string key)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry)) return;
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                Entries.Remove(key);
                entry.Dispose();
            }
        }
    }

    public static void DisposeAll()
    {
        lock (Gate)
        {
            foreach (var entry in Entries.Values)
                entry.Dispose();
            Entries.Clear();
        }
    }
}
