using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KatStreamToolkit.Views;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Inverted) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}
