using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatStreamToolkit.Chat;

namespace KatStreamToolkit.Services;

// Twitch OAuth (authorization code + PKCE). Opens the browser, catches the
// redirect on a one-shot loopback listener (fixed port 8770 - register exactly
// "http://localhost:8770/" as the app's redirect URL in the dev console),
// exchanges the code and validates the token. No client secret needed.
public static class TwitchAuthService
{
    public const int LoopbackPort = 8770;
    public const string RedirectUri = "http://localhost:8770/";

    // chat:read/edit for the IRC connection; the moderator scopes power
    // timeout/ban/delete/slow-mode from the overlay; channel:manage:broadcast
    // is for title/category sync later.
    public const string Scopes = "chat:read chat:edit " +
        "moderator:manage:banned_users moderator:manage:chat_messages " +
        "moderator:manage:chat_settings channel:manage:broadcast";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<TwitchAuthData> LoginAsync(string clientId, Action<string> log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new Exception("paste your Twitch application Client ID first (Chat Overlays tab)");

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, LoopbackPort);
        try
        {
            listener.Start();
        }
        catch (Exception)
        {
            throw new Exception(
                $"port {LoopbackPort} is busy - close whatever uses it and try again");
        }

        try
        {
            var url = "https://id.twitch.tv/oauth2/authorize" +
                      "?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                      $"&scope={Uri.EscapeDataString(Scopes)}" +
                      $"&state={Uri.EscapeDataString(state)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" +
                      "&force_verify=true";
            log("opening the browser for the Twitch login...");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            string? code = await WaitForCodeAsync(listener, state, ct);
            if (code == null)
                throw new Exception("no login response arrived (five minute timeout)");

            log("exchanging the login code for a token...");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
            };
            using (var content = new FormUrlEncodedContent(form))
            using (var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token", content, ct))
            {
                string body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var at))
                {
                    string error = root.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
                    throw new Exception($"token exchange failed: {error}");
                }
                string access = at.GetString() ?? "";
                string refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                return await ValidateAsync(clientId, access, refresh, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    // Twitch rotates refresh tokens on every use; callers must store the pair
    // the method returns, not keep the old refresh token.
    public static async Task<TwitchAuthData> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new Exception("no refresh token stored - log in again");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token", content, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at))
        {
            string error = root.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
            throw new Exception($"token refresh failed: {error}");
        }
        string access = at.GetString() ?? "";
        string refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? refreshToken : refreshToken;
        return await ValidateAsync(clientId, access, refresh, ct);
    }

    private static async Task<TwitchAuthData> ValidateAsync(string clientId, string accessToken, string refreshToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"token validation failed ({(int)resp.StatusCode})");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string login = root.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
        string userId = root.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        int expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int s) ? s : 0;
        return new TwitchAuthData(accessToken, refreshToken,
            DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(expiresIn, 60)), login, userId);
    }

    // One raw HTTP exchange on the loopback socket: HttpListener would need a
    // URL ACL for the port, a plain TcpListener does not.
    private static async Task<string?> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (!linked.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var sb = new StringBuilder();
                string request = "";
                while (sb.Length < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer, linked.Token);
                    if (read <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    int end = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        request = sb.ToString(0, end);
                        break;
                    }
                }
                // Answer the browser first so the tab does not hang.
                const string ok = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\n\r\n" +
                    "<html><body style='background:#16181d;color:#e6e8eb;font-family:sans-serif'>" +
                    "<h2>Login received</h2><p>You can close this tab and return to the toolkit.</p></body></html>";
                var bytes = Encoding.ASCII.GetBytes(ok);
                await stream.WriteAsync(bytes, linked.Token);

                var lines = request.Split('\n');
                if (lines.Length == 0) continue;
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) continue;
                var query = ParseQuery(new Uri("http://x" + parts[1]).Query);
                query.TryGetValue("state", out string? state);
                query.TryGetValue("error", out string? error);
                if (state != expectedState) continue; // not our redirect - keep waiting
                if (error != null)
                {
                    query.TryGetValue("error_description", out var desc);
                    throw new Exception($"Twitch denied the login: {error} ({desc})");
                }
                query.TryGetValue("code", out string? code);
                if (!string.IsNullOrEmpty(code))
                    return code;
            }
        }
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string val = eq < 0 ? "" : pair[(eq + 1)..];
            dict[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(val.Replace('+', ' '));
        }
        return dict;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
