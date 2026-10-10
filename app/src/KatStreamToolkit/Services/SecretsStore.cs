using System.IO;
using System.Text.Json;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// Stream keys and the server IP live in their own file, never inside config.json,
// so sharing or exporting settings can never leak them.
public class SecretsData
{
    public Dictionary<string, string>? DestinationKeys { get; set; }
    public string UpstreamStreamName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public string SshPassword { get; set; } = "";

    // Euler Stream API key for the TikTok chat connector (free tier).
    public string EulerApiKey { get; set; } = "";

    // obs-websocket password for go-live orchestration.
    public string ObsPassword { get; set; } = "";
}

public static class SecretsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private const string KeyMask = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public static string DefaultPath => Path.Combine(ConfigStore.DirectoryPath, "secrets.json");

    public static string MaskKey => KeyMask;

    public static SecretsData Load(string path)
    {
        // Same recovery as the config: a keys file that fails to parse falls
        // back to AtomicWrite's .bak before giving up, instead of silently
        // showing no keys at all.
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;
                var data = JsonSerializer.Deserialize<SecretsData>(File.ReadAllText(candidate), Options);
                if (data != null)
                {
                    data.DestinationKeys ??= new Dictionary<string, string>();
                    return data;
                }
            }
            catch
            {
                // Try the next copy.
            }
        }
        return new SecretsData();
    }

    public static void Save(string path, SecretsData data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Atomic write: this file holds every stream key - a torn write must not
        // leave it unreadable.
        ConfigStore.AtomicWrite(path, JsonSerializer.Serialize(data, Options));
    }

    public static SecretsData Capture(AppConfig cfg)
    {
        // Built with an indexer instead of ToDictionary: a duplicated destination
        // Id (possible via hand-edited config) threw ArgumentException inside
        // Save, which is swallowed, and secrets silently stopped persisting.
        var keys = new Dictionary<string, string>();
        foreach (var dest in cfg.Destinations)
            keys[dest.Id.ToString()] = dest.StreamKey;
        return new SecretsData
        {
            DestinationKeys = keys,
            UpstreamStreamName = cfg.Upstream.StreamName,
            ServerHost = cfg.ServerHost,
        };
    }

    public static void SaveFromConfig(string path, AppConfig cfg, SecretsData? previous = null)
    {
        var data = Capture(cfg);
        if (previous != null)
        {
            data.SshPassword = previous.SshPassword;
            data.EulerApiKey = previous.EulerApiKey;
            data.ObsPassword = previous.ObsPassword;
        }
        Save(path, data);
    }

    public static void Apply(AppConfig cfg, SecretsData data)
    {
        foreach (var dest in cfg.Destinations)
        {
            if (data.DestinationKeys != null &&
                data.DestinationKeys.TryGetValue(dest.Id.ToString(), out var key))
                dest.StreamKey = key;
        }
        if (!string.IsNullOrEmpty(data.UpstreamStreamName))
            cfg.Upstream.StreamName = data.UpstreamStreamName;
        if (!string.IsNullOrEmpty(data.ServerHost))
            cfg.ServerHost = data.ServerHost;
    }
}
