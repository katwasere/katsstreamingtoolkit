using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

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
        {
            var channel = cfg.TwitchChannel.Trim().TrimStart('#').ToLowerInvariant();
            specs.Add(new ChatSourceSpec("Twitch", $"twitch:{channel}",
                () => new TwitchChatClient(channel)));
        }
        if (wants(ChatMode.Kick) && !string.IsNullOrWhiteSpace(cfg.KickChannel))
        {
            var channel = cfg.KickChannel.Trim();
            var manual = string.IsNullOrWhiteSpace(cfg.KickChatroomId) ? null : cfg.KickChatroomId.Trim();
            specs.Add(new ChatSourceSpec("Kick", $"kick:{manual ?? channel.ToLowerInvariant()}",
                () => new KickChatClient(channel, manual)));
        }
        if (wants(ChatMode.YouTube) && !string.IsNullOrWhiteSpace(cfg.YouTubeUrl))
        {
            var url = cfg.YouTubeUrl.Trim();
            specs.Add(new ChatSourceSpec("YouTube", $"yt:{url.ToLowerInvariant()}",
                () => new YouTubeChatClient(url)));
        }
        if (wants(ChatMode.TikTok) && !string.IsNullOrWhiteSpace(cfg.TikTokHandle))
        {
            var handle = cfg.TikTokHandle.Trim().TrimStart('@').ToLowerInvariant();
            specs.Add(new ChatSourceSpec("TikTok", $"tiktok:{handle}",
                () => new TikTokChatClient(handle)));
        }
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

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
}
