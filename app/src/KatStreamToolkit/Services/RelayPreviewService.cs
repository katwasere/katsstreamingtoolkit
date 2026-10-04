using System.Text;
using KatStreamToolkit.Models;
using Renci.SshNet;

namespace KatStreamToolkit.Services;

public sealed record PreviewSnapshot(byte[]? Data, string Status)
{
    public bool HasFrame => Data is { Length: > 100 };
    public bool IsTestFrame { get; init; }
}

// Fetches the relay's per-destination preview snapshots (small JPEGs the ffmpeg
// processes write inside the container at ~2 fps). Keeps one SSH session alive
// while the Output Studio is watching; reconnects on failure or target change,
// and always reports WHY there is no frame so the UI can say what to do.
public static class RelayPreviewService
{
    private static SshClient? _client;
    private static DeployTarget? _target;
    private static readonly object Gate = new();

    public static PreviewSnapshot FetchSnapshot(DeployTarget t, DestinationConfig dest)
        => FetchSnapshot(t, dest, dest.Id);

    public static PreviewSnapshot FetchSnapshot(DeployTarget t, DestinationConfig dest, Guid destinationId)
    {
        lock (Gate)
        {
            try
            {
                if (_client == null || !_client.IsConnected || TargetChanged(t))
                {
                    _client?.Disconnect();
                    _client?.Dispose();
                    _client = null;
                    _client = DeployService.ConnectSsh(t);
                    _target = t;
                }
            }
            catch (Exception ex)
            {
                _client = null;
                return new PreviewSnapshot(null, "could not SSH to the server: " + ex.Message);
            }

            try
            {
                // Is the relay container up? (docker group users, NOPASSWD sudo, or root)
                string containers = Run(_client,
                    "sh -c 'docker ps --format \"{{.Names}}\" 2>/dev/null || sudo -n docker ps --format \"{{.Names}}\" 2>/dev/null' || sudo docker ps --format \"{{.Names}}\" 2>/dev/null");
                if (string.IsNullOrWhiteSpace(containers))
                    return new PreviewSnapshot(null, "cannot query docker on the server (the SSH user needs docker rights)");
                if (!containers.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Contains("kat-relay"))
                    return new PreviewSnapshot(null, "relay container not running - deploy the bundle first");

                string file = RelayConfigGenerator.SnapshotPath(destinationId);
                string b64 = Run(_client,
                    $"sh -c 'docker exec kat-relay sh -c \"base64 {file} 2>/dev/null\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"base64 {file} 2>/dev/null\" 2>/dev/null' || sudo docker exec kat-relay sh -c 'base64 {file} 2>/dev/null' 2>/dev/null");

                // While not live, the test-card run (if one was started) fills in.
                bool isTest = false;
                if (string.IsNullOrWhiteSpace(b64) || b64.Length < 100)
                {
                    string testFile = RelayConfigGenerator.SnapshotTestPath(destinationId);
                    b64 = Run(_client,
                        $"sh -c 'docker exec kat-relay sh -c \"base64 {testFile} 2>/dev/null\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"base64 {testFile} 2>/dev/null\" 2>/dev/null' || sudo docker exec kat-relay sh -c 'base64 {testFile} 2>/dev/null' 2>/dev/null");
                    isTest = !string.IsNullOrWhiteSpace(b64) && b64.Length >= 100;
                }

                if (string.IsNullOrWhiteSpace(b64) || b64.Length < 100)
                {
                    // If this destination's ingest host does not even resolve on the
                    // server, say so - a dead default hostname is easy to miss.
                    string? ingestHost = null;
                    if (Uri.TryCreate(dest.IngestUrl.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                        ingestHost = uri.Host;
                    else if (dest.IngestUrl.Trim().StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
                        ingestHost = dest.IngestUrl.Trim()[7..].Split('/')[0];

                    if (!string.IsNullOrWhiteSpace(ingestHost))
                    {
                        string probe = Run(_client,
                            $"sh -c 'docker exec kat-relay sh -c \"getent ahostsv4 {ingestHost} 2>/dev/null | head -1\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"getent ahostsv4 {ingestHost} 2>/dev/null | head -1\" 2>/dev/null'");
                        if (string.IsNullOrWhiteSpace(probe))
                            return new PreviewSnapshot(null,
                                $"no frames: this destination's ingest host '{ingestHost}' does not resolve on the server - fix the ingest URL (some defaults, like TikTok's push.tiktokcdn.com, no longer exist in DNS)");
                    }                }

                if (string.IsNullOrWhiteSpace(b64) || b64.Length < 100)
                    return new PreviewSnapshot(null,
                        $"nginx sees no incoming stream - in OBS set Server: rtmp://{t.Host}/live, Key: your stream name, then Start Streaming (the Deploy tab light turns green when it connects)");

                try
                {
                    return new PreviewSnapshot(Convert.FromBase64String(b64.Trim()),
                        isTest ? "test card (auto-stops after a few seconds)" : "ok")
                    {
                        IsTestFrame = isTest,
                    };
                }
                catch (FormatException)
                {
                    return new PreviewSnapshot(null, "snapshot was torn mid-write - retrying");
                }
            }
            catch (Exception ex)
            {
                try { _client?.Disconnect(); _client?.Dispose(); } catch { }
                _client = null;
                return new PreviewSnapshot(null, "SSH error: " + ex.Message);
            }
        }
    }

    private static string Run(SshClient client, string command)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(10);
        cmd.Execute();
        return cmd.ExitStatus == 0 ? cmd.Result ?? "" : "";
    }

    // Runs every enabled ffmpeg-routed output's exact graph on a synthetic test
    // source for a few seconds, writing only to the test snapshot files - nothing
    // is pushed to any platform. Returns how many encoders were started.
    public static int StartTestEncoders(DeployTarget t, AppConfig cfg)
    {
        lock (Gate)
        {
            try
            {
                if (_client == null || !_client.IsConnected || TargetChanged(t))
                {
                    _client?.Disconnect();
                    _client?.Dispose();
                    _client = null;
                    _client = DeployService.ConnectSsh(t);
                    _target = t;
                }
            }
            catch
            {
                _client = null;
                return 0;
            }

            int started = 0;
            foreach (var dest in cfg.Destinations.Where(d =>
                         d.Enabled && (d.Orientation == Models.Orientation.Portrait ||
                                       d.PortraitStyle == Models.PortraitStyle.Custom)))
            {
                // Kill any leaked test encoder first, then run this one under
                // timeout: the script execs straight into ffmpeg so TERM lands
                // on ffmpeg itself, and the test file is removed afterwards.
                Run(_client, $"sh -c 'docker exec kat-relay pkill -f kat-preview-test 2>/dev/null' || sudo -n docker exec kat-relay pkill -f kat-preview-test 2>/dev/null || sudo docker exec kat-relay pkill -f kat-preview-test 2>/dev/null");

                string script = $"exec {RelayConfigGenerator.BuildTestEncoderCommand(cfg, dest)} 2>>/tmp/kat-test.log";
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                string testPath = RelayConfigGenerator.SnapshotTestPath(dest.Id);
                string remote =
                    $"sh -c 'docker exec -d kat-relay sh -c \"rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}\" 2>/dev/null' || sudo -n docker exec -d kat-relay sh -c 'rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}' || sudo docker exec -d kat-relay sh -c 'rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}'";
                var output = _client.CreateCommand(remote);
                output.CommandTimeout = TimeSpan.FromSeconds(10);
                output.Execute();
                if (output.ExitStatus == 0) started++;
            }
            return started;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _client?.Disconnect(); _client?.Dispose(); } catch { }
            _client = null;
            _target = null;
        }
    }

    private static bool TargetChanged(DeployTarget t)
        => _target is null
           || _target.Host != t.Host
           || _target.User != t.User
           || _target.RemotePath != t.RemotePath
           || _target.UseKey != t.UseKey;
}
