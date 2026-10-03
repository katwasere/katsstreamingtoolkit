using System.IO;
using System.Text;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

public static class ServerExporter
{
    public static void Export(string folder, AppConfig cfg)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Dockerfile"), Dockerfile);
        File.WriteAllText(Path.Combine(folder, "docker-compose.yml"), ComposeYml);
        File.WriteAllText(Path.Combine(folder, "nginx.conf"), RelayConfigGenerator.GenerateNginxConf(cfg));
        File.WriteAllText(Path.Combine(folder, "SETUP.md"), BuildSetupGuide(cfg));
        // Keys live inside nginx.conf; keep them out of any git repo.
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "nginx.conf\n");
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
            ports:
              - "1935:1935"
              - "127.0.0.1:8080:8080"
            logging:
              driver: json-file
              options:
                max-size: "10m"
                max-file: "3"
        """;

    private static string BuildSetupGuide(AppConfig cfg)
    {
        var sb = new StringBuilder();
        string host = string.IsNullOrWhiteSpace(cfg.ServerHost) ? "YOUR.SERVER.IP" : cfg.ServerHost.Trim();
        double upMbps = cfg.Upstream.VideoBitrateKbps / 1000.0 * 1.1;
        double serverMbps = cfg.Destinations.Where(d => d.Enabled).Sum(d => (d.VideoBitrateKbps + d.AudioBitrateKbps) / 1000.0) * 1.1;

        sb.AppendLine("# KAT relay - server setup");
        sb.AppendLine();
        sb.AppendLine("This folder is a complete restream server. One stream goes in, copies go out to every enabled destination.");
        sb.AppendLine();
        sb.AppendLine("## 1. Get a cheap server (any Ubuntu VPS works)");
        sb.AppendLine("- Hetzner CX22 (~EUR 4/mo, 20 TB traffic) or Oracle Cloud free tier are good picks.");
        sb.AppendLine($"- Pick a location with good bandwidth to your home AND to the platforms (Frankfurt/Ashgate EU, or Ashburn for US).");
        sb.AppendLine();
        sb.AppendLine("## 2. Install Docker on the server (once)");
        sb.AppendLine("```bash");
        sb.AppendLine("curl -fsSL https://get.docker.com | sh");
        sb.AppendLine("ufw allow OpenSSH && ufw allow 1935/tcp && ufw --force enable");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 3. Upload this folder to the server");
        sb.AppendLine("```bash");
        sb.AppendLine($"scp -r \"{Directory.GetCurrentDirectory()}\" root@{host}:/opt/kat-relay");
        sb.AppendLine("```");
        sb.AppendLine("Or use WinSCP (drag and drop, GUI).");
        sb.AppendLine();
        sb.AppendLine("## 4. Start it");
        sb.AppendLine("```bash");
        sb.AppendLine("cd /opt/kat-relay && docker compose up -d --build");
        sb.AppendLine("docker compose logs -f   # Ctrl+C to stop watching");
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
