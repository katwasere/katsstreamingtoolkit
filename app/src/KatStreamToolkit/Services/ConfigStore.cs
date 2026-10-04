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
        // Write temp + replace: the old truncate-then-write left a truncated
        // config.json behind on a crash mid-write, which Load() then silently
        // swapped for defaults. File.Replace is atomic on NTFS and keeps a .bak.
        var json = JsonSerializer.Serialize(config, Options);
        AtomicWrite(FilePath, json);
    }

    internal static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        string bak = path + ".bak";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
            File.Replace(tmp, path, bak);
        else
            File.Move(tmp, path);
    }

    // One-time migration: older builds stored keys inside config.json.
    // Pull them out so they can be moved into secrets.json and stripped.
    public static SecretsData? TryReadLegacySecrets()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            // DestinationKeys has no initializer (stays null) - the old inline
            // `data.DestinationKeys![id] = key!` threw NRE inside the try, was
            // swallowed, and legacy keys were silently never migrated.
            var data = new SecretsData { DestinationKeys = new Dictionary<string, string>() };

            if (root.TryGetProperty("ServerHost", out var host) && host.ValueKind == JsonValueKind.String)
                data.ServerHost = host.GetString() ?? "";

            if (root.TryGetProperty("Upstream", out var upstream) &&
                upstream.TryGetProperty("StreamName", out var streamName) &&
                streamName.ValueKind == JsonValueKind.String)
                data.UpstreamStreamName = streamName.GetString() ?? "";

            if (root.TryGetProperty("Destinations", out var destinations) &&
                destinations.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in destinations.EnumerateArray())
                {
                    string? id = d.TryGetProperty("Id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() : null;
                    string? key = d.TryGetProperty("StreamKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String
                        ? keyEl.GetString() : null;
                    if (id != null && !string.IsNullOrEmpty(key))
                        data.DestinationKeys![id] = key!;
                }
            }

            if (data.DestinationKeys!.Count == 0 && data.ServerHost.Length == 0 && data.UpstreamStreamName.Length == 0)
                return null;
            return data;
        }
        catch
        {
            return null;
        }
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
