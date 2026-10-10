using System.Windows;

namespace KatStreamToolkit.Views;

public partial class ChatDiagnosticsWindow : Window
{
    public ChatDiagnosticsWindow() => InitializeComponent();

    public string Transcript
    {
        get => TranscriptBox.Text;
        set => TranscriptBox.Text = value;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TranscriptBox.Text); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
