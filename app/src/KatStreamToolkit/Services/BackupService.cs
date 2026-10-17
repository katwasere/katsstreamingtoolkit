using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KatStreamToolkit.Services;

// Scheduled encrypted backups of the toolkit's two files (config.json + the
// active secrets/keys file). One .katbak = "KATBAK1" + PBKDF2 salt + AES-GCM
// nonce + ciphertext of a zip holding both files; the passphrase is the only
// key, so a wrong one fails the authentication tag before anything is written
// on restore. S3-compatible stores are deliberately out: the folder of choice
// covers self-hosted sync (Nextcloud/Syncthing watch a folder just fine).
public static class BackupService
{
    private const string Magic = "KATBAK1";
    private const int SaltLen = 16, NonceLen = 12, KeyLen = 32, Iterations = 210_000, TagLen = 16;
    public const string Extension = ".katbak";
    public const int KeepBackups = 15;

    public sealed record BackupOutcome(string? Error, string? Path);
    public sealed record RestorePayload(string ConfigJson, string SecretsJson, string SecretsPath);

    // Reads the two files AS THEY ARE ON DISK (the live config object could be
    // mid-edit) and writes one encrypted archive. Returns the new file's path,
    // or an error string for the status line.
    public static BackupOutcome Backup(string folder, string secretsPath, string passphrase)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(passphrase))
                return new BackupOutcome("set a passphrase first - backups are encrypted with it", null);
            string configPath = ConfigStore.ConfigFilePath;
            if (!File.Exists(configPath)) return new BackupOutcome("nothing to back up yet (no config.json)", null);
            if (!File.Exists(secretsPath)) return new BackupOutcome("nothing to back up yet (no keys file)", null);

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    AddEntry(zip, "config.json", File.ReadAllBytes(configPath));
                    AddEntry(zip, "secrets.json", File.ReadAllBytes(secretsPath));
                    AddEntry(zip, "manifest.json",
                        JsonSerializer.SerializeToUtf8Bytes(new Manifest(DateTime.UtcNow, secretsPath)));
                }
                payload = ms.ToArray();
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
            byte[] cipher = new byte[payload.Length];
            byte[] tag = new byte[TagLen];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Encrypt(nonce, payload, cipher, tag);

            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"kat-backup-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
            using (var outStream = File.Create(path))
            {
                outStream.Write(Encoding.ASCII.GetBytes(Magic));
                outStream.Write(salt);
                outStream.Write(nonce);
                outStream.Write(cipher);
                outStream.Write(tag);
            }
            Prune(folder);
            return new BackupOutcome(null, path);
        }
        catch (Exception ex)
        {
            return new BackupOutcome("backup failed: " + ex.Message, null);
        }
    }

    // Verifies the passphrase against the GCM tag and returns the two files'
    // contents WITHOUT writing anything - the caller decides where they land.
    public static RestorePayload Open(string path, string passphrase)
    {
        try
        {
            byte[] all = File.ReadAllBytes(path);
            int header = Magic.Length + SaltLen + NonceLen;
            if (all.Length <= header + TagLen ||
                Encoding.ASCII.GetString(all, 0, Magic.Length) != Magic)
                throw new Exception("not a toolkit backup file");
            byte[] salt = all[Magic.Length..(Magic.Length + SaltLen)];
            byte[] nonce = all[(Magic.Length + SaltLen)..header];
            byte[] cipher = all[header..^TagLen];
            byte[] tag = all[^TagLen..];
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
            byte[] plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Decrypt(nonce, cipher, tag, plain);

            string configJson = "", secretsJson = "", secretsPath = "";
            using var ms = new MemoryStream(plain);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                switch (entry.Name)
                {
                    case "config.json": configJson = reader.ReadToEnd(); break;
                    case "secrets.json": secretsJson = reader.ReadToEnd(); break;
                    case "manifest.json":
                        secretsPath = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd())?.SecretsPath ?? "";
                        break;
                }
            }
            if (configJson.Length == 0 || secretsJson.Length == 0)
                throw new Exception("backup archive is incomplete");
            return new RestorePayload(configJson, secretsJson, secretsPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A wrong passphrase surfaces as an AES authentication failure here -
            // the only two causes the user can act on are named plainly.
            throw new Exception("could not open the backup - wrong passphrase, or the file is damaged", ex);
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes);
    }

    private static void Prune(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "*" + Extension)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            for (int i = KeepBackups; i < files.Count; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch
        {
            // Pruning is best-effort; the new backup is already on disk.
        }
    }

    private sealed record Manifest(DateTime CreatedUtc, string SecretsPath);
}
