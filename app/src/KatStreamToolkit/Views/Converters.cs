using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KatStreamToolkit.Models;

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

public class NullToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        if (Inverted) isNull = !isNull;
        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EqualsToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        if (Inverted) equal = !equal;
        return equal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class LightBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Idle = Freeze(Color.FromRgb(0x9A, 0xA0, 0xA8));
    private static readonly SolidColorBrush Error = Freeze(Color.FromRgb(0xE2, 0x57, 0x4A));
    private static readonly SolidColorBrush Unknown = Freeze(Color.FromRgb(0x5A, 0x60, 0x69));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is VerifyLight light ? light switch
        {
            VerifyLight.Ok => Ok,
            VerifyLight.Idle => Idle,
            VerifyLight.Error => Error,
            _ => Unknown,
        } : Unknown;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
