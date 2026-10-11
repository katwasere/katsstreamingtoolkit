using System.Text.RegularExpressions;

namespace KatStreamToolkit.Services;

// The shared chat filter: one word list that every overlay honors. Matching
// chat lines are dropped before they render, so they never reach the stream
// capture. Static like the other shared services - overlays only receive their
// OverlayConfig, not the app config, and the list is edited once in the
// Channels & account tab. Toolkit lines (alerts, !command replies, moderation
// results, test messages) bypass the filter on purpose: it can never silence
// the toolkit's own voice.
public static class ChatFilter
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static Regex[] _patterns = [];

    // Rebuilt on every settings change; matching runs per line, so the list is
    // kept small (regex array) and never touches the UI thread for long.
    public static void Configure(bool enabled, string words)
    {
        var patterns = new List<Regex>();
        if (enabled)
        {
            foreach (var raw in words.Split('\n'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0 || entry.StartsWith('#')) continue;
                // Literal text, never regex. Lookarounds instead of \b so an
                // entry with a symbol edge (e.g. "!slur") still matches - \b
                // needs a word character on the other side.
                patterns.Add(new Regex(
                    "(?<!\\w)" + Regex.Escape(entry) + "(?!\\w)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
        }
        lock (Gate)
        {
            _enabled = enabled;
            _patterns = [.. patterns];
        }
    }

    // True when the line text hits any entry (case-insensitive whole-word).
    public static bool ShouldHide(string text)
    {
        if (!_enabled) return false;
        lock (Gate)
        {
            foreach (var p in _patterns)
                if (p.IsMatch(text)) return true;
            return false;
        }
    }
}
