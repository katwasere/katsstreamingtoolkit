using System.IO;
using System.Reflection;
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

    // The live config file's path (backup/restore need the same file Load/Save use).
    public static string ConfigFilePath => FilePath;

    public static AppConfig Load()
    {
        // A config that fails to parse (torn write, disk hiccup) falls back to
        // the .bak that AtomicWrite keeps - the backup existed exactly for this
        // and was never read. Defaults only when BOTH copies are unreadable.
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg == null)
                    continue;
                cfg.Destinations ??= new List<DestinationConfig>();
                cfg.Overlays ??= new List<OverlayConfig>();
                cfg.Commands ??= new List<CommandConfig>();
                cfg.Upstream ??= new UpstreamConfig();
                cfg.MyChannels ??= new MyChannelsConfig();
                NormalizeNullStrings(cfg);
                ClearKnownDeadDefaultIngest(cfg);
                return cfg;
            }
            catch
            {
                // Try the next copy.
            }
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
        // BUG-60: a fixed ".tmp" name raced concurrent saves (LoginTwitch's
        // off-UI-thread Save vs the debounced autosave) - one IOException and
        // that save was lost silently, a rotated refresh token among the
        // casualties. A unique temp file per write makes concurrent writes
        // independent; File.Replace still lands each atomically and keeps .bak.
        string tmp = path + ".tmp" + Guid.NewGuid().ToString("N");
        string bak = path + ".bak";
        try
        {
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
                File.Replace(tmp, path, bak);
            else
                File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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

    // BUG-63: one JSON null over a non-nullable string property (hand-edited
    // config.json is an anticipated scenario) assigned null into e.g.
    // IngestUrl - the .Trim() in ClearKnownDeadDefaultIngest then threw an NRE
    // that the per-file catch swallowed, BOTH copies "failed", and the whole
    // config reset to defaults. Walk the loaded graph once and coalesce every
    // null string back to "" before any code touches the config.
    private static void NormalizeNullStrings(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, 0);

        void Walk(object? node, int depth)
        {
            if (node is null || depth > 8 || !visited.Add(node)) return;
            if (node is IEnumerable<object> list)
            {
                foreach (var item in list) Walk(item, depth + 1);
                return;
            }
            var type = node.GetType();
            if (!type.IsClass) return;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // Computed getters can themselves NRE on a half-null node - this
                // pass must never throw, or Load falls back to defaults again.
                try
                {
                    if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;
                    if (prop.PropertyType == typeof(string))
                    {
                        if (prop.CanWrite && prop.GetValue(node) is null)
                            prop.SetValue(node, "");
                    }
                    else if (prop.PropertyType.IsClass)
                    {
                        Walk(prop.GetValue(node), depth + 1);
                    }
                }
                catch { }
            }
        }
    }

    // The shipped TikTok default host (push.tiktokcdn.com) no longer exists in
    // DNS - TikTok rotates ingest endpoints, and the real RTMP URL has to come
    // from the creator's live-key page. nginx resolves push hosts while parsing
    // the config, so this stale default blocked EVERY deploy. Clear it; the
    // generated config then skips that destination (with a comment) until a
    // real URL is pasted.
    private static void ClearKnownDeadDefaultIngest(AppConfig cfg)
    {
        foreach (var dest in cfg.Destinations)
        {
            if (Uri.TryCreate(dest.IngestUrl.Trim(), UriKind.Absolute, out var uri) &&
                uri.Host.Equals("push.tiktokcdn.com", StringComparison.OrdinalIgnoreCase))
                dest.IngestUrl = "";
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
                    // TikTok rotates ingest hosts - paste the RTMP URL from your
                    // TikTok live-key page here; there is no stable default.
                    IngestUrl = "",
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
