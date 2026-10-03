using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DirectoryPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KatStreamToolkit");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Destinations ??= new List<DestinationConfig>();
                    cfg.Overlays ??= new List<OverlayConfig>();
                    cfg.Upstream ??= new UpstreamConfig();
                    cfg.MyChannels ??= new MyChannelsConfig();
                    return cfg;
                }
            }
        }
        catch
        {
        }
        return CreateDefaults();
    }

    public static void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(FilePath, json);
    }

    private static AppConfig CreateDefaults()
    {
        return new AppConfig
        {
            Destinations = new List<DestinationConfig>
            {
                new()
                {
                    Name = "Twitch (landscape)",
                    Platform = Platform.Twitch,
                    Enabled = true,
                    Orientation = Orientation.Landscape,
                    IngestUrl = "rtmp://live.twitch.tv/app",
                },
                new()
                {
                    Name = "YouTube Live (landscape)",
                    Platform = Platform.YouTube,
                    Enabled = true,
                    Orientation = Orientation.Landscape,
                    IngestUrl = "rtmp://a.rtmp.youtube.com/live2",
                },
                new()
                {
                    Name = "Kick (landscape)",
                    Platform = Platform.Kick,
                    Enabled = true,
                    Orientation = Orientation.Landscape,
                    IngestUrl = "rtmp://fa723fc1b171.global-contribute.live-video.net/app",
                },
                new()
                {
                    Name = "TikTok (portrait)",
                    Platform = Platform.TikTok,
                    Enabled = false,
                    Orientation = Orientation.Portrait,
                    PortraitStyle = PortraitStyle.BlurredBackground,
                    IngestUrl = "rtmp://push.tiktokcdn.com/live",
                    VideoBitrateKbps = 4500,
                },
                new()
                {
                    Name = "YouTube Live (portrait / Shorts)",
                    Platform = Platform.YouTube,
                    Enabled = false,
                    Orientation = Orientation.Portrait,
                    PortraitStyle = PortraitStyle.BlurredBackground,
                    IngestUrl = "rtmp://a.rtmp.youtube.com/live2",
                    VideoBitrateKbps = 6000,
                },
            },
            Overlays = new List<OverlayConfig>
            {
                new()
                {
                    Name = "Merged chat",
                    Mode = ChatMode.All,
                },
            },
        };
    }
}
