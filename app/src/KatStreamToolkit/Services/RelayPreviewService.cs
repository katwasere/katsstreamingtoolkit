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

    // Reconnects the shared session if needed; callers must hold Gate.
    private static SshClient GetClient(DeployTarget t)
    {
        if (_client == null || !_client.IsConnected || TargetChanged(t))
        {
            try { _client?.Disconnect(); _client?.Dispose(); } catch { }
            _client = null;
            _client = DeployService.ConnectSsh(t);
            _target = t;
        }
        return _client;
    }

    // One command through the shared persistent SSH session. Kept alive across
    // calls so the 10s status/monitor polling stops opening a fresh connection
    // every tick; throws on SSH failure so callers keep their error handling.
    public static string RunOnServer(DeployTarget t, string command, int timeoutSeconds = 20)
    {
        lock (Gate)
        {
            try
            {
                var client = GetClient(t);
                var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                cmd.Execute();
                return (cmd.Result + " " + cmd.Error).Trim();
            }
            catch
            {
                try { _client?.Disconnect(); _client?.Dispose(); } catch { }
                _client = null;
                throw;
            }
        }
    }

    public static PreviewSnapshot FetchSnapshot(DeployTarget t, DestinationConfig dest, AppConfig? cfg = null)
        => FetchSnapshot(t, dest, dest.Id, cfg);

    public static PreviewSnapshot FetchSnapshot(DeployTarget t, DestinationConfig dest, Guid destinationId, AppConfig? cfg = null)
    {
        lock (Gate)
        {
            try
            {
                var client = GetClient(t);
                return FetchSnapshotInner(client, t, dest, destinationId, cfg);
            }
            catch (Exception ex)
            {
                try { _client?.Disconnect(); _client?.Dispose(); } catch { }
                _client = null;
                return new PreviewSnapshot(null, "SSH error: " + ex.Message);
            }
        }
    }

    private static PreviewSnapshot FetchSnapshotInner(SshClient client, DeployTarget t, DestinationConfig dest, Guid destinationId, AppConfig? cfg)
    {
        // Exceptions propagate to the caller, which resets the shared session
        // and reports the SSH error.

        // Is the relay container up? (docker group users, NOPASSWD sudo, or root)
        string containers = Run(client,
            "sh -c 'docker ps --format \"{{.Names}}\" 2>/dev/null || sudo -n docker ps --format \"{{.Names}}\" 2>/dev/null' || sudo -n docker ps --format \"{{.Names}}\" 2>/dev/null");
        if (string.IsNullOrWhiteSpace(containers))
            return new PreviewSnapshot(null, "cannot query docker on the server (the SSH user needs docker rights)");
        if (!containers.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("kat-relay"))
            return new PreviewSnapshot(null, "relay container not running - deploy the bundle first");

        string file = RelayConfigGenerator.SnapshotPath(destinationId);
        string b64 = Run(client,
            $"sh -c 'docker exec kat-relay sh -c \"base64 {file} 2>/dev/null\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"base64 {file} 2>/dev/null\" 2>/dev/null' || sudo -n docker exec kat-relay sh -c 'base64 {file} 2>/dev/null' 2>/dev/null");

        // While not live, the test-card run (if one was started) fills in.
        bool isTest = false;
        if (string.IsNullOrWhiteSpace(b64) || b64.Length < 100)
        {
            string testFile = RelayConfigGenerator.SnapshotTestPath(destinationId);
            b64 = Run(client,
                $"sh -c 'docker exec kat-relay sh -c \"base64 {testFile} 2>/dev/null\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"base64 {testFile} 2>/dev/null\" 2>/dev/null' || sudo -n docker exec kat-relay sh -c 'base64 {testFile} 2>/dev/null' 2>/dev/null");
            isTest = !string.IsNullOrWhiteSpace(b64) && b64.Length >= 100;
        }

        if (string.IsNullOrWhiteSpace(b64) || b64.Length < 100)
        {
            // No frame. Before blaming OBS, find the real cause: is the relay
            // even up, is ANY stream being published, does the deployed config
            // write preview snapshots at all, and what did ffmpeg log?
            string stat = Run(client,
                "sh -c 'curl -s --max-time 3 http://127.0.0.1:8080/stat 2>/dev/null || wget -qO- -T 3 http://127.0.0.1:8080/stat 2>/dev/null'");
            if (string.IsNullOrWhiteSpace(stat) || !stat.Contains("<rtmp"))
                return new PreviewSnapshot(null,
                    "relay not responding - deploy the bundle first (the Deploy tab light reports the same)");

            if (!stat.Contains("<publishing"))
                return new PreviewSnapshot(null,
                    $"nginx sees no incoming stream - in OBS set Server: rtmp://{t.Host}/live (rtmp://{t.Host}:1935/live is the same thing - 1935 is the default RTMP port), Key: your stream name, then Start Streaming. If OBS cannot connect at all, the server firewall/security list may not allow TCP 1935");

            // The stream IS live.

            // A disabled destination has no encoder at all - say so instead of
            // hinting at OBS.
            if (!dest.Enabled)
                return new PreviewSnapshot(null,
                    $"stream is live, but '{dest.Name}' is disabled - enable it and click 'Deploy to server' so its encoder starts");

            // No ingest URL: the deploy deliberately leaves this destination
            // out of nginx.conf (an empty/broken URL would kill the relay).
            if (string.IsNullOrWhiteSpace(dest.IngestUrl))
                return new PreviewSnapshot(null,
                    $"stream is live, but '{dest.Name}' has no ingest URL - paste the RTMP URL from the platform's live-key page in the toolkit, then redeploy");

            // Does the container run the config THIS app generates right now?
            // Compared against the running /etc/nginx/nginx.conf - no guessing
            // whether a redeploy actually happened.
            if (cfg != null)
            {
                string hashOut = Run(client,
                    "sh -c 'docker exec kat-relay sha256sum /etc/nginx/nginx.conf 2>/dev/null || sudo -n docker exec kat-relay sha256sum /etc/nginx/nginx.conf 2>/dev/null'");
                string? runningHash = hashOut.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (runningHash != null)
                {
                    string expected = ServerExporter.ComputeNginxConfigHash(cfg);
                    if (!runningHash.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        string deployedVersion = Run(client, $"head -n 1 '{t.RemotePath}/BUNDLE-VERSION' 2>/dev/null").Trim();
                        return new PreviewSnapshot(null,
                            "your stream IS live - but the relay container runs an older config than this app builds"
                            + (deployedVersion.Length > 0 ? $" (deployed bundle {deployedVersion[..Math.Min(12, deployedVersion.Length)]})" : " (deployed before version stamps)")
                            + $", this app expects config {expected[..12]}. Click 'Deploy to server', then restart the stream so the encoder picks it up");
                    }
                }
            }
            else
            {
                // No config to compare against - fall back to detecting a
                // pre-snapshot deployment.
                string snapLines = Run(client,
                    "sh -c 'docker exec kat-relay sh -c \"grep -c kat-preview /etc/nginx/nginx.conf\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"grep -c kat-preview /etc/nginx/nginx.conf\" 2>/dev/null'");
                if (string.IsNullOrWhiteSpace(snapLines) || snapLines.Trim() == "0")
                    return new PreviewSnapshot(null,
                        "your stream IS live - but the relay on the server was deployed before preview snapshots existed. Click 'Deploy to server' once, then restart the stream (or re-click the test card) and this preview will fill in");
            }

            // If this destination's ingest host does not even resolve on the
            // server, say so - a dead default hostname is easy to miss.
            string? ingestHost = null;
            if (Uri.TryCreate(dest.IngestUrl.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                ingestHost = uri.Host;
            else if (dest.IngestUrl.Trim().StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
                ingestHost = dest.IngestUrl.Trim()[7..].Split('/')[0];

            if (!string.IsNullOrWhiteSpace(ingestHost))
            {
                string probe = Run(client,
                    $"sh -c 'docker exec kat-relay sh -c \"getent ahostsv4 {ingestHost} 2>/dev/null | head -1\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"getent ahostsv4 {ingestHost} 2>/dev/null | head -1\" 2>/dev/null'");
                if (string.IsNullOrWhiteSpace(probe))
                    return new PreviewSnapshot(null,
                        $"stream is live, but this destination's ingest host '{ingestHost}' does not resolve on the server - fix the ingest URL (some defaults, like TikTok's push.tiktokcdn.com, no longer exist in DNS)");
            }

            // ffmpeg's stderr lands in the nginx error log - surface the most
            // recent failure instead of a generic hint. (The child's lines do
            // not always mention "ffmpeg", so match the failure texts.)
            string logTail = Run(client,
                "sh -c 'docker exec kat-relay sh -c \"tail -n 80 /var/log/nginx/error.log\" 2>/dev/null || sudo -n docker exec kat-relay sh -c \"tail -n 80 /var/log/nginx/error.log\" 2>/dev/null'");
            string? lastError = logTail
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l =>
                    l.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("no such file", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("connection timed out", StringComparison.OrdinalIgnoreCase));
            if (lastError != null)
            {
                if (lastError.Length > 220) lastError = lastError[..220] + "...";
                return new PreviewSnapshot(null,
                    "stream is live, but the relay log shows a recent failure: " + lastError);
            }

            return new PreviewSnapshot(null,
                "stream is live - waiting for this output's first preview frame (takes a few seconds). If nothing shows up, toggle the destination off/on and redeploy so its encoder restarts while the stream is live");
        }

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

    private static string Run(SshClient client, string command)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(10);
        cmd.Execute();
        return cmd.ExitStatus == 0 ? cmd.Result ?? "" : "";
    }

    // Runs every enabled ffmpeg-routed output's exact graph on a synthetic test
    // source for a few seconds, writing only to the test snapshot files - nothing
    // is pushed to any platform. Returns how many encoders actually produced a
    // frame (a detached `docker exec -d` always exits 0, so trusting its status
    // proved nothing ran).
    public static int StartTestEncoders(DeployTarget t, AppConfig cfg)
    {
        lock (Gate)
        {
            SshClient client;
            try
            {
                client = GetClient(t);
            }
            catch
            {
                return 0;
            }

            // Kill leaked test encoders ONCE, up front: per-destination kills
            // murdered the encoders just started for the previous outputs, so
            // only the last destination ever showed the test card.
            Exec(client,
                "sh -c 'docker exec kat-relay pkill -f kat-preview-test 2>/dev/null' || sudo -n docker exec kat-relay pkill -f kat-preview-test 2>/dev/null || sudo -n docker exec kat-relay pkill -f kat-preview-test 2>/dev/null");

            var launched = new List<Guid>();
            foreach (var dest in cfg.Destinations.Where(d =>
                         d.Enabled && (d.Orientation == Models.Orientation.Portrait ||
                                       d.PortraitStyle == Models.PortraitStyle.Custom)))
            {
                // Run under timeout: the script execs straight into ffmpeg so TERM
                // lands on ffmpeg itself, and the test file is removed afterwards.
                string script = $"exec {RelayConfigGenerator.BuildTestEncoderCommand(cfg, dest)} 2>>/tmp/kat-test.log";
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                string testPath = RelayConfigGenerator.SnapshotTestPath(dest.Id);
                string remote =
                    $"sh -c 'docker exec -d kat-relay sh -c \"rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}\" 2>/dev/null' || sudo -n docker exec -d kat-relay sh -c 'rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}' || sudo -n docker exec -d kat-relay sh -c 'rm -f {testPath}; echo {b64} | base64 -d > /tmp/kat-test.sh && timeout 8 sh /tmp/kat-test.sh; rm -f {testPath}'";
                if (Exec(client, remote) == 0)
                    launched.Add(dest.Id);
            }

            // Verify each encoder actually started by polling for its snapshot
            // file while the runs are still alive (ffmpeg needs ~1s to encode
            // the first frame).
            int verified = 0;
            foreach (var id in launched)
            {
                string testPath = RelayConfigGenerator.SnapshotTestPath(id);
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Thread.Sleep(400);
                    string probe = ExecWithStatus(client,
                        $"sh -c 'docker exec kat-relay sh -c \"[ -s {testPath} ]\" 2>/dev/null' || sudo -n docker exec kat-relay sh -c '[ -s {testPath} ]' 2>/dev/null");
                    if (probe == "0")
                    {
                        verified++;
                        break;
                    }
                }
            }
            return verified;
        }
    }

    private static int Exec(SshClient client, string command)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(10);
        cmd.Execute();
        return cmd.ExitStatus ?? -1;
    }

    private static string ExecWithStatus(SshClient client, string command)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(10);
        cmd.Execute();
        return (cmd.ExitStatus ?? -1).ToString();
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
