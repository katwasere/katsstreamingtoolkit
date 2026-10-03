using System.Windows;
using System.Windows.Controls;

namespace KatStreamToolkit.Views;

// TextBox that displays dots unless it currently has keyboard focus.
// Bind SecretText (not Text): the real value is always in SecretText, the
// visible Text is masked the moment you click away - safe if it shows on stream.
public class SecretBox : TextBox
{
    private const string Mask = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public static readonly DependencyProperty SecretTextProperty =
        DependencyProperty.Register(nameof(SecretText), typeof(string), typeof(SecretBox),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSecretTextChanged));

    public string SecretText
    {
        get => (string)GetValue(SecretTextProperty);
        set => SetValue(SecretTextProperty, value);
    }

    private bool _updating;
    private string _real = "";

    public SecretBox()
    {
        GotKeyboardFocus += (_, _) => Reveal();
        LostKeyboardFocus += (_, _) => RefreshDisplay();
    }

    private static void OnSecretTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SecretBox)d;
        box._real = (string?)e.NewValue ?? "";
        box.RefreshDisplay();
    }

    private void Reveal()
    {
        _updating = true;
        Text = _real;
        CaretIndex = Text.Length;
        _updating = false;
    }

    private void RefreshDisplay()
    {
        _updating = true;
        Text = !IsKeyboardFocusWithin && !string.IsNullOrEmpty(_real) ? Mask : _real;
        _updating = false;
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (_updating) return;
        _real = Text;
        if (IsKeyboardFocusWithin)
            SecretText = Text;
        else
            RefreshDisplay();
    }
}
