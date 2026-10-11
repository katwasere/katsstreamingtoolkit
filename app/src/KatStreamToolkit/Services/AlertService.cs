using System.IO;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// Alert pipeline: owns the Twitch EventSub connection and fans every alert out
// to the overlays (plain text lines - no animations on purpose, they would be
// distracting in-game) and, when enabled, to a rolling text file that OBS
// reads with a Text (GDI+) source + Scroll filter.
public sealed class AlertService : IDisposable
{
    private readonly AppConfig _config;
    private readonly TwitchEventSubClient _client = new();
    private readonly object _bannerGate = new();
    private readonly List<string> _bannerLines = new();
    // Serializes the file writes: AtomicWrite's temp path is fixed, so two
    // concurrent writes (e.g. a test alert landing mid-real-alert) could
    // otherwise interleave and lose one.
    private readonly SemaphoreSlim _bannerWriteGate = new(1, 1);

    // Raised on a background thread; the subscriber marshals to the UI.
    public event Action<AlertEvent>? Alert;
    public event Action<string>? Status;

    public AlertService(AppConfig config)
    {
        _config = config;
        _client.Alert += OnClientAlert;
        _client.Status += s => Status?.Invoke(s);
    }

    public static string DefaultBannerPath => Path.Combine(ConfigStore.DirectoryPath, "alerts.txt");

    // Keeps the watched channel set in sync with the config (My channels +
    // every overlay's Twitch channel). Called on channel edits, login changes
    // and master-toggle flips; a no-op set change only wakes the loop.
    public void SyncChannels()
    {
        var channels = new List<string>();
        if (_config.Alert.Enabled)
        {
            var mc = _config.MyChannels;
            if (!string.IsNullOrWhiteSpace(mc.TwitchChannel)) channels.Add(mc.TwitchChannel);
            foreach (var o in _config.Overlays)
            {
                if (!string.IsNullOrWhiteSpace(o.TwitchChannel)) channels.Add(o.TwitchChannel);
            }
        }
        _client.SetChannels(channels.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // Auth changed (login/logout/refresh): the loop re-resolves auth on its
    // next iteration; a nudge skips its backoff wait.
    public void Nudge() => _client.Nudge();

    private void OnClientAlert(AlertEvent alert)
    {
        var cfg = _config.Alert;
        if (!cfg.Enabled) return;
        bool shown = alert.Type switch
        {
            AlertType.Follow => cfg.ShowFollows,
            AlertType.Sub => cfg.ShowSubs,
            AlertType.Raid => cfg.ShowRaids,
            _ => false,
        };
        if (!shown) return;
        WriteBanner(alert);
        Alert?.Invoke(alert);
    }

    // Local test alert through the same pipeline (overlay lines + banner file),
    // so what Kat verifies is exactly what a real alert does.
    public void InjectTest(AlertType type)
    {
        string name = $"testviewer{Random.Shared.Next(100, 999)}";
        var alert = new AlertEvent
        {
            Platform = "Twitch",
            Type = type,
            User = name,
            IsTest = true,
            Tier = type == AlertType.Sub ? "1000" : null,
            Months = type == AlertType.Sub ? 3 : 0,
            Viewers = type == AlertType.Raid ? Random.Shared.Next(2, 40) : 0,
            Message = type == AlertType.Sub ? "welcome to the channel!" : null,
        };
        OnClientAlert(alert);
    }

    // ---------- OBS banner file ----------

    private void WriteBanner(AlertEvent alert)
    {
        var cfg = _config.Alert;
        if (!cfg.BannerEnabled) return;
        string path = string.IsNullOrWhiteSpace(cfg.BannerFilePath)
            ? DefaultBannerPath
            : cfg.BannerFilePath.Trim();
        string line = alert.IsTest ? $"[test] {alert.Display}" : alert.Display;
        List<string> snapshot;
        lock (_bannerGate)
        {
            _bannerLines.Add(line);
            while (_bannerLines.Count > Math.Max(1, cfg.BannerMaxLines))
                _bannerLines.RemoveAt(0);
            snapshot = _bannerLines.ToList();
        }
        // Off the calling thread (test buttons run on the UI thread; real
        // alerts arrive on the websocket thread) and serialized, so a click
        // never blocks on disk and two writes can never interleave.
        _ = Task.Run(async () =>
        {
            try
            {
                await _bannerWriteGate.WaitAsync();
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    ConfigStore.AtomicWrite(path, string.Join(Environment.NewLine, snapshot) + Environment.NewLine);
                }
                finally { _bannerWriteGate.Release(); }
            }
            catch (Exception ex)
            {
                Status?.Invoke($"alerts: could not write the banner file ({ex.Message})");
            }
        });
    }

    public void Dispose() => _client.Dispose();
}
