using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// Watches every configured chat channel for !commands and executes them:
// reply in the platform's chat (where the platform supports sending - Twitch
// today) and/or flip an OBS scene. Shares its connections with the overlays
// through ChatHub, so a channel is never connected twice.
public sealed class CommandRunner : IDisposable
{
    private readonly AppConfig _config;
    private readonly Func<string, Task> _switchScene;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatEntry> _entries = new();
    private readonly Dictionary<string, IDisposable> _subs = new();
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, IChatSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);

    // Reply texts this runner sent, with timestamps: Twitch echoes our own
    // messages back to us, so if a configured reply is itself a command
    // (response starting with '!'), the echo must not re-trigger it.
    private readonly Dictionary<string, DateTime> _recentReplies = new(StringComparer.OrdinalIgnoreCase);

    // Set by the view model: a chat line to display on the overlays when a
    // command with ShowOnOverlay fires.
    public Action<ChatMessage>? OverlayLine { get; set; }

    // One-line activity feed for the Commands tab (marshaled to the UI thread
    // by the subscriber).
    public event Action<string>? Activity;

    public CommandRunner(AppConfig config, Func<string, Task> switchScene)
    {
        _config = config;
        _switchScene = switchScene;
    }

    // Acquires/releases entries so the watched set always matches the current
    // channels; ChatHub refcounting makes re-syncing cheap.
    public void SyncSources()
    {
        lock (_gate)
        {
            var desired = BuildDesired();
            var wanted = desired.Select(s => s.Key).ToHashSet();
            foreach (var key in _keys.Where(k => !wanted.Contains(k)).ToList())
                Release(key);
            foreach (var spec in desired.Where(s => !_keys.Contains(s.Key)))
                Acquire(spec);
        }
    }

    private void Acquire(ChatSourceSpec spec)
    {
        try
        {
            var entry = ChatHub.Acquire(spec);
            _entries[spec.Key] = entry;
            _keys.Add(spec.Key);
            if (entry.Client is IChatSender sender && !_senders.ContainsKey(spec.Platform))
                _senders[spec.Platform] = sender;
            _subs[spec.Key] = entry.Subscribe(
                msg => OnMessage(msg),
                _ => { });
        }
        catch
        {
            // A platform that fails to connect must not break the others;
            // SyncSources can retry on the next channel edit.
        }
    }

    private void Release(string key)
    {
        if (_subs.Remove(key, out var sub)) sub.Dispose();
        if (_entries.Remove(key, out var entry)) ChatHub.Release(key);
        _keys.Remove(key);
        RebuildSenders();
    }

    private void RebuildSenders()
    {
        _senders.Clear();
        foreach (var entry in _entries.Values)
            if (entry.Client is IChatSender sender && !_senders.ContainsKey(entry.Client.PlatformName))
                _senders[entry.Client.PlatformName] = sender;
    }

    // Every channel the toolkit knows about: "My channels" plus each overlay's
    // per-platform fields (dedup happens through the spec keys). TikTok is on
    // hold (see ROADMAP) and stays out.
    private List<ChatSourceSpec> BuildDesired()
    {
        var specs = new List<ChatSourceSpec>();
        void Add(ChatSourceSpec? spec) { if (spec != null) specs.Add(spec); }

        var mc = _config.MyChannels;
        if (!string.IsNullOrWhiteSpace(mc.TwitchChannel)) Add(ChatSources.Twitch(mc.TwitchChannel));
        if (!string.IsNullOrWhiteSpace(mc.KickChannel)) Add(ChatSources.Kick(mc.KickChannel, null));
        if (!string.IsNullOrWhiteSpace(mc.YouTubeUrl)) Add(ChatSources.YouTube(mc.YouTubeUrl));

        foreach (var o in _config.Overlays)
        {
            if (!string.IsNullOrWhiteSpace(o.TwitchChannel)) Add(ChatSources.Twitch(o.TwitchChannel));
            if (!string.IsNullOrWhiteSpace(o.KickChannel)) Add(ChatSources.Kick(o.KickChannel, o.KickChatroomId));
            if (!string.IsNullOrWhiteSpace(o.YouTubeUrl)) Add(ChatSources.YouTube(o.YouTubeUrl));
        }
        return specs.GroupBy(s => s.Key).Select(g => g.First()).ToList();
    }

    private void OnMessage(ChatMessage msg)
    {
        if (msg.Text.Length < 2 || msg.Text[0] != '!') return;

        string word = msg.Text[1..].Split(' ', 2)[0].Trim().ToLowerInvariant();
        if (word.Length == 0) return;

        // The logged-in account is usually the broadcaster - whose typed
        // commands MUST work - so do NOT blanket-ignore it. Only skip echoes
        // of replies this runner itself just sent (loop protection for
        // responses that start with '!').
        var auth = ChatAuthStore.Twitch;
        if (auth != null && msg.Platform == "Twitch" &&
            string.Equals(msg.Author, auth.Login, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                PruneReplies();
                if (_recentReplies.TryGetValue(msg.Text.Trim(), out var sent) &&
                    DateTime.UtcNow - sent < TimeSpan.FromSeconds(15))
                    return;
            }
        }

        CommandConfig? cmd;
        lock (_gate)
        {
            cmd = _config.Commands.FirstOrDefault(c =>
                c.Enabled &&
                c.Name.Trim().TrimStart('!').ToLowerInvariant() == word);
            if (cmd == null) return;
            var now = DateTime.UtcNow;
            if (_lastRun.TryGetValue(cmd.Name, out var last) &&
                now - last < TimeSpan.FromSeconds(Math.Max(0, cmd.CooldownSeconds)))
                return; // still on cooldown - stay silent, no spam
            _lastRun[cmd.Name] = now;
        }
        Execute(cmd, msg);
    }

    private void PruneReplies()
    {
        // caller holds _gate
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(60);
        var stale = _recentReplies.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
            _recentReplies.Remove(key);
    }

    private async void Execute(CommandConfig cmd, ChatMessage msg)
    {
        string name = cmd.Name.Trim().TrimStart('!');
        try
        {
            string response = cmd.Response?.Trim() ?? "";
            if (response.Length > 0)
            {
                IChatSender? sender = null;
                lock (_gate) { _senders.TryGetValue(msg.Platform, out sender); }
                if (sender is { CanSend: true })
                {
                    bool sent = await sender.TrySendAsync(response);
                    if (sent)
                    {
                        lock (_gate) { _recentReplies[response] = DateTime.UtcNow; }
                        Note($"!{name}: replied in {msg.Platform} chat");
                    }
                    else
                    {
                        Note($"!{name}: could not send to {msg.Platform} (connection down?)");
                    }
                }
                else
                {
                    Note($"!{name}: triggered - {msg.Platform} chat is read-only so far " +
                         "(no reply sent; writing there needs that platform's OAuth work)");
                }
            }

            string scene = cmd.Scene?.Trim() ?? "";
            if (scene.Length > 0)
            {
                try
                {
                    await _switchScene(scene);
                    Note($"!{name}: OBS scene -> {scene}");
                }
                catch (Exception ex)
                {
                    Note($"!{name}: scene switch failed ({ex.Message})");
                }
            }

            // Optional overlay echo of the command, per-command toggle.
            if (cmd.ShowOnOverlay)
            {
                string text = response.Length > 0 ? response : $"{msg.Author} used !{name}";
                try { OverlayLine?.Invoke(new ChatMessage { Platform = msg.Platform, Author = "bot", Text = text }); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Note($"!{name}: {ex.Message}");
        }
    }

    private void Note(string text) => Activity?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys.ToList())
                Release(key);
            _keys.Clear();
        }
    }
}
