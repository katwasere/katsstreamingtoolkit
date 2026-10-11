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

    // ShutdownMode in App.xaml is OnExplicitShutdown: the main window decides
    // what its close button means. With overlays on screen or a stream live,
    // closing MINIMIZES (deliberate: an accidental X must not kill the
    // overlays mid-stream, and minimizing hides the screen - no leaked
    // personal info on a capture); closing again from the taskbar while
    // minimized, or closing with nothing active, exits the app. Overlays are
    // satellites and die with it either way.
    protected override void OnExit(ExitEventArgs e)
    {
        ChatHub.DisposeAll();
        base.OnExit(e);
    }
}
