using System.IO;
using KatStreamToolkit.Models;
using Renci.SshNet;

namespace KatStreamToolkit.Services;

public sealed record DeployTarget(string Host, string User, bool UseKey, string Password, string KeyPath, string RemotePath);

// SSH/SFTP operations for the Deploy tab. Everything runs on a background thread;
// callers get progress through a log callback.
public static class DeployService
{
    // Internal connect reused by the preview service; DeployService stays the
    // single place that knows how to open SSH.
    public static SshClient ConnectSsh(DeployTarget t)
    {
        AuthenticationMethod auth = t.UseKey
            ? new PrivateKeyAuthenticationMethod(t.User, new PrivateKeyFile(t.KeyPath))
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
            ? new PrivateKeyAuthenticationMethod(t.User, new PrivateKeyFile(t.KeyPath))
            : new PasswordAuthenticationMethod(t.User, t.Password);
        var info = new ConnectionInfo(t.Host, t.User, auth) { Timeout = TimeSpan.FromSeconds(15) };
        var sftp = new SftpClient(info);
        sftp.Connect();
        if (!sftp.IsConnected)
            throw new Exception("could not open SFTP session");
        return sftp;
    }

    private static (int ExitCode, string Output) Run(SshClient client, string command, int timeoutSeconds = 300)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        cmd.Execute();
        var text = (cmd.Result + " " + cmd.Error).Trim();
        return (cmd.ExitStatus ?? -1, text);
    }

    public static void TestConnection(DeployTarget t, Action<string> log)
    {
        using var client = ConnectSsh(t);
        log($"connected to {t.Host}");
        var (code, output) = Run(client, "docker --version && docker compose version");
        if (code != 0)
            throw new Exception("Docker is not installed on the server yet - run the install step in server/VPS-SETUP.md: curl -fsSL https://get.docker.com | sh");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log("  " + line);
        log("docker is ready.");
    }

    public static void Deploy(AppConfig cfg, DeployTarget t, Action<string> log)
    {
        string temp = Path.Combine(Path.GetTempPath(), "kat-relay-deploy");
        if (Directory.Exists(temp))
            Directory.Delete(temp, true);
        ServerExporter.Export(temp, cfg);
        log("bundle exported");

        using var client = ConnectSsh(t);
        log($"connected to {t.Host}");
        ValidateIngestHosts(cfg, client, log);

        using (var sftp = ConnectSftp(t))
        {
            EnsureRemoteDir(sftp, client, t.RemotePath, log);
            foreach (var file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(temp, file);
                string remoteDir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                if (remoteDir.Length > 0 && !sftp.Exists($"{t.RemotePath}/{remoteDir}"))
                    sftp.CreateDirectory($"{t.RemotePath}/{remoteDir}");
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
        var (hardenCode, hardenOut) = Run(client,
            $"cd '{t.RemotePath}' && (sudo -n sh server-hardening.sh 2>&1 || sudo sh server-hardening.sh 2>&1 || echo HARDENING_SKIPPED)");
        foreach (var line in hardenOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log("  " + line);
        if (hardenCode != 0 || hardenOut.Contains("HARDENING_SKIPPED"))
            log("  note: could not apply MTU hardening (sudo needs a password?). " +
                "If OBS connects then drops after a few seconds, ssh in and run: sudo sh " + t.RemotePath + "/server-hardening.sh");

        var (code, output) = Run(client, $"cd '{t.RemotePath}' && sudo docker compose up -d --build 2>&1", 600);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log("  " + line);
        if (code != 0)
            throw new Exception($"deploy failed (exit code {code}) - see log above");
        log("deploy complete - relay is running");
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
            var (code, output) = Run(ssh, $"sudo mkdir -p '{remotePath}' && sudo chown \"$(id -un)\" '{remotePath}'");
            if (code != 0)
                throw new Exception($"could not create {remotePath}: {output}");
        }
    }

    // Reads the relay's stats endpoint through the server's loopback interface,
    // so nothing (not even the stats) is exposed to the internet.
    public static string FetchStatus(DeployTarget t)
    {
        var (reachable, receiving) = FetchSnapshot(t);
        if (!reachable)
            return "relay not responding - deploy the bundle first";
        return receiving ? "receiving your stream" : "relay running, waiting for OBS";
    }

    public static (bool Reachable, bool Receiving) FetchSnapshot(DeployTarget t)
    {
        using var client = ConnectSsh(t);
        var (_, output) = Run(client,
            "curl -s --max-time 3 http://127.0.0.1:8080/stat || wget -qO- -T 3 http://127.0.0.1:8080/stat || true", 20);
        if (string.IsNullOrWhiteSpace(output) || !output.Contains("<rtmp"))
            return (false, false);
        return (true, output.Contains("<publishing"));
    }
}
