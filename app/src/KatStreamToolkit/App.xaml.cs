using System.Windows;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        ChatHub.DisposeAll();
        base.OnExit(e);
    }
}
