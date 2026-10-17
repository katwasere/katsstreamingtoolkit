using System.Text.Json.Serialization;

namespace KatStreamToolkit.Models;

public enum Platform
{
    Twitch,
    YouTube,
    Kick,
    TikTok,
    Custom,
}

public enum Orientation
{
    Landscape,
    Portrait,
}

public enum PortraitStyle
{
    CenterCrop,
    BlurredBackground,
    Custom,
}

public enum ChatMode
{
    All,
    Twitch,
    Kick,
    YouTube,
    TikTok,
}

public enum VerifyLight
{
    Unknown,
    Idle,
    Ok,
    Error,
}

public enum LayerType
{
    Source,
    Image,
    Blur,
    BackgroundReveal,
}

// One piece of a destination's Custom composite. Everything is normalized (0-1):
// source rects against the input frame, output rects against the 1080x1920 output.
// List order = z-order (first = back).
public class OutputLayer : ObservableBase
{
    private Guid _id = Guid.NewGuid();
    private LayerType _type = LayerType.Source;
    private string _name = "";
    private double _srcX, _srcY, _srcW, _srcH;
    private double _x, _y, _w = 1, _h = 1;
    private string _path = "";

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public LayerType Type { get => _type; set => Set(ref _type, value); }

    public string Name
    {
        get => _name;
        set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? DefaultName(_type) : value.Trim());
    }

    // Source layers: which part of the input this cut takes.
    public double SrcX { get => _srcX; set => Set(ref _srcX, Math.Clamp(value, 0, 1)); }
    public double SrcY { get => _srcY; set => Set(ref _srcY, Math.Clamp(value, 0, 1)); }
    public double SrcW { get => _srcW; set => Set(ref _srcW, Math.Clamp(value, 0.005, 1)); }
    public double SrcH { get => _srcH; set => Set(ref _srcH, Math.Clamp(value, 0.005, 1)); }

    // Where the layer sits in the output frame.
    public double X { get => _x; set => Set(ref _x, Math.Clamp(value, 0, 1)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(value, 0, 1)); }
    public double W { get => _w; set => Set(ref _w, Math.Clamp(value, 0.005, 1)); }
    public double H { get => _h; set => Set(ref _h, Math.Clamp(value, 0.005, 1)); }

    // Image layers: local PNG (transparency supported); shipped with the bundle.
    public string Path { get => _path; set => Set(ref _path, value); }

    public static string DefaultName(LayerType type) => type switch
    {
        LayerType.Source => "Source cut",
        LayerType.Image => "Image overlay",
        LayerType.Blur => "Blur area",
        LayerType.BackgroundReveal => "Show background",
        _ => "Layer",
    };
}

public class UpstreamConfig : ObservableBase
{
    private int _videoBitrateKbps = 8000;
    private int _fps = 60;
    private int _width = 1920;
    private int _height = 1080;
    private string _streamName = "kat-" + Guid.NewGuid().ToString("N")[..10];

    public int VideoBitrateKbps { get => _videoBitrateKbps; set => Set(ref _videoBitrateKbps, value); }
    public int Fps { get => _fps; set => Set(ref _fps, value); }
    public int Width { get => _width; set => Set(ref _width, value); }
    public int Height { get => _height; set => Set(ref _height, value); }

    [JsonIgnore]
    public string StreamName { get => _streamName; set => Set(ref _streamName, value); }

    [JsonIgnore]
    public double AspectRatio => _width <= 0 || _height <= 0 ? 16.0 / 9 : (double)_width / _height;}

public class MyChannelsConfig : ObservableBase
{
    private string _twitchChannel = "";
    private string _kickChannel = "";
    private string _youTubeUrl = "";
    private string _tikTokHandle = "";

    public string TwitchChannel { get => _twitchChannel; set => Set(ref _twitchChannel, value); }
    public string KickChannel { get => _kickChannel; set => Set(ref _kickChannel, value); }
    public string YouTubeUrl { get => _youTubeUrl; set => Set(ref _youTubeUrl, value); }
    public string TikTokHandle { get => _tikTokHandle; set => Set(ref _tikTokHandle, value); }
}

public class AppConfig : ObservableBase
{
    private UpstreamConfig _upstream = new();
    private MyChannelsConfig _myChannels = new();
    private string _serverHost = "";
    private List<DestinationConfig> _destinations = new();
    private List<OverlayConfig> _overlays = new();
    private List<CommandConfig> _commands = new();
    private bool _watchdogAutoRestart = true;
    private bool _alarmSound = true;
    private bool _obsEnabled;
    private string _obsWebSocketUrl = "ws://127.0.0.1:4455";
    private string _twitchClientId = "";
    private AlertConfig _alert = new();

    public UpstreamConfig Upstream { get => _upstream; set => Set(ref _upstream, value); }
    public MyChannelsConfig MyChannels { get => _myChannels; set => Set(ref _myChannels, value); }

    [JsonIgnore]
    public string ServerHost { get => _serverHost; set => Set(ref _serverHost, value); }

    public string KeysFilePath { get; set; } = "";

    private string _sshUser = "";
    private bool _sshUseKey;
    private string _sshKeyPath = "";
    private string _remotePath = "/opt/kat-relay";

    public string SshUser { get => _sshUser; set => Set(ref _sshUser, value); }
    public bool SshUseKey { get => _sshUseKey; set => Set(ref _sshUseKey, value); }
    public string SshKeyPath { get => _sshKeyPath; set => Set(ref _sshKeyPath, value); }
    public string RemotePath { get => _remotePath; set => Set(ref _remotePath, value); }
    public List<DestinationConfig> Destinations { get => _destinations; set => Set(ref _destinations, value); }
    public List<OverlayConfig> Overlays { get => _overlays; set => Set(ref _overlays, value); }
    public List<CommandConfig> Commands { get => _commands; set => Set(ref _commands, value); }

    // Watchdog: kill a frozen encoder so nginx respawns it, and beep when a new
    // alarm appears. Both persist with the rest of the config.
    public bool WatchdogAutoRestart { get => _watchdogAutoRestart; set => Set(ref _watchdogAutoRestart, value); }
    public bool AlarmSound { get => _alarmSound; set => Set(ref _alarmSound, value); }

    // OBS control via obs-websocket (same PC by default). The password lives in
    // secrets.json, never here.
    public bool ObsEnabled { get => _obsEnabled; set => Set(ref _obsEnabled, value); }
    public string ObsWebSocketUrl { get => _obsWebSocketUrl; set => Set(ref _obsWebSocketUrl, value); }

    // Twitch application Client ID (public, not a secret) - required once for
    // the account login that powers chat send, !commands and moderation.
    public string TwitchClientId { get => _twitchClientId; set => Set(ref _twitchClientId, value); }

    // Follows/subs/raids (see AlertConfig). Persists with the rest of the config.
    public AlertConfig Alert { get => _alert; set => Set(ref _alert, value); }

    // The shared chat filter (see ModerationConfig). Persists with the config.
    public ModerationConfig Moderation { get => _moderation; set => Set(ref _moderation, value); }
    private ModerationConfig _moderation = new();

    private int _keyRotationReminderDays = 90;

    // Key rotation nag interval in days (0 = off). A destination whose key was
    // marked rotated longer ago than this (or never) feeds the Relay tab's
    // key-rotation warning line.
    public int KeyRotationReminderDays
    {
        get => _keyRotationReminderDays;
        set => Set(ref _keyRotationReminderDays, Math.Clamp(value, 0, 3650));
    }

    private double _bandwidthMonthlyGb;

    // Monthly traffic allowance for the server's outgoing streams (0 = off).
    // The bandwidth watchdog compares the CONFIGURED outputs' projected monthly
    // usage against this and warns while there is still time to react.
    public double BandwidthMonthlyGb
    {
        get => _bandwidthMonthlyGb;
        set => Set(ref _bandwidthMonthlyGb, Math.Clamp(value, 0, 1_000_000));
    }

    // Automatic OBS scene changes on alert events (see ObsAutoSwitchConfig).
    public ObsAutoSwitchConfig ObsAutoSwitch { get => _obsAutoSwitch; set => Set(ref _obsAutoSwitch, value); }
    private ObsAutoSwitchConfig _obsAutoSwitch = new();

    // The poll/prediction widget (see PollWidgetConfig).
    public PollWidgetConfig PollWidget { get => _pollWidget; set => Set(ref _pollWidget, value); }
    private PollWidgetConfig _pollWidget = new();
}

// One OBS scene change per alert type, driven by the alert feed. Scene names
// must match OBS exactly (same convention as the Commands tab). Hold 0 means
// stay on the scene until switched back manually.
public class ObsAutoSwitchConfig : ObservableBase
{
    private bool _enabled;
    private string _raidScene = "";
    private int _raidHoldSeconds = 30;
    private string _subScene = "";
    private int _subHoldSeconds = 10;
    private string _followScene = "";
    private int _followHoldSeconds = 10;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public string RaidScene { get => _raidScene; set => Set(ref _raidScene, value.Trim()); }
    public string SubScene { get => _subScene; set => Set(ref _subScene, value.Trim()); }
    public string FollowScene { get => _followScene; set => Set(ref _followScene, value.Trim()); }

    public int RaidHoldSeconds { get => _raidHoldSeconds; set => Set(ref _raidHoldSeconds, Math.Clamp(value, 0, 600)); }
    public int SubHoldSeconds { get => _subHoldSeconds; set => Set(ref _subHoldSeconds, Math.Clamp(value, 0, 600)); }
    public int FollowHoldSeconds { get => _followHoldSeconds; set => Set(ref _followHoldSeconds, Math.Clamp(value, 0, 600)); }
}

// The on-screen poll/prediction widget: one small click-through window showing
// the broadcaster's active Twitch poll or prediction (plain text bars, same
// no-distraction philosophy as the chat overlays). Needs the Twitch login.
public class PollWidgetConfig : ObservableBase
{
    private bool _enabled;
    private bool _showPredictions = true;
    private bool _locked;
    private double _x;
    private double _y;
    private double _width = 340;
    private double _fontSize = 14;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // Predictions render when no poll is running; off = polls only.
    public bool ShowPredictions { get => _showPredictions; set => Set(ref _showPredictions, value); }

    // Locked = click-through (Ctrl+Alt+C toggles it together with the overlays).
    public bool Locked { get => _locked; set => Set(ref _locked, value); }

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, Math.Clamp(value, 180, 800)); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 8, 48)); }
}

// One toolkit-handled !command: reply in chat and/or switch an OBS scene.
public class CommandConfig : ObservableBase
{
    private string _name = "";
    private string _response = "";
    private string _scene = "";
    private bool _enabled = true;
    private bool _showOnOverlay = true;
    private int _cooldownSeconds = 5;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Response { get => _response; set => Set(ref _response, value); }
    public string Scene { get => _scene; set => Set(ref _scene, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, Math.Clamp(value, 0, 600)); }

    // When on, firing the command shows the reply (or who used it) as a "bot"
    // line in the chat overlays.
    public bool ShowOnOverlay { get => _showOnOverlay; set => Set(ref _showOnOverlay, value); }
}

// Alert settings (global - alerts are per-channel, not per-overlay). Overlays
// render alerts as PLAIN TEXT lines: no animations, they would be distracting
// while playing. The OBS banner file feeds a Text source + Scroll filter.
public class AlertConfig : ObservableBase
{
    private bool _enabled = true;
    private bool _showFollows = true;
    private bool _showSubs = true;
    private bool _showRaids = true;
    private bool _bannerEnabled;
    private string _bannerFilePath = "";
    private int _bannerMaxLines = 10;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool ShowFollows { get => _showFollows; set => Set(ref _showFollows, value); }
    public bool ShowSubs { get => _showSubs; set => Set(ref _showSubs, value); }
    public bool ShowRaids { get => _showRaids; set => Set(ref _showRaids, value); }

    // Rolling text file for OBS (empty path = toolkit folder / alerts.txt).
    public bool BannerEnabled { get => _bannerEnabled; set => Set(ref _bannerEnabled, value); }
    public string BannerFilePath { get => _bannerFilePath; set => Set(ref _bannerFilePath, value); }
    public int BannerMaxLines { get => _bannerMaxLines; set => Set(ref _bannerMaxLines, Math.Clamp(value, 1, 50)); }
}

// The shared chat filter (one list for every overlay): chat lines containing
// any of these words or phrases are dropped before they render - slurs and
// spam never reach the stream capture. Plain config, not secrets.
public class ModerationConfig : ObservableBase
{
    private bool _enabled;
    private string _bannedWords = "";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // One word or phrase per line (edited in a multiline box).
    public string BannedWords { get => _bannedWords; set => Set(ref _bannedWords, value); }
}

// One active watchdog alarm, shown in the red banner. Id is stable per condition
// ("relay", "obs", "enc:<destId>", "push:<destId>") so alarms update in place
// and clear themselves when the condition goes away.
public sealed class WatchdogAlarm : ObservableBase
{
    private string _text;

    public WatchdogAlarm(string id, string text, Guid? destinationId)
    {
        Id = id;
        _text = text;
        DestinationId = destinationId;
    }

    public string Id { get; }
    public Guid? DestinationId { get; }

    public string Text { get => _text; set => Set(ref _text, value); }

    // Encoder alarms offer a one-click "kill it so nginx respawns it" action.
    public bool CanRestart { get; init; }
}

// One extra rung of a destination's quality ladder: the same composed picture,
// downscaled and re-encoded with its own bitrate, pushed to the SAME ingest URL
// under the stream key + suffix (e.g. key "abc123" + suffix "720p" ->
// "abc123-720p"), for targets that serve several qualities. The destination's
// main output keeps its own settings and is always on.
public class QualityVariantConfig : ObservableBase
{
    private string _suffix = "720p";
    private int _shortSide = 720;
    private int _videoBitrateKbps = 4500;
    private int _fpsOverride;

    // Becomes part of the stream name and of the watchdog's snapshot file name:
    // lowercase ASCII letters/digits only (BUG-77: IsLetterOrDigit alone kept
    // Unicode letters like ö/Ж/中 - legal filenames, but a bad idea inside a
    // stream key platforms must accept).
    public string Suffix
    {
        get => _suffix;
        set
        {
            string clean = new(value.Trim().ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(24).ToArray());
            Set(ref _suffix, clean);
        }
    }

    // Target SHORT-side size in pixels (720 -> 1280x720 landscape, 720x1280
    // portrait); the long side follows the main output's aspect ratio.
    public int ShortSide { get => _shortSide; set => Set(ref _shortSide, Math.Clamp(value, 144, 2160)); }
    public int VideoBitrateKbps { get => _videoBitrateKbps; set => Set(ref _videoBitrateKbps, Math.Clamp(value, 100, 20000)); }

    // 0 = same fps as the main output (e.g. 1080p60 main + 720p30 fallback).
    // Encoder preset and audio bitrate always follow the main output.
    public int FpsOverride { get => _fpsOverride; set => Set(ref _fpsOverride, Math.Clamp(value, 0, 240)); }
}

public class DestinationConfig : ObservableBase
{
    private Guid _id = Guid.NewGuid();
    private string _name = "";
    private Platform _platform = Platform.Custom;
    private bool _enabled;
    private Orientation _orientation = Orientation.Landscape;
    private PortraitStyle _portraitStyle = PortraitStyle.CenterCrop;
    private string _ingestUrl = "";
    private string _streamKey = "";
    private int _videoBitrateKbps = 6000;
    private int _audioBitrateKbps = 160;
    private int _keyframeSeconds = 2;
    private string _encoderPreset = "veryfast";
    private int _fpsOverride;
    private string _extraArgs = "";
    private int _delaySeconds;
    private bool _loudnessNormalize;
    private double _volumeTrimDb;
    private double _cropX;
    private double _cropY;
    private double _cropW;
    private double _fgX;
    private double _fgY;
    private double _fgScale;
    private string _customBackgroundPath = "";
    private string _channelHandle = "";

    // Platform account this destination belongs to (Twitch login, Kick slug,
    // YouTube URL/@handle, TikTok @handle). Powers the per-destination live
    // checks; empty falls back to the matching "My channels" entry.
    public string ChannelHandle { get => _channelHandle; set => Set(ref _channelHandle, value); }

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public Platform Platform { get => _platform; set => Set(ref _platform, value); }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(PushLightText)); } }
    public Orientation Orientation { get => _orientation; set => Set(ref _orientation, value); }
    public PortraitStyle PortraitStyle { get => _portraitStyle; set => Set(ref _portraitStyle, value); }
    public string IngestUrl { get => _ingestUrl; set => Set(ref _ingestUrl, value); }
    [JsonIgnore]
    public string StreamKey { get => _streamKey; set => Set(ref _streamKey, value); }
    public int VideoBitrateKbps { get => _videoBitrateKbps; set => Set(ref _videoBitrateKbps, value); }
    public int AudioBitrateKbps { get => _audioBitrateKbps; set => Set(ref _audioBitrateKbps, value); }
    public int KeyframeSeconds { get => _keyframeSeconds; set => Set(ref _keyframeSeconds, value); }
    public string EncoderPreset { get => _encoderPreset; set => Set(ref _encoderPreset, value); }
    public int FpsOverride { get => _fpsOverride; set => Set(ref _fpsOverride, Math.Clamp(value, 0, 240)); }
    public string ExtraArgs { get => _extraArgs; set => Set(ref _extraArgs, value); }
    public int DelaySeconds { get => _delaySeconds; set => Set(ref _delaySeconds, Math.Clamp(value, 0, 600)); }

    // Audio processing, per destination (the ladder variants inherit the same
    // audio). Normalization targets the -16 LUFS streaming standard (one-pass
    // loudnorm); trim is a plain gain in dB applied after it.
    public bool LoudnessNormalize { get => _loudnessNormalize; set => Set(ref _loudnessNormalize, value); }
    public double VolumeTrimDb { get => _volumeTrimDb; set => Set(ref _volumeTrimDb, Math.Round(Math.Clamp(value, -30, 15), 2)); }

    private DateTimeOffset? _keyRotatedAtUtc;

    // When the stream key was last rotated (the user tells the toolkit with the
    // 'Key rotated today' button). Keys leak via screenshots; the toolkit nags
    // once the age passes the configured interval. Null = never recorded.
    public DateTimeOffset? KeyRotatedAtUtc
    {
        get => _keyRotatedAtUtc;
        set { if (Set(ref _keyRotatedAtUtc, value)) Raise(nameof(KeyAgeText)); }
    }

    [JsonIgnore]
    public string KeyAgeText => KeyRotatedAtUtc is null
        ? "key never marked rotated"
        : $"key rotated {KeyAgeWords(DateTime.UtcNow - KeyRotatedAtUtc.Value.UtcDateTime)} ago";

    // Time passes without any property change: the 30s live-check tick calls
    // this so card labels and ages stay truthful.
    public void RefreshKeyAge() => Raise(nameof(KeyAgeText));

    private static string KeyAgeWords(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} days"
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} h"
        : $"{(int)age.TotalMinutes} min";

    // Custom portrait layout, all normalized (0-1) to survive upstream resolution changes.
    // Zeros mean "not touched yet" and fall back to the center-crop equivalent.
    public double CropX { get => _cropX; set => Set(ref _cropX, Math.Clamp(value, 0, 1)); }
    public double CropY { get => _cropY; set => Set(ref _cropY, Math.Clamp(value, 0, 1)); }
    public double CropW { get => _cropW; set => Set(ref _cropW, Math.Clamp(value, 0, 1)); }
    public double FgX { get => _fgX; set => Set(ref _fgX, Math.Clamp(value, 0, 1)); }
    public double FgY { get => _fgY; set => Set(ref _fgY, Math.Clamp(value, 0, 1)); }
    public double FgScale { get => _fgScale; set => Set(ref _fgScale, Math.Clamp(value, 0, 1)); }
    public string CustomBackgroundPath { get => _customBackgroundPath; set => Set(ref _customBackgroundPath, value); }

    // Custom composite layers (z-ordered). Empty = the single-crop custom layout
    // above still applies, so older configs behave exactly as before.
    private System.Collections.ObjectModel.ObservableCollection<OutputLayer> _layers = new();
    public System.Collections.ObjectModel.ObservableCollection<OutputLayer> Layers { get => _layers; set => Set(ref _layers, value); }

    // Quality ladder: extra downscaled variants pushed beside the main output.
    // Empty = single output, exactly like configs before the ladder existed.
    private System.Collections.ObjectModel.ObservableCollection<QualityVariantConfig> _variants = new();
    public System.Collections.ObjectModel.ObservableCollection<QualityVariantConfig> Variants { get => _variants; set => Set(ref _variants, value); }

    // The variants that actually ship: suffix sanitized non-empty, duplicate
    // suffixes ignored (first wins) - two rungs must never fight over one
    // stream name / snapshot file.
    public List<QualityVariantConfig> EffectiveVariants()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<QualityVariantConfig>();
        foreach (var v in Variants)
        {
            if (v.Suffix.Length == 0 || !seen.Add(v.Suffix)) continue;
            list.Add(v);
        }
        return list;
    }

    private VerifyLight _pushLight = VerifyLight.Unknown;

    [JsonIgnore]
    public VerifyLight PushLight
    {
        get => _pushLight;
        set { if (Set(ref _pushLight, value)) Raise(nameof(PushLightText)); }
    }

    [JsonIgnore]
    public string PushLightText => !Enabled
        ? "off"
        : PushLight switch
        {
            VerifyLight.Ok => "pushing",
            VerifyLight.Idle => "waiting",
            VerifyLight.Error => "unreachable",
            _ => "unknown",
        };

    // Platform-side live state ("actually live on the platform"), refreshed by
    // LiveCheckService - the relay-side PushLight cannot see past the push.
    private VerifyLight _liveLight = VerifyLight.Unknown;

    [JsonIgnore]
    public VerifyLight LiveLight
    {
        get => _liveLight;
        set { if (Set(ref _liveLight, value)) Raise(nameof(LiveLightText)); }
    }

    [JsonIgnore]
    public string LiveLightText => !Enabled
        ? "off"
        : LiveLight switch
        {
            VerifyLight.Ok => "live on platform",
            VerifyLight.Idle => "not live yet",
            VerifyLight.Error => "live check failed",
            _ => "platform: unknown",
        };

    // Live viewer count where the platform's public API hands one out
    // (Twitch/Kick/TikTok; YouTube has no free path). Null = unknown, never 0.
    private int? _viewers;

    [JsonIgnore]
    public int? Viewers
    {
        get => _viewers;
        set { if (Set(ref _viewers, value)) Raise(nameof(ViewersText)); }
    }

    [JsonIgnore]
    public string ViewersText => !Enabled || _viewers is null ? "" : $"{_viewers} viewers";
}

public class OverlayConfig : ObservableBase
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Chat overlay";
    private ChatMode _mode = ChatMode.All;
    private string _twitchChannel = "";
    private string _kickChannel = "";
    private string _kickChatroomId = "";
    private string _youTubeUrl = "";
    private string _tikTokHandle = "";
    private double _x;
    private double _y;
    private double _width = 420;
    private double _height = 640;
    private double _backgroundOpacity = 0.35;
    private double _fontSize = 15;
    private int _maxMessages = 30;
    private bool _locked;
    private bool _isVisible = true;
    private bool _showAlerts = true;
    private string _chatFontFamily = "Segoe UI";
    private string _chatTextColor = "#EEEEEE";
    private string _alertColor = "#FFD75E";

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public ChatMode Mode { get => _mode; set => Set(ref _mode, value); }
    public string TwitchChannel { get => _twitchChannel; set => Set(ref _twitchChannel, value); }
    public string KickChannel { get => _kickChannel; set => Set(ref _kickChannel, value); }
    public string KickChatroomId { get => _kickChatroomId; set => Set(ref _kickChatroomId, value); }
    public string YouTubeUrl { get => _youTubeUrl; set => Set(ref _youTubeUrl, value); }
    public string TikTokHandle { get => _tikTokHandle; set => Set(ref _tikTokHandle, value); }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 1)); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 8, 48)); }
    public int MaxMessages { get => _maxMessages; set => Set(ref _maxMessages, Math.Clamp(value, 5, 200)); }
    public bool Locked { get => _locked; set => Set(ref _locked, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    // Alert events (follows/subs/raids) appear as plain text lines in this
    // overlay. The OBS banner file is unaffected by this toggle.
    public bool ShowAlerts { get => _showAlerts; set => Set(ref _showAlerts, value); }

    // Overlay look: one font family for the whole overlay (chat + alerts),
    // and the two text colors. Hex strings ("#RRGGBB"); an invalid value
    // falls back to the default at render time, so half-typed input is safe.
    public string ChatFontFamily
    {
        get => _chatFontFamily;
        set => Set(ref _chatFontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim());
    }

    public string ChatTextColor
    {
        get => _chatTextColor;
        set => Set(ref _chatTextColor, value.Trim());
    }

    public string AlertColor
    {
        get => _alertColor;
        set => Set(ref _alertColor, value.Trim());
    }
}
