using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using KatStreamToolkit.Models;
using KatStreamToolkit.Services;

namespace KatStreamToolkit.Views;

// The poll/prediction widget: one small always-on-top window, click-through
// when locked (Ctrl+Alt+C toggles it with the chat overlays). Hides itself
// whenever no poll/prediction is running - no permanent dead box on screen.
public partial class PollOverlayWindow : Window
{
    private readonly PollWidgetConfig _config;

    public PollOverlayWindow(PollWidgetConfig config)
    {
        InitializeComponent();
        _config = config;
        DataContext = _config;
        SetBinding(FontSizeProperty, new System.Windows.Data.Binding(nameof(PollWidgetConfig.FontSize)) { Source = _config });

        Left = _config.X;
        Top = _config.Y;
        Width = _config.Width;
        if (_config.X == 0 && _config.Y == 0)
        {
            // First run: bottom-left corner, away from the default chat overlay spot.
            Left = 48;
            Top = SystemParameters.WorkArea.Height - 260;
        }

        Closed += (_, _) => Persist();
        // The lock style needs the hwnd; re-apply after every Show as well,
        // because a fresh handle loses the WS_EX_TRANSPARENT flag.
        SourceInitialized += (_, _) => ApplyLockStyle();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
            Persist();
        }
    }

    private void Persist()
    {
        _config.X = Left;
        _config.Y = Top;
        _config.Width = ActualWidth;
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

    // New poll state from any thread (service loop -> view model -> dispatcher).
    // Null = nothing running: hide instead of showing an empty box. The window
    // is NEVER shown by its creator - only here, and only with real content.
    public void Update(PollService.PollState? state)
    {
        PollPanel.DataContext = state;
        if (state == null)
        {
            if (IsVisible) Hide();
        }
        else
        {
            if (!IsVisible)
            {
                Show();
                ApplyLockStyle();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
}
