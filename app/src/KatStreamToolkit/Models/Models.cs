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
}

public enum ChatMode
{
    All,
    Twitch,
    Kick,
    YouTube,
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

    public string TwitchChannel { get => _twitchChannel; set => Set(ref _twitchChannel, value); }
    public string KickChannel { get => _kickChannel; set => Set(ref _kickChannel, value); }
    public string YouTubeUrl { get => _youTubeUrl; set => Set(ref _youTubeUrl, value); }
}

public class AppConfig : ObservableBase
{
    private UpstreamConfig _upstream = new();
    private MyChannelsConfig _myChannels = new();
    private string _serverHost = "";
    private List<DestinationConfig> _destinations = new();
    private List<OverlayConfig> _overlays = new();

    public UpstreamConfig Upstream { get => _upstream; set => Set(ref _upstream, value); }
    public MyChannelsConfig MyChannels { get => _myChannels; set => Set(ref _myChannels, value); }

    [JsonIgnore]
    public string ServerHost { get => _serverHost; set => Set(ref _serverHost, value); }

    public string KeysFilePath { get; set; } = "";
    public List<DestinationConfig> Destinations { get => _destinations; set => Set(ref _destinations, value); }
    public List<OverlayConfig> Overlays { get => _overlays; set => Set(ref _overlays, value); }
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

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public Platform Platform { get => _platform; set => Set(ref _platform, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Orientation Orientation { get => _orientation; set => Set(ref _orientation, value); }
    public PortraitStyle PortraitStyle { get => _portraitStyle; set => Set(ref _portraitStyle, value); }
    public string IngestUrl { get => _ingestUrl; set => Set(ref _ingestUrl, value); }
    [JsonIgnore]
    public string StreamKey { get => _streamKey; set => Set(ref _streamKey, value); }
    public int VideoBitrateKbps { get => _videoBitrateKbps; set => Set(ref _videoBitrateKbps, value); }
    public int AudioBitrateKbps { get => _audioBitrateKbps; set => Set(ref _audioBitrateKbps, value); }
    public int KeyframeSeconds { get => _keyframeSeconds; set => Set(ref _keyframeSeconds, value); }
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
