using System.IO;
using System.Text;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

public static class ServerExporter
{
    public static void Export(string folder, AppConfig cfg, Action<string>? log = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Dockerfile"), Dockerfile);
        File.WriteAllText(Path.Combine(folder, "docker-compose.yml"), ComposeYml);
        File.WriteAllText(Path.Combine(folder, "nginx.conf"), RelayConfigGenerator.GenerateNginxConf(cfg));
        File.WriteAllText(Path.Combine(folder, "SETUP.md"), BuildSetupGuide(cfg, folder));
        // Cloud VPSs (Oracle in particular) advertise a giant NIC MTU while the
        // internet path is 1500, and dropped ICMP "fragmentation needed" then
        // blackholes every large RTMP packet: OBS connects and dies a few
        // seconds in, and pushes to platforms never take off. The script clamps
        // the advertised MSS and turns on kernel MTU probing. Idempotent.
        File.WriteAllText(Path.Combine(folder, "server-hardening.sh"), ServerHardening);
        // Host networking: Docker no longer manages resolv.conf, and a flaky single
        // resolver stalls the encoders before they read the stream. Redundant VCN +
        // public resolvers with retries keep platform ingest names resolving.
        File.WriteAllText(Path.Combine(folder, "resolv.conf"), ResolvConf);
        // Version stamp: the toolkit reads this back from the server so "is the
        // deployed relay current?" is a check against the running container,
        // not a guess.
        File.WriteAllText(Path.Combine(folder, "BUNDLE-VERSION"),
            ComputeBundleHash(cfg) + "\n" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC\n");
        // Keys live inside nginx.conf; keep them out of any git repo.
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "nginx.conf\n");
        ExportCustomBackgrounds(folder, cfg, log);
    }

    private const string ResolvConf =
        "nameserver 169.254.169.254\nnameserver 1.1.1.1\nnameserver 8.8.8.8\noptions timeout:2 attempts:3 rotate\n";

    // Hash of everything Export() writes that changes relay behavior, in a
    // fixed order. Stamped into BUNDLE-VERSION on every export.
    public static string ComputeBundleHash(AppConfig cfg)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        void Feed(string name, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"### {name} ###\n{content}");
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        Feed("Dockerfile", Dockerfile);
        Feed("docker-compose.yml", ComposeYml);
        Feed("nginx.conf", RelayConfigGenerator.GenerateNginxConf(cfg));
        Feed("server-hardening.sh", ServerHardening);
        Feed("resolv.conf", ResolvConf);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // Hash of just the live relay config, comparable against the RUNNING
    // container's /etc/nginx/nginx.conf (they are the same bytes when the
    // deploy is current - both come from GenerateNginxConf).
    public static string ComputeNginxConfigHash(AppConfig cfg)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(RelayConfigGenerator.GenerateNginxConf(cfg))))
            .ToLowerInvariant();
    }

    // A missing image no longer aborts the entire export/deploy: the file is
    // skipped with a warning and the rest of the bundle still ships.
    private static void ExportCustomBackgrounds(string folder, AppConfig cfg, Action<string>? log)
    {
        void Warn(string message) => log?.Invoke("  warning: " + message);

        foreach (var dest in cfg.Destinations.Where(d => d.PortraitStyle == PortraitStyle.Custom && !string.IsNullOrWhiteSpace(d.CustomBackgroundPath)))
        {
            if (!File.Exists(dest.CustomBackgroundPath))
            {
                Warn($"custom background for '{dest.Name}' not found on disk, skipping: {dest.CustomBackgroundPath}");
                continue;
            }
            string backgrounds = Path.Combine(folder, "backgrounds");
            Directory.CreateDirectory(backgrounds);
            File.Copy(dest.CustomBackgroundPath, Path.Combine(backgrounds, RelayConfigGenerator.BackgroundFileName(dest)), true);
        }

        foreach (var dest in cfg.Destinations)
        {
            foreach (var layer in dest.Layers.Where(l => l.Type == LayerType.Image && !string.IsNullOrWhiteSpace(l.Path)))
            {
                if (!File.Exists(layer.Path))
                {
                    Warn($"overlay image for '{dest.Name}' / '{layer.Name}' not found on disk, skipping: {layer.Path}");
                    continue;
                }
                string backgrounds = Path.Combine(folder, "backgrounds");
                Directory.CreateDirectory(backgrounds);
                File.Copy(layer.Path, Path.Combine(backgrounds, RelayConfigGenerator.OverlayFileName(layer)), true);
            }
        }
    }

    private static string Dockerfile =>
        """
        FROM ubuntu:24.04
        ENV DEBIAN_FRONTEND=noninteractive
        RUN apt-get update \
         && apt-get install -y --no-install-recommends nginx libnginx-mod-rtmp ffmpeg ca-certificates \
         && rm -rf /var/lib/apt/lists/*
        COPY nginx.conf /etc/nginx/nginx.conf
        EXPOSE 1935
        STOPSIGNAL SIGQUIT
        CMD ["nginx", "-g", "daemon off;"]
        """;

    private static string ComposeYml =>
        """
        services:
          relay:
            build: .
            image: kat-relay
            container_name: kat-relay
            restart: unless-stopped
            # Host networking: nginx binds 1935/8080 directly on the VPS. Docker's
            # forwarded-port path (DNAT + FORWARD chain) has been observed dropping
            # RTMP data packets on Oracle Cloud images; host mode sidesteps it all.
            network_mode: host
            volumes:
              # Host networking: Docker no longer manages resolv.conf, and a flaky
              # single resolver would stall the encoders before they read the
              # stream - supply a redundant resolver setup instead.
              - ./resolv.conf:/etc/resolv.conf:ro
              - ./backgrounds:/var/kat-backgrounds:ro
            logging:
              driver: json-file
              options:
                max-size: "10m"
                max-file: "3"
        """;

    // Clamps the TCP MSS the server advertises and enables kernel MTU probing.
    // Fixes the classic cloud-VPS failure: RTMP handshake succeeds (small
    // packets), video data (big packets) is silently dropped, OBS disconnects
    // after a few seconds and platform pushes stall. Safe to re-run.
    private static string ServerHardening => """
        #!/bin/sh
        # Oracle Cloud VNICs advertise MTU 9000 while the internet uses 1500, and
        # their security lists usually drop ICMP "fragmentation needed". TCP then
        # silently drops every large segment: OBS connects, stalls and disconnects
        # seconds into the stream, and pushes out to the platforms never start.
        # This clamps the MSS the server advertises in SYN replies and lets the
        # kernel probe the real path MTU as a fallback.

        CLAMP="-p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu"

        iptables -t mangle -C OUTPUT $CLAMP 2>/dev/null || iptables -t mangle -A OUTPUT $CLAMP
        iptables -t mangle -C FORWARD $CLAMP 2>/dev/null || iptables -t mangle -A FORWARD $CLAMP

        printf "net.ipv4.tcp_mtu_probing=1\n" > /etc/sysctl.d/99-kat-relay-mtu.conf
        sysctl -w net.ipv4.tcp_mtu_probing=1 >/dev/null

        # Re-apply the clamp automatically after a server reboot.
        cat > /etc/systemd/system/kat-relay-mss.service <<'UNIT'
        [Unit]
        Description=Clamp TCP MSS so RTMP video survives cloud MTU mismatch (kat-relay)
        After=network.target

        [Service]
        Type=oneshot
        ExecStart=/bin/sh -c 'iptables -t mangle -C OUTPUT -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || iptables -t mangle -A OUTPUT -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu; iptables -t mangle -C FORWARD -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu 2>/dev/null || iptables -t mangle -A FORWARD -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu'
        RemainAfterExit=yes

        [Install]
        WantedBy=multi-user.target
        UNIT
        systemctl daemon-reload 2>/dev/null
        systemctl enable kat-relay-mss.service >/dev/null 2>&1

        echo "MTU/MSS hardening applied: MSS clamp on OUTPUT+FORWARD, tcp_mtu_probing=1, re-applied on boot."
        """;

    private static string BuildSetupGuide(AppConfig cfg, string exportFolder)
    {
        var sb = new StringBuilder();
        string host = string.IsNullOrWhiteSpace(cfg.ServerHost) ? "YOUR.SERVER.IP" : cfg.ServerHost.Trim();
        string sshUser = string.IsNullOrWhiteSpace(cfg.SshUser) ? "root" : cfg.SshUser.Trim();
        string remotePath = string.IsNullOrWhiteSpace(cfg.RemotePath) ? "/opt/kat-relay" : cfg.RemotePath.Trim();
        // The exported folder itself (not the app's current working directory).
        string localFolder = Path.GetFullPath(exportFolder);
        double upMbps = cfg.Upstream.VideoBitrateKbps / 1000.0 * 1.1;
        double serverMbps = cfg.Destinations.Where(d => d.Enabled).Sum(d => (d.VideoBitrateKbps + d.AudioBitrateKbps) / 1000.0) * 1.1;

        sb.AppendLine("# KAT relay - server setup");
        sb.AppendLine();
        sb.AppendLine("This folder is a complete restream server. One stream goes in, copies go out to every enabled destination.");
        sb.AppendLine();
        sb.AppendLine("## 1. Get a cheap server (any Ubuntu VPS works)");
        sb.AppendLine("- Hetzner CX22 (~EUR 4/mo, 20 TB traffic) or Oracle Cloud free tier are good picks.");
        sb.AppendLine($"- Pick a location with good bandwidth to your home AND to the platforms (Falkenstein/Frankfurt EU, or Ashburn for US).");
        sb.AppendLine();
        sb.AppendLine("## 2. Install Docker on the server (once)");
        sb.AppendLine("```bash");
        sb.AppendLine("curl -fsSL https://get.docker.com | sh");
        sb.AppendLine("ufw allow OpenSSH && ufw allow 1935/tcp && ufw --force enable");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 3. Upload this folder to the server");
        sb.AppendLine("```bash");
        sb.AppendLine($"scp -r \"{localFolder}\" {sshUser}@{host}:{remotePath}");
        sb.AppendLine("```");
        sb.AppendLine("Or use WinSCP (drag and drop, GUI).");
        sb.AppendLine();
        sb.AppendLine("## 4. Start it");
        sb.AppendLine("```bash");
        sb.AppendLine($"cd {remotePath} && docker compose up -d --build");
        sb.AppendLine("docker compose logs -f   # Ctrl+C to stop watching");
        sb.AppendLine("```");
        sb.AppendLine("If you deploy by hand, also run the network hardening once (the toolkit's 'Deploy to server' button runs it on every deploy):");
        sb.AppendLine("```bash");
        sb.AppendLine($"sudo sh {remotePath}/server-hardening.sh   # fixes OBS connecting then dropping a few seconds in (cloud MTU mismatch)");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 5. Point OBS at it");
        sb.AppendLine("OBS -> Settings -> Stream -> Service: Custom...");
        sb.AppendLine($"- Server: `rtmp://{host}/live`");
        sb.AppendLine($"- Stream key: `{cfg.Upstream.StreamName}` (this is your secret password - anything can push with it)");
        sb.AppendLine();
        sb.AppendLine("## Changing destinations later");
        sb.AppendLine("Edit destinations in the toolkit, then click 'Deploy to server' on the Deploy tab");
        sb.AppendLine("(or re-export + upload + `docker compose up -d --build` by hand).");
        sb.AppendLine();
        sb.AppendLine("## Health status");
        sb.AppendLine("The relay exposes stats on the server's loopback only (127.0.0.1:8080).");
        sb.AppendLine("The toolkit's Deploy tab reads it over SSH - nothing is open to the internet.");
        sb.AppendLine("Quick manual check: ssh in and run `curl -s http://127.0.0.1:8080/health`.");
        sb.AppendLine();
        sb.AppendLine("## Traffic math");
        sb.AppendLine($"- Your home upload sends one stream: ~{upMbps:F1} Mbps (~{upMbps * 0.45:F1} GB/hour).");
        sb.AppendLine($"- The server sends every enabled destination: ~{serverMbps:F1} Mbps total (~{serverMbps * 0.45:F1} GB/hour, ~{serverMbps * 0.45 * 720 / 1000:F1} TB if live 24/7).");
        sb.AppendLine("- Stay under your VPS traffic allowance; scale bitrates down if needed.");
        sb.AppendLine();
        sb.AppendLine("## Security notes");
        sb.AppendLine("- nginx.conf contains your platform stream keys. Never commit it or share the folder publicly.");
        sb.AppendLine("- The `live` app accepts anyone who knows your stream key, so keep it long and random.");
        return sb.ToString();
    }
}
