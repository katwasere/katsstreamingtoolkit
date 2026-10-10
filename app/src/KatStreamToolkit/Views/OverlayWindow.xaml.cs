using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;
using KatStreamToolkit.Services;

namespace KatStreamToolkit.Views;

public partial class OverlayWindow : Window
{
    private readonly OverlayConfig _config;
    private readonly ObservableCollection<ChatMessage> _lines = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly List<string> _sourceKeys = new();
    private readonly DispatcherTimer _saveDebounce;
    private bool _atBottom = true;

    public OverlayWindow(OverlayConfig config)
    {
        InitializeComponent();
        _config = config;
        DataContext = _config;
        Messages.ItemsSource = _lines;
        SetBinding(FontSizeProperty, new System.Windows.Data.Binding("FontSize") { Source = _config });

        Left = _config.X;
        Top = _config.Y;
        Width = _config.Width;
        Height = _config.Height;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            PersistGeometry();
        };
        LocationChanged += (_, _) => OnGeometryChanged();
        SizeChanged += (_, _) => OnGeometryChanged();

        ChatAuthStore.TwitchChanged += OnTwitchAuthChanged;
        ContextMenuOpening += OnContextMenuOpening;

        Closed += (_, _) =>
        {
            PersistGeometry();
            ReleaseSources();
            ChatAuthStore.TwitchChanged -= OnTwitchAuthChanged;
        };
        Loaded += (_, _) => ApplyLockStyle();

        if (_config.X == 0 && _config.Y == 0)
        {
            Left = SystemParameters.WorkArea.Width - Width - 48;
            Top = 48;
        }

        if (!_config.IsVisible)
            Hide();

        StartSources();
    }

    private void OnTwitchAuthChanged()
    {
        // Token refreshes raise this off the UI thread.
        Dispatcher.BeginInvoke(RestartSources);
    }

    private void OnGeometryChanged()
    {
        if (_saveDebounce.IsEnabled) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void PersistGeometry()
    {
        _config.X = Left;
        _config.Y = Top;
        _config.Width = ActualWidth;
        _config.Height = ActualHeight;
    }

    public void ToggleLock()
    {
        _config.Locked = !_config.Locked;
        ApplyLockStyle();
    }

    public void ApplyLockStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (_config.Locked)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
            PersistGeometry();
        }
    }

    public void RestartSources() => StartSources();

    // Bumped on every (re)start: status callbacks from the previous sources can
    // still be queued right before RestartSources replaces them, and the old
    // code let them repaint the header with stale text.
    private int _sourceGeneration;

    private void StartSources()
    {
        int generation = ++_sourceGeneration;
        ReleaseSources();

        var specs = BuildSpecs(_config);
        if (specs.Count == 0)
        {
            StatusText.Text = "no chats configured";
            return;
        }

        foreach (var spec in specs)
        {
            var entry = ChatHub.Acquire(spec);
            _sourceKeys.Add(spec.Key);
            _subscriptions.Add(entry.Subscribe(
                msg => Dispatcher.BeginInvoke(() => AppendMessage(msg)),
                status => Dispatcher.BeginInvoke(() => UpdateStatus(generation, spec.Platform, status))));
            _statuses[spec.Platform] = "starting...";
        }
    }

    private void ReleaseSources()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        foreach (var key in _sourceKeys) ChatHub.Release(key);
        _sourceKeys.Clear();
        _statuses.Clear();
    }

    private static List<ChatSourceSpec> BuildSpecs(OverlayConfig cfg)
    {
        var specs = new List<ChatSourceSpec>();
        bool wants(ChatMode m) => cfg.Mode == ChatMode.All || cfg.Mode == m;

        if (wants(ChatMode.Twitch) && !string.IsNullOrWhiteSpace(cfg.TwitchChannel))
            specs.Add(ChatSources.Twitch(cfg.TwitchChannel));
        if (wants(ChatMode.Kick) && !string.IsNullOrWhiteSpace(cfg.KickChannel))
            specs.Add(ChatSources.Kick(cfg.KickChannel, cfg.KickChatroomId));
        if (wants(ChatMode.YouTube) && !string.IsNullOrWhiteSpace(cfg.YouTubeUrl))
            specs.Add(ChatSources.YouTube(cfg.YouTubeUrl));
        if (wants(ChatMode.TikTok) && !string.IsNullOrWhiteSpace(cfg.TikTokHandle))
            specs.Add(ChatSources.TikTok(cfg.TikTokHandle));
        return specs;
    }

    private readonly Dictionary<string, string> _statuses = new();

    private void UpdateStatus(int generation, string platform, string status)
    {
        if (generation != _sourceGeneration) return;
        _statuses[platform] = status;
        var parts = _statuses.Select(kv => $"{kv.Key}: {kv.Value}");
        StatusText.Text = string.Join("  |  ", parts);
    }

    private void AppendMessage(ChatMessage msg)
    {
        _atBottom = Scroller.VerticalOffset + Scroller.ViewportHeight >= Scroller.ExtentHeight - 30;
        _lines.Add(msg);
        while (_lines.Count > _config.MaxMessages)
            _lines.RemoveAt(0);
        if (_atBottom)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());
    }

    // Right-click a chat line while the overlay is unlocked: moderation actions
    // for Twitch lines, when a Twitch account is logged in. Locked overlays are
    // click-through, so this can only ever happen while unlocked.
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var msg = (e.OriginalSource as FrameworkElement)?.DataContext as ChatMessage;
        if (msg == null || ChatAuthStore.Twitch == null ||
            !string.Equals(msg.Platform, "Twitch", StringComparison.OrdinalIgnoreCase))
        {
            ContextMenu = null;
            return;
        }
        if (ContextMenu is not ContextMenu existing || !ReferenceEquals(existing.Tag, msg))
            ContextMenu = BuildModMenu(msg);
    }

    private ContextMenu? BuildModMenu(ChatMessage msg)
    {
        var menu = new ContextMenu { Tag = msg };
        // Never act on broadcasters/moderators.
        if (!msg.IsMod && msg.AuthorId != null)
        {
            menu.Items.Add(ModItem(msg, $"Timeout {msg.Author} (10 min)", $"timed out {msg.Author} for 10 min",
                (auth, broadcasterId, ct) => ModerationService.TimeoutAsync(auth, broadcasterId, msg.AuthorId!, 600, "kat toolkit", ct)));
            menu.Items.Add(ModItem(msg, $"Timeout {msg.Author} (1 hour)", $"timed out {msg.Author} for 1 hour",
                (auth, broadcasterId, ct) => ModerationService.TimeoutAsync(auth, broadcasterId, msg.AuthorId!, 3600, "kat toolkit", ct)));
            menu.Items.Add(ModItem(msg, $"Ban {msg.Author}", $"banned {msg.Author}",
                (auth, broadcasterId, ct) => ModerationService.BanAsync(auth, broadcasterId, msg.AuthorId!, "kat toolkit", ct)));
        }
        if (msg.MsgId != null)
        {
            menu.Items.Add(ModItem(msg, "Delete message", $"deleted {msg.Author}'s message",
                (auth, broadcasterId, ct) => ModerationService.DeleteMessageAsync(auth, broadcasterId, msg.MsgId!, ct)));
        }
        return menu.Items.Count == 0 ? null : menu;
    }

    private MenuItem ModItem(ChatMessage msg, string header, string okLine,
        Func<TwitchAuthData, string, CancellationToken, Task<string?>> call)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            item.IsEnabled = false;
            try
            {
                string? error = await ModerateAsync(msg, call);
                AppendSystemLine(msg.Platform, error == null ? okLine : $"mod action failed: {error}");
            }
            catch (Exception ex)
            {
                AppendSystemLine(msg.Platform, $"mod action failed: {ex.Message}");
            }
            finally
            {
                item.IsEnabled = true;
            }
        };
        return item;
    }

    private async Task<string?> ModerateAsync(ChatMessage msg,
        Func<TwitchAuthData, string, CancellationToken, Task<string?>> call)
    {
        var auth = ChatAuthStore.Twitch;
        if (auth == null) return "not logged in to Twitch";
        string channel = _config.TwitchChannel.Trim().TrimStart('#');
        if (channel.Length == 0) return "this overlay has no Twitch channel configured";
        string? broadcasterId = await ModerationService.ResolveBroadcasterIdAsync(channel);
        if (broadcasterId == null) return $"could not resolve the channel id for '{channel}'";
        return await call(auth, broadcasterId, CancellationToken.None);
    }

    // A moderation result (or failure) appears as a toolkit line in the chat.
    private void AppendSystemLine(string platform, string text)
    {
        _atBottom = Scroller.VerticalOffset + Scroller.ViewportHeight >= Scroller.ExtentHeight - 30;
        _lines.Add(new ChatMessage { Platform = platform, Author = "toolkit", Text = text });
        while (_lines.Count > _config.MaxMessages)
            _lines.RemoveAt(0);
        if (_atBottom)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
}
