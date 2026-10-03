using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit.Views;

// Renders one chat line: platform dot + colored author + text.
public class ChatLineTextBlock : TextBlock
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(ChatLineTextBlock),
        new PropertyMetadata(null, OnMessageChanged));

    public ChatMessage? Message
    {
        get => (ChatMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChatLineTextBlock)d).Render();

    private void Render()
    {
        Inlines.Clear();
        var msg = Message;
        if (msg == null) return;

        var platformBrush = new SolidColorBrush(PlatformColors.Get(msg.Platform));
        platformBrush.Freeze();

        var authorColor = msg.Color;
        if (string.IsNullOrWhiteSpace(authorColor) || !authorColor.StartsWith('#'))
            authorColor = PlatformColors.HashColor(msg.Author);
        var authorBrush = new SolidColorBrush(PlatformColors.FromHex(authorColor.Length == 7 ? authorColor : "#" + authorColor));
        authorBrush.Freeze();

        var textBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        textBrush.Freeze();

        var dimBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8));
        dimBrush.Freeze();

        Inlines.Add(new Run(msg.PlatformTag + " ") { Foreground = platformBrush, FontWeight = FontWeights.Bold });
        Inlines.Add(new Run(msg.Author) { Foreground = authorBrush, FontWeight = FontWeights.Bold });
        if (msg.IsAction)
        {
            Inlines.Add(new Run(" " + msg.Text) { Foreground = authorBrush, FontStyle = FontStyles.Italic });
        }
        else
        {
            Inlines.Add(new Run(": ") { Foreground = dimBrush });
            Inlines.Add(new Run(msg.Text) { Foreground = textBrush });
        }
    }
}
