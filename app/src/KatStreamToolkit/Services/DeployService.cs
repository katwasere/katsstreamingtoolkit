using System.IO;
using KatStreamToolkit.Models;
using Renci.SshNet;

namespace KatStreamToolkit.Services;

public sealed record DeployTarget(string Host, string User, bool UseKey, string Password, string KeyPath, string RemotePath);

// What the server actually runs vs what this app would deploy right now.
public sealed record ServerBundleInfo(
    bool ContainerRunning,
    string? DeployedVersion,     // first line of BUNDLE-VERSION on the server (null = pre-versioning deploy)
    string? RunningConfigHash,   // sha256 of the container's live /etc/nginx/nginx.conf
    string ExpectedConfigHash)   // sha256 of the config this app generates now
{
    public bool RunningConfigMatches =>
        RunningConfigHash != null &&
        RunningConfigHash.StartsWith(ExpectedConfigHash, StringComparison.OrdinalIgnoreCase);
}

// SSH/SFTP operations for the Deploy tab. Everything runs on a background thread;
// callers get progress through a log callback.
public static class DeployService
{
    // Internal connect reused by the preview service; DeployService stays the
    // single place that knows how to open SSH.
    public static SshClient ConnectSsh(DeployTarget t)
    {
        AuthenticationMethod auth = t.UseKey
            ? new PrivateKeyAuthenticationMethod(t.User, LoadPrivateKey(t))
            : new PasswordAuthenticationMethod(t.User, t.Password);
        var info = new ConnectionInfo(t.Host, t.User, auth) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new SshClient(info);
        client.Connect();
        if (!client.IsConnected)
            throw new Exception("could not connect (check host, user and credentials)");
        return client;
    }

    private static SftpClient ConnectSftp(DeployTarget t)
    {
        AuthenticationMethod auth = t.UseKey
            ? new PrivateKeyAuthenticationMethod(t.User, LoadPrivateKey(t))
            : new PasswordAuthenticationMethod(t.User, t.Password);
        var info = new ConnectionInfo(t.Host, t.User, auth) { Timeout = TimeSpan.FromSeconds(15) };
        var sftp = new SftpClient(info);
        sftp.Connect();
        if (!sftp.IsConnected)
            throw new Exception("could not open SFTP session");
        return sftp;
    }

    // Passphrase-protected keys: SSH.NET accepts the deploy password as the key
    // passphrase. The old `new PrivateKeyFile(t.KeyPath)` threw an opaque
    // "private key is encrypted" error for encrypted keys and never tried.
    private static PrivateKeyFile LoadPrivateKey(DeployTarget t)
    {
        try
        {
            return string.IsNullOrEmpty(t.Password)
                ? new PrivateKeyFile(t.KeyPath)
                : new PrivateKeyFile(t.KeyPath, t.Password);
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(t.Password))
        {
            try
            {
                return new PrivateKeyFile(t.KeyPath);
            }
            catch
            {
                throw new Exception(
                    $"could not read the SSH key '{t.KeyPath}': {ex.Message}" +
                    " (passphrase-protected keys use the Deploy password as the passphrase)", ex);
            }
        }
    }

    private static (int ExitCode, string Output) Run(SshClient client, string command, int timeoutSeconds = 300)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        cmd.Execute();
        var text = (cmd.Result + " " + cmd.Error).Trim();
        return (cmd.ExitStatus ?? -1, text);
    }

    public static void TestConnection(DeployTarget t, Action<string> log, AppConfig? cfg = null)
    {
        using var client = ConnectSsh(t);
        log($"connected to {t.Host}");
        var (code, output) = Run(client, "docker --version && docker compose version");
        if (code != 0)
            throw new Exception("Docker is not installed on the server yet - run the install step in server/VPS-SETUP.md: curl -fsSL https://get.docker.com | sh");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log("  " + line);
        log("docker is ready.");
        if (cfg != null)
        {
            var info = GetServerBundleInfo(t, cfg);
            log(info.DeployedVersion is null
                ? "  server bundle: not stamped yet (deploy once to record the version)"
                : $"  server bundle version: {info.DeployedVersion[..Math.Min(12, info.DeployedVersion.Length)]}");
            if (info.RunningConfigHash is null)
                log("  running relay config: could not read it (is the container running?)");
            else if (info.RunningConfigMatches)
                log("  running relay config matches this app - deploy is up to date");
            else
                log($"  running relay config is OLDER than this app (server {info.RunningConfigHash[..12]}, this app {info.ExpectedConfigHash[..12]}) - click Deploy to server");
        }
    }

    // Best-effort read of what the server actually runs. Never throws: every
    // field degrades to "unknown" on failure.
    public static ServerBundleInfo GetServerBundleInfo(DeployTarget t, AppConfig cfg)
    {
        bool containerRunning = false;
        string? version = null;
        string? runningHash = null;
        try
        {
            string containers = RelayPreviewService.RunOnServer(t,
                "sh -c 'docker ps --format \"{{.Names}}\" 2>/dev/null || sudo -n docker ps --format \"{{.Names}}\" 2>/dev/null'");
            containerRunning = containers.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("kat-relay");

            version = RelayPreviewService.RunOnServer(t, $"head -n 1 '{t.RemotePath}/BUNDLE-VERSION' 2>/dev/null").Trim();
            if (version.Length == 0) version = null;

            string hashOut = RelayPreviewService.RunOnServer(t,
                "sh -c 'docker exec kat-relay sha256sum /etc/nginx/nginx.conf 2>/dev/null || sudo -n docker exec kat-relay sha256sum /etc/nginx/nginx.conf 2>/dev/null'");
            runningHash = hashOut.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch
        {
            // Best effort - the caller shows "unknown".
        }
        return new ServerBundleInfo(containerRunning, version, runningHash, ServerExporter.ComputeNginxConfigHash(cfg));
    }

    public static void Deploy(AppConfig cfg, DeployTarget t, Action<string> log)
    {
        // The staged bundle contains nginx.conf with every real stream key -
        // clean it up whatever happens instead of leaving it in %TEMP% forever.
        string temp = Path.Combine(Path.GetTempPath(), "kat-relay-deploy");
        try
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            ServerExporter.Export(temp, cfg, log);
            log($"bundle exported (version {ServerExporter.ComputeBundleHash(cfg)[..12]})");

            using var client = ConnectSsh(t);
            log($"connected to {t.Host}");
            ValidateIngestHosts(cfg, client, log);

            // Plain `sudo` blocks forever waiting for a password. Detect it once:
            // `sudo -n` fails immediately instead of hanging the deploy.
            var (sudoCode, _) = Run(client, "sudo -n true 2>/dev/null", 15);
            bool passwordlessSudo = sudoCode == 0;
            string sudo = passwordlessSudo ? "sudo -n" : "";
            if (passwordlessSudo)
                log("sudo: passwordless");
            else
                log("sudo needs a password - falling back to direct docker access (the SSH user needs the docker group)");

            using (var sftp = ConnectSftp(t))
            {
                EnsureRemoteDir(sftp, client, t.RemotePath, log);
                foreach (var file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(temp, file);
                    string remoteDir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                    if (remoteDir.Length > 0)
                        EnsureRemoteDirPath(sftp, t.RemotePath, remoteDir);
                    using var fs = File.OpenRead(file);
                    sftp.UploadFile(fs, $"{t.RemotePath}/{relative.Replace('\\', '/')}", true);
                    log("  uploaded " + relative);
                }
            }
            log("upload complete");

            // Cloud VPS MTU mismatch (Oracle's 9000-byte VNICs vs the 1500-byte
            // internet) blackholes large RTMP packets: OBS connects and dies a few
            // seconds in and pushes to platforms stall. Idempotent, best-effort.
            log("applying network hardening (MTU/MSS clamp)...");
            if (passwordlessSudo)
            {
                var (hardenCode, hardenOut) = Run(client, $"cd '{t.RemotePath}' && sudo -n sh server-hardening.sh 2>&1");
                foreach (var line in hardenOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    log("  " + line);
                if (hardenCode != 0)
                    log("  note: hardening failed - if OBS connects then drops after a few seconds, ssh in and run: sudo sh " + t.RemotePath + "/server-hardening.sh");
            }
            else
            {
                log("  skipped (no passwordless sudo) - if OBS connects then drops after a few seconds, ssh in and run: sudo sh " + t.RemotePath + "/server-hardening.sh");
            }

            var (code, output) = Run(client, $"cd '{t.RemotePath}' && {sudo} docker compose up -d --build 2>&1", 600);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                log("  " + line);
            if (code != 0)
            {
                string hint = passwordlessSudo
                    ? ""
                    : " - if this was a permission error, add your SSH user to the docker group (sudo usermod -aG docker "
                      + t.User + ") or configure passwordless sudo";
                throw new Exception($"deploy failed (exit code {code}) - see log above{hint}");
            }
            log("deploy complete - relay is running");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    // nginx resolves every push host while PARSING the config - one dead
    // hostname and the whole relay container fails to boot (restart loop),
    // which just looks like "nothing works". Catch it before the deploy.
    private static void ValidateIngestHosts(AppConfig cfg, SshClient client, Action<string> log)
    {
        var hosts = cfg.Destinations
            .Where(d => d.Enabled)
            .Select(d => IngestHost(d.IngestUrl))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dead = new List<string>();
        foreach (var host in hosts)
        {
            var (code, _) = Run(client, $"getent ahostsv4 {host} >/dev/null 2>&1");
            if (code != 0)
                dead.Add(host);
        }

        if (dead.Count == 0)
        {
            if (hosts.Count > 0)
                log($"ingest hosts OK ({hosts.Count} checked)");
            return;
        }

        throw new Exception(
            "these enabled destinations use ingest hosts that do not resolve on the server: "
            + string.Join(", ", dead)
            + " - fix the URL (some platform defaults are outdated) or disable the destination, otherwise nginx cannot start");
    }

    private static string? IngestHost(string ingestUrl)
    {
        var url = ingestUrl.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;
        if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            return url["rtmp://".Length..].Split('/')[0];
        return null;
    }

    // SFTP cannot use sudo; on sudo-only servers (e.g. Oracle Linux's opc user) we
    // create the folder through the SSH session instead.
    private static void EnsureRemoteDir(SftpClient sftp, SshClient ssh, string remotePath, Action<string> log)
    {
        if (sftp.Exists(remotePath))
            return;
        try
        {
            sftp.CreateDirectory(remotePath);
        }
        catch
        {
            log("folder needs admin rights - creating it with sudo...");
            var (code, output) = Run(ssh, $"sudo -n mkdir -p '{remotePath}' && sudo -n chown \"$(id -un)\" '{remotePath}'");
            if (code != 0)
                throw new Exception($"could not create {remotePath}: {output}");
        }
    }

    // Creates every level of a nested remote folder - the old code made exactly
    // one level, so a two-level relative path (assets/img) failed.
    private static void EnsureRemoteDirPath(SftpClient sftp, string remoteRoot, string relativeDir)
    {
        string current = remoteRoot.TrimEnd('/');
        foreach (var part in relativeDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!sftp.Exists(current))
                sftp.CreateDirectory(current);
        }
    }

    // Reads the relay's stats endpoint through the server's loopback interface,
    // so nothing (not even the stats) is exposed to the internet.
    public static string FetchStatus(DeployTarget t, AppConfig? cfg = null)
    {
        var (reachable, receiving) = FetchSnapshot(t);
        if (!reachable)
            return "relay not responding - deploy the bundle first";
        string status = receiving ? "receiving your stream" : "relay running, waiting for OBS";
        if (cfg != null)
        {
            var info = GetServerBundleInfo(t, cfg);
            status += info.RunningConfigHash is null
                ? " | server bundle: unverified"
                : info.RunningConfigMatches
                    ? $" | server bundle up to date ({info.ExpectedConfigHash[..12]})"
                    : $" | server bundle OUTDATED (server {info.RunningConfigHash[..12]} vs app {info.ExpectedConfigHash[..12]}) - redeploy";
        }
        return status;
    }

    public static (bool Reachable, bool Receiving) FetchSnapshot(DeployTarget t)
    {
        // Runs through the shared persistent SSH session (same one the preview
        // service keeps alive) instead of opening a brand-new connection every
        // 10 seconds forever.
        string output = RelayPreviewService.RunOnServer(t,
            "curl -s --max-time 3 http://127.0.0.1:8080/stat || wget -qO- -T 3 http://127.0.0.1:8080/stat || true", 20);
        if (string.IsNullOrWhiteSpace(output) || !output.Contains("<rtmp"))
            return (false, false);
        return (true, output.Contains("<publishing"));
    }
}
