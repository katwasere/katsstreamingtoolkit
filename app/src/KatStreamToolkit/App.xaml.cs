using System.Threading;
using System.Windows;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit;

public partial class App : Application
{
    // Held for the process lifetime: a second instance would open its own
    // overlay windows that look identical to the first instance's - and the
    // old instance is nearly invisible (overlays are not in the taskbar).
    private static readonly Mutex SingleInstance = new(false, @"Local\KatStreamToolkit.SingleInstance");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstance.WaitOne(TimeSpan.Zero))
        {
            MessageBox.Show(
                "KAT's Streaming Toolkit is already running.\n\n" +
                "Close the existing window first (Alt-Tab to it; the chat overlay windows themselves never appear in the taskbar). " +
                "Two instances would show two identical-looking overlays - and only the old one's would update.",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ChatHub.DisposeAll();
        base.OnExit(e);
    }
}
