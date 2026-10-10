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
    private readonly List<ChatEntry> _entries = new();
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _statusTimer;
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

        MouseRightButtonUp += OnOverlayRightClick;

        // Keeps the header status line honest even when nothing changes
        // (client counters move without a status event).
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RenderStatus();
        _statusTimer.Start();

        Closed += (_, _) =>
        {
            PersistGeometry();
            ReleaseSources();
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
            _entries.Add(entry);
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
        _entries.Clear();
        _statuses.Clear();
        _lineCounts.Clear();
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
    private readonly Dictionary<string, int> _lineCounts = new();

    private void UpdateStatus(int generation, string platform, string status)
    {
        if (generation != _sourceGeneration) return;
        _statuses[platform] = status;
        RenderStatus();
    }

    // Header status with live counters - "raw" lines straight off the wire vs
    // "in" parsed chat messages vs "shown" rendered lines (incl. test lines).
    // raw climbing + in stuck = the app drops messages (my bug);
    // raw stuck = the connection receives nothing (network/platform side).
    private void RenderStatus()
    {
        var parts = _statuses.Select(kv =>
        {
            string s = kv.Value;
            var entry = _entries.FirstOrDefault(e => e.Client.PlatformName == kv.Key);
            if (entry?.Client is IChatStats st)
                s += $" - raw {st.RawLines}, in {st.ChatMessages}";
            if (_lineCounts.TryGetValue(kv.Key, out int shown) && shown > 0)
                s += $", shown {shown}";
            return $"{kv.Key}: {s}";
        });
        StatusText.Text = string.Join("  |  ", parts);
    }

    private void AppendMessage(ChatMessage msg)
    {
        _atBottom = Scroller.VerticalOffset + Scroller.ViewportHeight >= Scroller.ExtentHeight - 30;
        _lines.Add(msg);
        _lineCounts[msg.Platform] = _lineCounts.GetValueOrDefault(msg.Platform) + 1;
        RenderStatus();
        while (_lines.Count > _config.MaxMessages)
            _lines.RemoveAt(0);
        if (_atBottom)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());
    }

    // Right-click a chat line while the overlay is unlocked: moderation actions
    // for Twitch lines, when a Twitch account is logged in. The menu is opened
    // MANUALLY on MouseRightButtonUp - the ContextMenuService never raised
    // ContextMenuOpening on these chromeless windows (it only opens menus it
    // can already find, and ours is built from the clicked line).
    private void OnOverlayRightClick(object sender, MouseButtonEventArgs e)
    {
        var msg = FindClickedMessage(e.OriginalSource as DependencyObject);
        if (msg == null) return;
        e.Handled = true;

        // Never fail silently again: right-clicking an actual chat line always
        // shows SOMETHING (the old build just returned, which looked broken).
        if (!string.Equals(msg.Platform, "Twitch", StringComparison.OrdinalIgnoreCase))
        {
            ShowMenuNote($"{msg.Platform} moderation isn't wired up yet - Twitch only for now");
            return;
        }
        if (ChatAuthStore.Twitch == null)
        {
            ShowMenuNote("log in to Twitch (Chat Overlays tab) to moderate this line");
            return;
        }

        var menu = BuildModMenu(msg);
        if (menu == null) return;
        menu.PlacementTarget = Scroller;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // A right-click on the chat TEXT lands on a Run inside the line's
    // TextBlock - and Run is a FrameworkContentElement, NOT a FrameworkElement,
    // so a plain cast came back null and the menu never opened (clicking blank
    // padding worked, clicking the words didn't). Walk up the content and
    // visual trees until something carries a ChatMessage.
    private static ChatMessage? FindClickedMessage(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is FrameworkElement { DataContext: ChatMessage msg })
                return msg;
            node = node switch
            {
                System.Windows.FrameworkContentElement content => content.Parent,
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    => System.Windows.Media.VisualTreeHelper.GetParent(node),
                _ => LogicalTreeHelper.GetParent(node),
            };
        }
        return null;
    }

    private void ShowMenuNote(string text)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = text, IsEnabled = false });
        menu.PlacementTarget = Scroller;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu? BuildModMenu(ChatMessage msg)
    {
        var menu = new ContextMenu { Tag = msg };
        var auth = ChatAuthStore.Twitch;

        // Timeout/ban are hidden for lines where they cannot work: the
        // broadcaster themselves, or our own logged-in account. (Moderators
        // CAN be moderated by the broadcaster - the API errors otherwise and
        // the error is shown in the overlay.)
        bool ownLine = auth != null &&
                       string.Equals(msg.Author, auth.Login, StringComparison.OrdinalIgnoreCase);
        bool canModerateTarget = !msg.IsBroadcaster && !ownLine && msg.AuthorId != null;
        if (canModerateTarget)
        {
            menu.Items.Add(ModItem(msg, $"Timeout {msg.Author} (10 min)", $"timed out {msg.Author} for 10 min",
                (a, broadcasterId, ct) => ModerationService.TimeoutAsync(a, broadcasterId, msg.AuthorId!, 600, "kat toolkit", ct)));
            menu.Items.Add(ModItem(msg, $"Timeout {msg.Author} (1 hour)", $"timed out {msg.Author} for 1 hour",
                (a, broadcasterId, ct) => ModerationService.TimeoutAsync(a, broadcasterId, msg.AuthorId!, 3600, "kat toolkit", ct)));
            menu.Items.Add(ModItem(msg, $"Ban {msg.Author}", $"banned {msg.Author}",
                (a, broadcasterId, ct) => ModerationService.BanAsync(a, broadcasterId, msg.AuthorId!, "kat toolkit", ct)));
        }
        if (msg.MsgId != null)
        {
            menu.Items.Add(ModItem(msg, "Delete message", $"deleted {msg.Author}'s message",
                (a, broadcasterId, ct) => ModerationService.DeleteMessageAsync(a, broadcasterId, msg.MsgId!, ct)));
        }
        if (!canModerateTarget)
        {
            // Say WHY instead of silently hiding the items.
            string why = msg.IsBroadcaster ? "broadcaster - cannot be timed out or banned"
                : ownLine ? "your own line - no timeout/ban"
                : "no user id on this line - no timeout/ban";
            menu.Items.Insert(0, new MenuItem { Header = why, IsEnabled = false });
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

    // One external line (a fired !command shown on the overlays), from any
    // thread.
    public void InjectExternal(ChatMessage msg) => Dispatcher.BeginInvoke(() => AppendMessage(msg));

    // Fake chat lines injected straight into the list - verifies placement,
    // size, wrapping and colors without waiting for (or needing) real chat.
    public void InjectTestMessages()
    {
        var platforms = _statuses.Count > 0
            ? _statuses.Keys.ToList()
            : new List<string> { "Twitch", "Kick", "YouTube", "TikTok" };
        string first = platforms[0];

        var tests = new List<ChatMessage>();
        for (int i = 0; i < platforms.Count; i++)
            tests.Add(new ChatMessage
            {
                Platform = platforms[i],
                Author = $"testviewer{i + 1}",
                Text = $"test message from {platforms[i]} - if you can read this, the overlay renders",
            });
        tests.Add(new ChatMessage
        {
            Platform = first,
            Author = "longtext",
            Text = "a much longer line to check wrapping: the quick brown fox jumps over the lazy dog " +
                   "again and again until it wraps onto a second line",
        });
        tests.Add(new ChatMessage
        {
            Platform = first,
            Author = "chatter",
            Text = "waves at the camera",
            IsAction = true,
        });
        foreach (var msg in tests)
            AppendMessage(msg);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
}
