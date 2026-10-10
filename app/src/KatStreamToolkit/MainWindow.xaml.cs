using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KatStreamToolkit.Models;
using KatStreamToolkit.ViewModels;
using KatStreamToolkit.Views;

namespace KatStreamToolkit;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<Guid, OverlayWindow> _overlayWindows = new();

    public MainWindow()
    {
        InitializeComponent();
        _vm = (MainViewModel)DataContext;
        // Build stamp in the title: a screenshot of this window always shows
        // exactly which build is running - stale binaries caused real
        // confusion during debugging.
        Title += $"  -  build {File.GetLastWriteTime(GetType().Assembly.Location):yyyy-MM-dd HH:mm}";
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _vm.Shutdown();
            _vm.Save();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RegisterHotkey();

        _vm.OverlayAdded += overlay => OpenOverlay(overlay);
        _vm.OverlayRemoved += overlay => CloseOverlay(overlay);
        _vm.OverlayTestMessages += overlay =>
        {
            if (_overlayWindows.TryGetValue(overlay.Id, out var window))
                window.InjectTestMessages();
        };
        _vm.OverlayChatLine += msg => Dispatcher.BeginInvoke(() =>
        {
            foreach (var window in _overlayWindows.Values)
                window.InjectExternal(msg);
        });

        foreach (var overlay in _vm.Overlays)
            OpenOverlay(overlay);
    }

    private void OpenOverlay(OverlayConfig overlay)
    {
        if (_overlayWindows.ContainsKey(overlay.Id)) return;
        var window = new OverlayWindow(overlay);
        overlay.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(OverlayConfig.TwitchChannel)
                or nameof(OverlayConfig.KickChannel)
                or nameof(OverlayConfig.KickChatroomId)
                or nameof(OverlayConfig.YouTubeUrl)
                or nameof(OverlayConfig.TikTokHandle)
                or nameof(OverlayConfig.Mode))
            {
                window.Dispatcher.BeginInvoke(() => window.RestartSources());
            }
            else if (args.PropertyName is nameof(OverlayConfig.Locked))
            {
                window.Dispatcher.BeginInvoke(() => window.ApplyLockStyle());
            }
            else if (args.PropertyName is nameof(OverlayConfig.IsVisible))
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    if (overlay.IsVisible) window.Show(); else window.Hide();
                });
            }
        };
        _overlayWindows[overlay.Id] = window;
        window.Show();
    }

    private void CloseOverlay(OverlayConfig overlay)
    {
        if (!_overlayWindows.Remove(overlay.Id, out var window)) return;
        window.Close();
    }

    private void ToggleAllLocks()
    {
        foreach (var overlay in _vm.Overlays)
            _vm.ToggleOverlayLock(overlay);
    }

    private void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
        // Two aliases so one colliding app cannot silently kill the toggle:
        // whichever registers works, both toggle all overlays.
        bool c = RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, 0x43);
        bool x = RegisterHotKey(hwnd, HotkeyIdAlias, MOD_CONTROL | MOD_ALT, 0x58);
        if (!c && !x)
            Title += "  (both Ctrl+Alt+C and Ctrl+Alt+X are taken - the overlay lock hotkey is off)";
        else if (!c)
            Title += "  (Ctrl+Alt+C is taken - use Ctrl+Alt+X to lock/unlock overlays)";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && (wParam.ToInt32() == HotkeyId || wParam.ToInt32() == HotkeyIdAlias))
        {
            ToggleAllLocks();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private const int HotkeyId = 0xCA7;
    private const int HotkeyIdAlias = 0xCA8;

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, HotkeyId);
        base.OnClosed(e);
    }

    private void PreviewBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            box.ScrollToEnd();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
