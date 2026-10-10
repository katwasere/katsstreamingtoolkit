namespace KatStreamToolkit.Chat;

public sealed record ChatSourceSpec(string Platform, string Key, Func<IChatClient> Factory);

public sealed class ChatEntry : IDisposable
{
    private readonly object _subsGate = new();
    private readonly List<Action<ChatMessage>> _messageSubs = new();
    private readonly List<Action<string>> _statusSubs = new();

    public required IChatClient Client { get; init; }
    public int RefCount { get; set; }
    public string Status { get; private set; } = "starting...";

    public void Activate()
    {
        Client.MessageReceived += OnMessage;
        Client.StatusChanged += OnStatus;
        Client.Start();
    }

    // These are the ONLY paths from the client to subscribers (overlays, the
    // command runner). An earlier revision raised unused C# events here and
    // never called the subscription lists - chat flowed into a void: overlays
    // showed nothing real while test messages (injected past this chain)
    // worked, and the status line only looked right because Subscribe pushes
    // the current status once at subscribe time.
    private void OnMessage(ChatMessage msg)
    {
        Action<ChatMessage>[] subs;
        lock (_subsGate) subs = _messageSubs.ToArray();
        foreach (var sub in subs)
        {
            try { sub(msg); }
            catch { /* one broken subscriber must not starve the others */ }
        }
    }

    private void OnStatus(string status)
    {
        Status = status;
        Action<string>[] subs;
        lock (_subsGate) subs = _statusSubs.ToArray();
        foreach (var sub in subs)
        {
            try { sub(status); }
            catch { }
        }
    }

    public IDisposable Subscribe(Action<ChatMessage> onMessage, Action<string> onStatus)
    {
        lock (_subsGate)
        {
            _messageSubs.Add(onMessage);
            _statusSubs.Add(onStatus);
        }
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
            lock (_entry._subsGate)
            {
                _entry._messageSubs.Remove(_message);
                _entry._statusSubs.Remove(_status);
            }
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
            entry.RefCount = 1;
            // Activate BEFORE registering: if Start()/connect throws, the old
            // order left a dead entry in the dictionary and that platform could
            // never connect again until an app restart.
            try
            {
                entry.Activate();
            }
            catch
            {
                entry.Dispose();
                throw;
            }
            Entries[spec.Key] = entry;
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
