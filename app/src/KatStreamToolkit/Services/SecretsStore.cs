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
}

public static class SecretsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private const string KeyMask = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public static string DefaultPath => Path.Combine(ConfigStore.DirectoryPath, "secrets.json");

    public static string MaskKey => KeyMask;

    public static SecretsData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SecretsData>(File.ReadAllText(path), Options);
                if (data != null)
                {
                    data.DestinationKeys ??= new Dictionary<string, string>();
                    return data;
                }
            }
        }
        catch
        {
        }
        return new SecretsData();
    }

    public static void Save(string path, SecretsData data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, Options));
    }

    public static SecretsData Capture(AppConfig cfg) => new()
    {
        DestinationKeys = cfg.Destinations.ToDictionary(d => d.Id.ToString(), d => d.StreamKey),
        UpstreamStreamName = cfg.Upstream.StreamName,
        ServerHost = cfg.ServerHost,
    };

    public static void SaveFromConfig(string path, AppConfig cfg) => Save(path, Capture(cfg));

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
