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
    private bool _watchdogAutoRestart = true;
    private bool _alarmSound = true;
    private bool _obsEnabled;
    private string _obsWebSocketUrl = "ws://127.0.0.1:4455";

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

    // Watchdog: kill a frozen encoder so nginx respawns it, and beep when a new
    // alarm appears. Both persist with the rest of the config.
    public bool WatchdogAutoRestart { get => _watchdogAutoRestart; set => Set(ref _watchdogAutoRestart, value); }
    public bool AlarmSound { get => _alarmSound; set => Set(ref _alarmSound, value); }

    // OBS control via obs-websocket (same PC by default). The password lives in
    // secrets.json, never here.
    public bool ObsEnabled { get => _obsEnabled; set => Set(ref _obsEnabled, value); }
    public string ObsWebSocketUrl { get => _obsWebSocketUrl; set => Set(ref _obsWebSocketUrl, value); }
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
}
