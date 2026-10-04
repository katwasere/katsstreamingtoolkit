using System.IO;
using System.Text;
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

// What the watchdog sees on one pass: relay reachability, OBS publisher state,
// which routed destinations' encoders are running, how stale each encoder's
// preview snapshot is, and the relay's recent error log.
public sealed record RelayHealthSnapshot(
    bool Reachable,
    bool Receiving,
    HashSet<Guid> Encoders,
    Dictionary<Guid, int> SnapshotAges,
    string LogTail)
{
    public static readonly RelayHealthSnapshot Down = new(false, false, new HashSet<Guid>(), new Dictionary<Guid, int>(), "");
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

    // Runs a command and forwards its output to `log` AS IT ARRIVES instead of
    // buffering until the command finishes - `docker compose up -d --build`
    // can run for minutes and the old per-step log left the Deploy tab frozen
    // the whole time. Returns the exit status (-1 on timeout/connection drop,
    // same contract as Run).
    private static int RunStreaming(SshClient client, string command, Action<string> log, int timeoutSeconds = 300)
    {
        var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var asyncResult = cmd.BeginExecute();
        using var stdout = cmd.OutputStream;
        using var stderr = cmd.ExtendedOutputStream;

        // Drain stderr concurrently: the remote side blocks once a full channel
        // window is unread, so an ignored chatty stderr could stall stdout too
        // (every streamed command appends 2>&1, so this is belt-and-braces).
        var stderrTask = Task.Run(() =>
        {
            var buffer = new byte[8192];
            var all = new MemoryStream();
            try
            {
                int read;
                while ((read = stderr.Read(buffer, 0, buffer.Length)) > 0)
                    all.Write(buffer, 0, read);
            }
            catch
            {
                // Channel torn down (timeout/abort) - keep what we got.
            }
            return Encoding.UTF8.GetString(all.ToArray());
        });

        try
        {
            var buffer = new byte[16 * 1024];
            var chars = new char[16 * 1024];
            var decoder = Encoding.UTF8.GetDecoder();
            var pending = new StringBuilder();
            int read;
            while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                pending.Append(chars, 0, count);
                FlushLines(pending, log, final: false);
            }
            FlushLines(pending, log, final: true);
            cmd.EndExecute(asyncResult);

            string err = stderrTask.GetAwaiter().GetResult();
            foreach (var line in err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                log(line);
            return cmd.ExitStatus ?? -1;
        }
        catch
        {
            // CommandTimeout closes the channel: reads end, EndExecute throws.
            return -1;
        }
        finally
        {
            try { cmd.Dispose(); } catch { }
        }
    }

    // Emits complete lines from `pending` (split on \n and \r - docker's
    // progress bars overwrite with \r), leaving a trailing partial line
    // buffered until more output arrives.
    private static void FlushLines(StringBuilder pending, Action<string> log, bool final)
    {
        string text = pending.ToString();
        pending.Clear();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOfAny(new[] { '\r', '\n' }, start);
            if (nl < 0) break;
            EmitLine(text[start..nl]);
            start = nl + 1;
        }
        string rest = text[start..];
        if (final)
            EmitLine(rest);
        else if (rest.Length > 0)
            pending.Append(rest);

        void EmitLine(string raw)
        {
            var line = raw.Trim();
            if (line.Length > 0) log(line);
        }
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
                int hardenCode = RunStreaming(client, $"cd '{t.RemotePath}' && sudo -n sh server-hardening.sh 2>&1", l => log("  " + l));
                if (hardenCode != 0)
                    log("  note: hardening failed - if OBS connects then drops after a few seconds, ssh in and run: sudo sh " + t.RemotePath + "/server-hardening.sh");
            }
            else
            {
                log("  skipped (no passwordless sudo) - if OBS connects then drops after a few seconds, ssh in and run: sudo sh " + t.RemotePath + "/server-hardening.sh");
            }

            int code = RunStreaming(client, $"cd '{t.RemotePath}' && {sudo} docker compose up -d --build 2>&1", l => log("  " + l), 600);
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

    public static string? IngestHost(string ingestUrl)
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

    // ---------- watchdog health snapshot ----------
    //
    // One command per tick fetches everything the watchdog needs: the relay's
    // /stat (reachable + OBS publishing), the container's ffmpeg command lines
    // (which routed encoders are alive - each contains its kat-preview-<id>
    // snapshot path), the age of every preview snapshot (a stale file means a
    // frozen encoder), and the relay error log tail (the "why"). The process
    // dump contains stream keys, so it never leaves this method - only boolean
    // per-destination facts do.

    public static RelayHealthSnapshot FetchHealthSnapshot(DeployTarget t, AppConfig cfg)
    {
        string output = RelayPreviewService.RunOnServer(t, BuildHealthCommand(), 30);
        return ParseHealthSnapshot(output, cfg);
    }

    private static string BuildHealthCommand()
    {
        // The whole pass is one base64 script piped into sh, and each container
        // script is itself base64 piped into `docker exec -i kat-relay sh`.
        // Reason: the first version nested the container scripts inside double
        // quotes of a host `sh -c`, and the host shell expanded every $() and
        // $var meant for the CONTAINER - the encoder list came back empty and
        // every routed destination falsely flagged "ENCODER IS DOWN". Base64
        // over stdin carries the scripts with zero quoting layers.
        const string procs =
            "for p in /proc/[0-9]*/cmdline; do c=$(tr '\\000' ' ' <\"$p\" 2>/dev/null); case \"$c\" in *ffmpeg*) echo \"$c\";; esac; done";
        const string ages =
            "now=$(date +%s); for f in /tmp/kat-preview-*.jpg; do [ -f \"$f\" ] || continue; m=$(stat -c %Y \"$f\" 2>/dev/null) || continue; echo \"$(basename \"$f\") $((now-m))\"; done";
        const string logTail = "tail -n 60 /var/log/nginx/error.log";

        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        string procsB64 = B64(procs);
        string agesB64 = B64(ages);
        string logB64 = B64(logTail);

        // `A | docker exec ... || A | sudo -n docker exec ...` - pipe binds
        // tighter than ||, so the sudo variant only runs when the plain docker
        // call failed (no docker group). -i feeds the script on stdin.
        string hostScript =
            "S=$(curl -s --max-time 3 http://127.0.0.1:8080/stat 2>/dev/null || wget -qO- -T 3 http://127.0.0.1:8080/stat 2>/dev/null)\n" +
            "echo STAT\n" +
            "printf '%s' \"$S\" | base64\n" +
            "echo\n" +
            "echo PROCS\n" +
            $"echo {procsB64} | base64 -d | docker exec -i kat-relay sh 2>/dev/null || echo {procsB64} | base64 -d | sudo -n docker exec -i kat-relay sh 2>/dev/null\n" +
            "echo AGES\n" +
            $"echo {agesB64} | base64 -d | docker exec -i kat-relay sh 2>/dev/null || echo {agesB64} | base64 -d | sudo -n docker exec -i kat-relay sh 2>/dev/null\n" +
            "echo LOG\n" +
            $"echo {logB64} | base64 -d | docker exec -i kat-relay sh 2>/dev/null || echo {logB64} | base64 -d | sudo -n docker exec -i kat-relay sh 2>/dev/null\n";

        return "echo " + B64(hostScript) + " | base64 -d | sh";
    }

    private static RelayHealthSnapshot ParseHealthSnapshot(string output, AppConfig cfg)
    {
        string statB64 = "";
        var procs = new List<string>();
        var ageLines = new List<string>();
        var logLines = new List<string>();

        string section = "";
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed is "STAT" or "PROCS" or "AGES" or "LOG")
            {
                section = trimmed;
                continue;
            }
            switch (section)
            {
                case "STAT": statB64 += trimmed; break;
                case "PROCS": if (trimmed.Length > 0) procs.Add(trimmed); break;
                case "AGES": if (trimmed.Length > 0) ageLines.Add(trimmed); break;
                case "LOG": if (trimmed.Length > 0) logLines.Add(trimmed); break;
            }
        }

        string xml;
        try
        {
            xml = Encoding.UTF8.GetString(Convert.FromBase64String(statB64.Trim()));
        }
        catch
        {
            // No stat (relay down / base64 unavailable).
            return RelayHealthSnapshot.Down;
        }
        if (!xml.Contains("<rtmp"))
            return RelayHealthSnapshot.Down;

        var routed = cfg.Destinations
            .Where(d => d.Enabled &&
                        (d.Orientation == Orientation.Portrait || d.PortraitStyle == PortraitStyle.Custom) &&
                        !string.IsNullOrWhiteSpace(d.IngestUrl))
            .ToList();

        var encoders = new HashSet<Guid>();
        var ages = new Dictionary<Guid, int>();
        foreach (var d in routed)
        {
            string token = d.Id.ToString("N");
            if (procs.Any(p => p.Contains("kat-preview-" + token, StringComparison.Ordinal)))
                encoders.Add(d.Id);
            foreach (var ageLine in ageLines)
            {
                // "<file> <age-seconds>"
                int sp = ageLine.IndexOf(' ');
                if (sp <= 0) continue;
                if (!ageLine[..sp].Contains(token, StringComparison.Ordinal)) continue;
                if (int.TryParse(ageLine[(sp + 1)..].Trim(), out int age) && age >= 0)
                    ages[d.Id] = age;
                break;
            }
        }

        return new RelayHealthSnapshot(true, xml.Contains("<publishing"), encoders, ages,
            string.Join('\n', logLines));
    }

    // Lines from the relay log that indicate a real failure (used for the
    // watchdog's "why" and for landscape-push detection).
    public static bool IsErrorLine(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
        || line.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
        || line.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
        || line.Contains("no such file", StringComparison.OrdinalIgnoreCase)
        || line.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
        || line.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
        || line.Contains("connection timed out", StringComparison.OrdinalIgnoreCase)
        || line.Contains("handshake", StringComparison.OrdinalIgnoreCase);

    // The most relevant recent relay-log line for a destination: one that
    // mentions its ingest host if there is any, otherwise the newest error line.
    public static string? FindLogReason(string logTail, AppConfig cfg, DestinationConfig dest)
    {
        var lines = logTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? host = IngestHost(dest.IngestUrl);
        string? hostMatch = null;
        if (host != null)
            hostMatch = lines.LastOrDefault(l => l.Contains(host, StringComparison.OrdinalIgnoreCase) && IsErrorLine(l));
        string? line = hostMatch ?? lines.LastOrDefault(IsErrorLine);
        if (line == null) return null;
        return RedactSecrets(line, cfg);
    }

    public static bool LogHasHostError(string logTail, string host) =>
        logTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(l => l.Contains(host, StringComparison.OrdinalIgnoreCase) && IsErrorLine(l));

    // Relay log lines can echo push URLs (which contain stream keys) - mask
    // every configured key before the text reaches the deploy log, which may be
    // visible on stream.
    public static string RedactSecrets(string text, AppConfig cfg)
    {
        foreach (var d in cfg.Destinations)
        {
            if (!string.IsNullOrWhiteSpace(d.StreamKey))
                text = text.Replace(d.StreamKey, "\u2022\u2022\u2022\u2022", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
