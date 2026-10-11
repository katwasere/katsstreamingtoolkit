using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit.Views;

// Renders one chat line: platform dot + colored author + text. Text and alert
// colors come from the owning overlay's config (bound per overlay window);
// invalid hex falls back to the built-in defaults so half-typed input is safe.
public class ChatLineTextBlock : TextBlock
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(ChatLineTextBlock),
        new PropertyMetadata(null, OnRenderChanged));

    public static readonly DependencyProperty TextColorProperty = DependencyProperty.Register(
        nameof(TextColor), typeof(string), typeof(ChatLineTextBlock),
        new PropertyMetadata("#EEEEEE", OnRenderChanged));

    public static readonly DependencyProperty AlertColorProperty = DependencyProperty.Register(
        nameof(AlertColor), typeof(string), typeof(ChatLineTextBlock),
        new PropertyMetadata("#FFD75E", OnRenderChanged));

    public ChatMessage? Message
    {
        get => (ChatMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string TextColor
    {
        get => (string)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public string AlertColor
    {
        get => (string)GetValue(AlertColorProperty);
        set => SetValue(AlertColorProperty, value);
    }

    private static void OnRenderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChatLineTextBlock)d).Render();

    // Parses "#RGB"/"#RRGGBB"/"#AARRGGBB"; anything else (or a color name the
    // parser rejects) keeps the overlay usable instead of throwing mid-edit.
    private static SolidColorBrush BrushOrDefault(string? hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
        }
        catch { }
        var fb = new SolidColorBrush(fallback);
        fb.Freeze();
        return fb;
    }

    private void Render()
    {
        Inlines.Clear();
        var msg = Message;
        if (msg == null) return;

        var platformBrush = new SolidColorBrush(PlatformColors.Get(msg.Platform));
        platformBrush.Freeze();

        // Alerts are plain text in their configured color - deliberately no
        // banners, slides or fades (distracting mid-game), just a line that
        // stands out.
        if (msg.Alert != null)
        {
            var alertBrush = BrushOrDefault(AlertColor, Color.FromRgb(0xFF, 0xD7, 0x5E));
            Inlines.Add(new Run(msg.PlatformTag + " ") { Foreground = platformBrush, FontWeight = FontWeights.Bold });
            Inlines.Add(new Run("\u2605 ") { Foreground = alertBrush, FontWeight = FontWeights.Bold });
            Inlines.Add(new Run(msg.Alert.Display) { Foreground = alertBrush, FontWeight = FontWeights.SemiBold });
            if (msg.Alert.IsTest)
                Inlines.Add(new Run("  [test]") { Foreground = platformBrush, FontStyle = FontStyles.Italic });
            return;
        }

        // BUG-50: platform-provided colors are NOT guaranteed well-formed
        // (Twitch sends 3-digit hexes like "#F53", Kick is free-form) and
        // PlatformColors.FromHex throws on anything but #RGB...-length input -
        // an exception here fires inside the dependency-property callback and
        // takes the app down mid-stream. Only accept strict hex, otherwise
        // fall back to the deterministic hash color.
        var authorColor = msg.Color;
        if (string.IsNullOrWhiteSpace(authorColor)
            || !Regex.IsMatch(authorColor, "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"))
            authorColor = PlatformColors.HashColor(msg.Author);
        var authorBrush = new SolidColorBrush(PlatformColors.FromHex(authorColor));
        authorBrush.Freeze();

        var textBrush = BrushOrDefault(TextColor, Color.FromRgb(0xEE, 0xEE, 0xEE));

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
