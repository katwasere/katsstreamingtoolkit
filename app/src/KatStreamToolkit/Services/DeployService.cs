using System.IO;
using KatStreamToolkit.Models;
using Renci.SshNet;

namespace KatStreamToolkit.Services;

public sealed record DeployTarget(string Host, string User, bool UseKey, string Password, string KeyPath, string RemotePath);

// SSH/SFTP operations for the Deploy tab. Everything runs on a background thread;
// callers get progress through a log callback.
public static class DeployService
{
    private static SshClient ConnectSsh(DeployTarget t)
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

        using (var sftp = ConnectSftp(t))
        {
            EnsureRemoteDir(sftp, client, t.RemotePath, log);
            foreach (var file in Directory.GetFiles(temp))
            {
                using var fs = File.OpenRead(file);
                sftp.UploadFile(fs, $"{t.RemotePath}/{Path.GetFileName(file)}", true);
                log("  uploaded " + Path.GetFileName(file));
            }
        }
        log("upload complete");

        var (code, output) = Run(client, $"cd '{t.RemotePath}' && sudo docker compose up -d --build 2>&1", 600);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            log("  " + line);
        if (code != 0)
            throw new Exception($"deploy failed (exit code {code}) - see log above");
        log("deploy complete - relay is running");
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
