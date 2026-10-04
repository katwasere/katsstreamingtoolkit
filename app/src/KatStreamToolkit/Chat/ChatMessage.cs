using System.Windows.Media;

namespace KatStreamToolkit.Chat;

public sealed class ChatMessage
{
    public required string Platform { get; init; }
    public required string Author { get; init; }
    public string? Color { get; init; }
    public required string Text { get; init; }
    public bool IsAction { get; init; }
    public DateTime Received { get; init; } = DateTime.Now;

    public string PlatformTag => Platform switch
    {
        "Twitch" => "T",
        "Kick" => "K",
        "YouTube" => "YT",
        "TikTok" => "TT",
        _ => "?",
    };
}

public static class PlatformColors
{
    public static Color Get(string platform) => platform switch
    {
        "Twitch" => FromHex("#9146FF"),
        "Kick" => FromHex("#53FC18"),
        "YouTube" => FromHex("#FF0000"),
        "TikTok" => FromHex("#FE2C55"),
        _ => FromHex("#8899AA"),
    };

    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return (Color)ColorConverter.ConvertFromString("#" + hex);
    }

    private static readonly Random Rng = new();

    public static string HashColor(string name)
    {
        uint h = 2166136261;
        foreach (var c in name)
        {
            h ^= c;
            h *= 16777619;
        }
        double hue = h % 360 / 360.0;
        var color = FromHsv(hue, 0.65, 0.78);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        int hi = (int)(hue * 6) % 6;
        double f = hue * 6 - Math.Floor(hue * 6);
        value *= 255;
        int v = (int)value;
        int p = (int)(value * (1 - saturation));
        int q = (int)(value * (1 - f * saturation));
        int t = (int)(value * (1 - (1 - f) * saturation));
        return hi switch
        {
            0 => Color.FromRgb((byte)v, (byte)t, (byte)p),
            1 => Color.FromRgb((byte)q, (byte)v, (byte)p),
            2 => Color.FromRgb((byte)p, (byte)v, (byte)t),
            3 => Color.FromRgb((byte)p, (byte)q, (byte)v),
            4 => Color.FromRgb((byte)t, (byte)p, (byte)v),
            _ => Color.FromRgb((byte)v, (byte)p, (byte)q),
        };
    }
}

public static class ChatText
{
    public static string DecodeEntities(string s) => s
        .Replace("&amp;", "&")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&#39;", "'");
}
