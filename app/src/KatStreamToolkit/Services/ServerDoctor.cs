using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// One line of the server doctor checklist. State: "ok" | "warn" | "fail" |
// "info". Fix is the exact command (or console path) that repairs the problem,
// printed for copy-paste.
public sealed record DoctorCheck(string Name, string State, string Detail, string? Fix);

// The server doctor: everything a fresh VPS needs for the relay, checked in
// ONE SSH pass (the same one-command-per-action rule as the watchdog), plus
// one outside view the server cannot have - whether TCP 1935 is actually
// reachable from the toolkit's machine. Degrades to "unknown" lines, never
// throws past RunAsync.
public static class ServerDoctor
{
    public static async Task<List<DoctorCheck>> RunAsync(DeployTarget t, AppConfig cfg)
    {
        var checks = new List<DoctorCheck>();
        string raw = await Task.Run(() => RelayPreviewService.RunOnServer(t, BuildScript(), 60));

        var sections = SplitSections(raw, "DOCKER", "RELAY", "DISK", "MEM", "MSS", "UFW", "PROV");
        string docker = Val(sections, "DOCKER");
        string relay = Val(sections, "RELAY");
        string disk = Val(sections, "DISK");
        string mem = Val(sections, "MEM");
        string mss = Val(sections, "MSS");
        string ufw = Val(sections, "UFW");
        string prov = Val(sections, "PROV");

        // Docker installed and usable by this SSH user (plain OR sudo -n).
        if (docker.Contains("Docker version", StringComparison.OrdinalIgnoreCase))
            checks.Add(new DoctorCheck("Docker", "ok", docker.Trim(), null));
        else
            checks.Add(new DoctorCheck("Docker", "fail", "docker is missing or not usable over SSH",
                "curl -fsSL https://get.docker.com | sh   # then: sudo usermod -aG docker " + t.User));

        // Relay container.
        if (relay.Trim() == "kat-relay")
            checks.Add(new DoctorCheck("Relay container", "ok", "kat-relay is running", null));
        else
            checks.Add(new DoctorCheck("Relay container", "fail", "the kat-relay container is not running",
                "cd " + t.RemotePath + " && docker compose up -d --build"));

        // Disk space on /.
        var dfParts = disk.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dfParts.Length >= 5 && double.TryParse(dfParts[4].TrimEnd('%'), out double usedPct))
        {
            checks.Add(usedPct >= 85
                ? new DoctorCheck("Disk space", "warn", $"root disk {usedPct:0}% used",
                    "docker system prune -af   # old images/build cache are the usual culprit")
                : new DoctorCheck("Disk space", "ok", $"root disk {usedPct:0}% used", null));
        }
        else
        {
            checks.Add(new DoctorCheck("Disk space", "info", "could not read df output", null));
        }

        // RAM (informational - the relay needs a few hundred MB).
        if (long.TryParse(mem.Trim(), out long memKb) && memKb > 0)
            checks.Add(new DoctorCheck("RAM", "info", $"{memKb / 1024.0 / 1024.0:0.0} GB total", null));

        // MTU/MSS hardening (the classic cloud-VPS stream killer).
        bool mssApplied = mss.Trim().Contains("1") || mss.Trim().Contains("clamp-mss-to-pmtu");
        checks.Add(mssApplied
            ? new DoctorCheck("MTU/MSS hardening", "ok", "MSS clamp / MTU probing active", null)
            : new DoctorCheck("MTU/MSS hardening", "warn",
                "not detected - on cloud VPSs (Oracle especially) OBS connects then drops seconds in",
                "sudo sh " + t.RemotePath + "/server-hardening.sh"));

        // Host firewall (ufw only - provider security lists are outside the box).
        // BUG-78: a blocked verdict needs BOTH "Status: active" and no 1935
        // rule - an inactive ufw filters nothing and must read "absent".
        string ufwTrimmed = ufw.Trim();
        bool ufwActive = ufwTrimmed.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        bool ufwHas1935 = ufwTrimmed.Contains("1935", StringComparison.Ordinal);
        UfwState = !ufwActive ? UfwFirewall.Absent
            : ufwHas1935 ? UfwFirewall.Open
            : UfwFirewall.Blocked;
        checks.Add(UfwState switch
        {
            UfwFirewall.Absent => new DoctorCheck("Host firewall (ufw)", "info",
                ufwTrimmed.Contains("no ufw", StringComparison.Ordinal)
                    ? "ufw not installed - nothing to check on the box (provider firewall is checked separately)"
                    : "ufw present but inactive - it filters nothing (provider firewall is checked separately)", null),
            UfwFirewall.Open => new DoctorCheck("Host firewall (ufw)", "ok", "ufw active and 1935 allowed", null),
            _ => new DoctorCheck("Host firewall (ufw)", "fail", "ufw is active but 1935 is not in the list",
                "sudo ufw allow OpenSSH && sudo ufw allow 1935/tcp   (or use the 'Apply ufw fix' button)"),
        });

        // Provider: determines whether a security list sits BETWEEN the internet
        // and this box (Oracle) or ufw on the box is the whole story.
        checks.Add(ProviderName(prov.Trim()) is { Length: > 0 } provider
            ? new DoctorCheck("Provider", "info", provider + (provider.Contains("Oracle", StringComparison.OrdinalIgnoreCase)
                ? " - security lists live OUTSIDE the server; port changes happen in the cloud console, ufw is not enough"
                : " - ufw on the box controls access"), null)
            : new DoctorCheck("Provider", "info", "unknown provider - if the port probe below fails, check both ufw and any cloud firewall", null));

        // Relay config freshness (local hash vs the container's live file).
        try
        {
            var info = DeployService.GetServerBundleInfo(t, cfg);
            checks.Add(info.ContainerRunning && info.RunningConfigMatches
                ? new DoctorCheck("Relay config", "ok", "the container runs this app's generated nginx.conf", null)
                : new DoctorCheck("Relay config", info.ContainerRunning ? "warn" : "fail",
                    info.ContainerRunning ? "the server runs an OLDER config than this app" : "no container to check",
                    "Deploy tab -> 'Deploy to server'"));
        }
        catch
        {
            checks.Add(new DoctorCheck("Relay config", "info", "could not compare config hashes", null));
        }

        // The one check only the OUTSIDE can answer: is 1935 reachable?
        try
        {
            bool reachable = await ProbePortAsync(t.Host, 1935, TimeSpan.FromSeconds(5));
            checks.Add(reachable
                ? new DoctorCheck("Port 1935 from outside", "ok", $"{t.Host}:1935 accepted a TCP connection", null)
                : new DoctorCheck("Port 1935 from outside", "fail",
                    $"{t.Host}:1935 refused or timed out - OBS cannot reach the relay",
                    "Open port 1935/tcp in the provider's firewall / security list (Oracle: VNIC subnet security list ingress rule)"));
        }
        catch
        {
            checks.Add(new DoctorCheck("Port 1935 from outside", "info", "probe failed (DNS?)", null));
        }

        return checks;
    }

    // TCP connect = "something listens here"; that is all OBS needs too.
    private static async Task<bool> ProbePortAsync(string host, int port, TimeSpan timeout)
    {
        using var client = new System.Net.Sockets.TcpClient();
        var connect = client.ConnectAsync(host, port);
        var done = await Task.WhenAny(connect, Task.Delay(timeout));
        return done == connect && client.Connected;
    }

    private static string BuildScript()
    {
        // Each check echoes a ### SECTION marker; failures print their own
        // placeholder so parsing stays simple.
        return string.Join("\n",
            "echo '### DOCKER'",
            "docker --version 2>/dev/null | head -n1 || echo 'no docker'",
            "echo '### RELAY'",
            "docker ps --format '{{.Names}}' 2>/dev/null | grep -x kat-relay || sudo -n docker ps --format '{{.Names}}' 2>/dev/null | grep -x kat-relay || echo 'no kat-relay'",
            "echo '### DISK'",
            "df -P / | tail -n1",
            "echo '### MEM'",
            "awk '/MemTotal/{print $2}' /proc/meminfo",
            "echo '### MSS'",
            // BUG-84: the old probe was "iptables ... | grep ... | head -n1 ||
            // sysctl ..." - head exits 0 even on EMPTY input, so the sysctl
            // fallback never ran and any non-root read (nft "Permission
            // denied" on stderr, suppressed) left the MSS section empty ->
            // permanent false "hardening not detected" even on a hardened
            // box (found live on the Hetzner relay right after a deploy that
            // HAD applied the clamp). The grep -q if/else keeps a real exit
            // status in the condition; plain iptables OR sudo -n iptables
            // covers root and non-root SSH users alike.
            "if iptables -t mangle -S OUTPUT 2>/dev/null | grep -q clamp-mss-to-pmtu || sudo -n iptables -t mangle -S OUTPUT 2>/dev/null | grep -q clamp-mss-to-pmtu; " +
            "then echo 'clamp-mss-to-pmtu rule present'; " +
            "else sysctl -n net.ipv4.tcp_mtu_probing 2>/dev/null || echo unknown; fi",
            "echo '### UFW'",
            // BUG-78: no head - a box with 20+ rules cut the 1935 line off and
            // the doctor flagged a healthy firewall as blocking. Full status;
            // the C# parse only needs "active" + whether 1935 appears.
            "command -v ufw >/dev/null && ufw status 2>/dev/null || echo 'no ufw'",
            "echo '### PROV'",
            "p=unknown; " +
            "curl -s -m 2 -H 'Authorization: Bearer Oracle' http://169.254.169.254/opc/v1/instance/ 2>/dev/null | grep -q canonicalRegionName && p=oracle; " +
            "curl -s -m 2 http://169.254.169.254/latest/meta-data/instance-id 2>/dev/null | grep -q '^i-' && p=aws; " +
            "curl -s -m 2 -H 'Metadata-Flavor: Google' http://metadata.google.internal/computeMetadata/v1/project/project-id 2>/dev/null | grep -q . && p=google; " +
            "curl -s -m 2 http://169.254.169.254/hetzner/v1/metadata/instance-id 2>/dev/null | grep -q . && p=hetzner; " +
            "echo $p");
    }

    public enum UfwFirewall
    {
        Unknown,
        Absent,
        Open,
        Blocked,
    }

    // Set by the last RunAsync: whether the "Apply ufw fix" button can do
    // anything. Static like the rest of the service class (one window, one
    // doctor run at a time).
    public static UfwFirewall UfwState { get; private set; } = UfwFirewall.Unknown;

    private static string ProviderName(string p) => p switch
    {
        "oracle" => "Oracle Cloud",
        "aws" => "Amazon Web Services",
        "google" => "Google Cloud",
        "hetzner" => "Hetzner",
        _ => "",
    };

    // The one-click fix: OpenSSH FIRST (never lock the user out), then 1935.
    // Passwordless sudo required - a password prompt would hang the SSH channel.
    public static async Task<string> ApplyUfwRules(DeployTarget t)
    {
        return await Task.Run(() =>
        {
            string output = RelayPreviewService.RunOnServer(t,
                "sudo -n ufw allow OpenSSH >/dev/null 2>&1 && sudo -n ufw allow 1935/tcp && sudo -n ufw reload >/dev/null 2>&1 && sudo -n ufw status | head -n 20",
                30);
            if (output.Contains("sudo: a password", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("sudo: no password", StringComparison.OrdinalIgnoreCase))
                return "needs passwordless sudo - or run by hand: sudo ufw allow OpenSSH && sudo ufw allow 1935/tcp";
            if (!output.Contains("1935", StringComparison.Ordinal))
                return "ufw did not confirm the rule - run by hand: sudo ufw allow OpenSSH && sudo ufw allow 1935/tcp";
            return output;
        });
    }

    private static Dictionary<string, string> SplitSections(string raw, params string[] names)
    {
        var dict = names.ToDictionary(n => n, _ => "");
        string current = "";
        foreach (var rawLine in raw.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("### ", StringComparison.Ordinal) &&
                names.FirstOrDefault(n => line == "### " + n) is { } found)
            {
                current = found;
                continue;
            }
            if (current.Length > 0 && line.Length > 0)
            {
                if (dict[current].Length > 0) dict[current] += "\n";
                dict[current] += line;
            }
        }
        return dict;
    }

    private static string Val(Dictionary<string, string> sections, string name) =>
        sections.TryGetValue(name, out var v) ? v : "";
}
