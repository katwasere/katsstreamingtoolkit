using System.Net;
using System.Text.Json;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;

namespace KatStreamToolkit.Services;

// Poll/prediction state for the on-screen widget: polls the logged-in
// broadcaster's active Twitch poll (and prediction) through the Helix API and
// publishes a compact snapshot on every change. No login = clear status, no
// crash loop; the widget hides itself whenever there is nothing to show.
public sealed class PollService : IDisposable
{
    public sealed class PollRow
    {
        public required string Title { get; init; }
        public required string Count { get; init; }
        public required string Bar { get; init; }

        // Front-runner highlight (most votes / most points).
        public bool Leader { get; init; }
    }

    public sealed class PollState
    {
        public required string Kind { get; init; } // "POLL" or "PREDICTION"
        public required string Title { get; init; }
        public string? TimeLeft { get; init; }
        public string? Total { get; init; }
        public required List<PollRow> Rows { get; init; }
    }

    private readonly AppConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string _lastFingerprint = "";
    private string? _lastStatus;
    private int _testBusy;

    // BUG-69: bumped on every enable/disable cycle. A loop belonging to an
    // older generation (still finishing one in-flight fetch after a fast
    // disable/re-enable) must not publish stale state over the fresh loop's.
    private int _generation;

    // Cached broadcaster_type ("", "affiliate" or "partner") of the logged-in
    // account, keyed by user id. Twitch polls/predictions exist ONLY on
    // affiliate and partner channels - without this check every polls call
    // just 403s and the widget looks broken for reasons no error text names.
    private string? _broadcasterType;
    private string? _broadcasterTypeFor;

    // Raised on the poll loop thread; the subscriber marshals to the UI.
    // A null state means "hide the widget".
    public event Action<PollState?>? Update;
    public event Action<string>? Status;

    public PollService(AppConfig config) => _config = config;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _cts ??= new CancellationTokenSource();
            if (_loop == null || _loop.IsCompleted)
            {
                int gen = Interlocked.Increment(ref _generation);
                _loop = Task.Run(() => RunAsync(_cts!.Token, gen));
            }
            try
            {
                if (_wake.CurrentCount == 0) _wake.Release();
            }
            catch (SemaphoreFullException) { }
        }
        else
        {
            // Retire the current loop's publish rights immediately, so an
            // in-flight fetch cannot paint stale state after the disable.
            Interlocked.Increment(ref _generation);
            var cts = _cts;
            _cts = null;
            _loop = null;
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
            Publish(null, Volatile.Read(ref _generation));
            SetStatus("poll widget off");
        }
    }

    private void Nudge()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync(CancellationToken ct, int gen)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct, gen);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus(Short(ex.Message));
            }
            // First wait honors a nudge (enable/toggle), the steady state is
            // one refresh per 6s - plenty for vote counts, gentle on Helix.
            try
            {
                if (await _wake.WaitAsync(TimeSpan.FromSeconds(6), ct))
                    continue; // pulsed - refresh immediately
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct, int gen)
    {
        var auth = ChatAuthStore.Twitch;
        if (auth == null)
        {
            Publish(null, gen);
            SetStatus("log in to Twitch (Channels & account tab) to show your polls");
            return;
        }
        if (string.IsNullOrWhiteSpace(ModerationService.ClientId))
        {
            Publish(null, gen);
            SetStatus("paste the Twitch app Client ID (Channels & account tab) first");
            return;
        }

        string? broadcasterType = await BroadcasterTypeAsync(auth, ct);
        if (broadcasterType != null && !PollsAvailable(broadcasterType))
        {
            Publish(null, gen);
            SetStatus("Twitch polls exist only on affiliate and partner channels - this channel is neither, so the widget has nothing to fetch (that is also why the test poll 403s)");
            return;
        }

        var state = await FetchPollAsync(auth, ct)
                    ?? (_config.PollWidget.ShowPredictions
                        ? await FetchPredictionAsync(auth, ct)
                        : null);
        if (state == null)
        {
            Publish(null, gen);
            SetStatus("no active poll or prediction on your channel");
        }
        else
        {
            Publish(state, gen);
        }
    }

    private static bool PollsAvailable(string? broadcasterType) =>
        broadcasterType is "affiliate" or "partner";

    private async Task<string?> BroadcasterTypeAsync(TwitchAuthData auth, CancellationToken ct)
    {
        if (_broadcasterTypeFor == auth.UserId) return _broadcasterType;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ModerationService.Helix}/users?id={Uri.EscapeDataString(auth.UserId)}");
            using var resp = await ModerationService.SendAsync(req, auth, ct);
            if (resp is not { IsSuccessStatusCode: true }) return null; // unknown - let the real calls report
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return null;
            _broadcasterType = PollString(data[0], "broadcaster_type") ?? "";
            _broadcasterTypeFor = auth.UserId;
            return _broadcasterType;
        }
        catch
        {
            return null;
        }
    }

    private async Task<PollState?> FetchPollAsync(TwitchAuthData auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ModerationService.Helix}/polls?broadcaster_id={Uri.EscapeDataString(auth.UserId)}");
        using var resp = await ModerationService.SendAsync(req, auth, ct);
        if (resp == null)
        {
            SetStatus("no connection to Twitch");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                SetStatus("the Twitch login expired - log in again");
            else if (resp.StatusCode == HttpStatusCode.Forbidden)
                SetStatus("the login lacks poll permissions - log out and back in once");
            else
                SetStatus($"polls request failed ({(int)resp.StatusCode})");
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var poll in data.EnumerateArray())
        {
            if (PollString(poll, "status") != "ACTIVE") continue;
            string title = PollString(poll, "title") ?? "";

            var rows = new List<PollRow>();
            long total = 0, max = 0;
            var votes = new List<long>();
            var titles = new List<string>();
            if (poll.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    titles.Add(PollString(choice, "title") ?? "");
                    long v = choice.TryGetProperty("votes", out var vv) && vv.TryGetInt64(out long n) ? n : 0;
                    votes.Add(v);
                    total += v;
                    if (v > max) max = v;
                }
            }
            for (int i = 0; i < titles.Count; i++)
            {
                double ratio = max > 0 ? (double)votes[i] / max : 0;
                rows.Add(new PollRow
                {
                    Title = titles[i],
                    Count = $"{votes[i]} votes ({Percent(total, votes[i])})",
                    Bar = new string('█', (int)Math.Round(ratio * 22)),
                    Leader = max > 0 && votes[i] == max,
                });
            }

            return new PollState
            {
                Kind = "POLL",
                Title = title,
                TimeLeft = TimeLeft(PollString(poll, "ends_at"), "left"),
                Total = total == 1 ? "1 vote" : $"{total} votes",
                Rows = rows,
            };
        }
        return null;
    }

    private async Task<PollState?> FetchPredictionAsync(TwitchAuthData auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ModerationService.Helix}/predictions?broadcaster_id={Uri.EscapeDataString(auth.UserId)}");
        using var resp = await ModerationService.SendAsync(req, auth, ct);
        if (resp == null || !resp.IsSuccessStatusCode)
        {
            // Polls succeeded (or we would have returned earlier) - only the
            // prediction permission may be missing; say it once, quietly.
            if (resp is { StatusCode: HttpStatusCode.Forbidden })
                SetStatus("the login lacks prediction permissions - log out and back in once");
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var pred in data.EnumerateArray())
        {
            string status = PollString(pred, "status") ?? "";
            if (status is not ("ACTIVE" or "LOCKED")) continue;
            string title = PollString(pred, "title") ?? "";

            var rows = new List<PollRow>();
            long totalUsers = 0, totalPoints = 0, maxUsers = 0;
            var outcomeTitles = new List<string>();
            var users = new List<long>();
            var points = new List<long>();
            if (pred.TryGetProperty("outcomes", out var outcomes) && outcomes.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in outcomes.EnumerateArray())
                {
                    outcomeTitles.Add(PollString(o, "title") ?? "");
                    long u = o.TryGetProperty("users", out var uu) && uu.TryGetInt64(out long un) ? un : 0;
                    long p = o.TryGetProperty("channel_points", out var pp) && pp.TryGetInt64(out long pn) ? pn : 0;
                    users.Add(u);
                    points.Add(p);
                    totalUsers += u;
                    totalPoints += p;
                    if (u > maxUsers) maxUsers = u;
                }
            }
            bool locked = status == "LOCKED";
            for (int i = 0; i < outcomeTitles.Count; i++)
            {
                double ratio = maxUsers > 0 ? (double)users[i] / maxUsers : 0;
                rows.Add(new PollRow
                {
                    Title = outcomeTitles[i],
                    Count = $"{users[i]} - {points[i]:N0} pts",
                    Bar = new string('█', (int)Math.Round(ratio * 22)),
                    Leader = maxUsers > 0 && users[i] == maxUsers,
                });
            }

            return new PollState
            {
                Kind = "PREDICTION",
                Title = title,
                TimeLeft = locked ? "locked" : TimeLeft(PollString(pred, "locks_at"), "to lock"),
                Total = $"{totalUsers} predictors - {totalPoints:N0} points",
                Rows = rows,
            };
        }
        return null;
    }

    // Creates a real 30-second poll on the broadcaster's own channel so the
    // widget can be verified end to end without building one in the dashboard.
    public async Task CreateTestPollAsync()
    {
        if (Interlocked.CompareExchange(ref _testBusy, 1, 0) != 0) return;
        try
        {
            var auth = ChatAuthStore.Twitch;
            if (auth == null)
            {
                SetStatus("log in to Twitch first (Channels & account tab)");
                return;
            }
            string? broadcasterType = await BroadcasterTypeAsync(auth, CancellationToken.None);
            if (broadcasterType != null && !PollsAvailable(broadcasterType))
            {
                SetStatus("Twitch polls exist only on affiliate and partner channels - this channel is neither, so no test poll can be created");
                return;
            }
            var body = JsonSerializer.Serialize(new
            {
                broadcaster_id = auth.UserId,
                title = "Toolkit test poll: cats or dogs?",
                choices = new[] { new { title = "cats" }, new { title = "dogs" } },
                duration = 30,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{ModerationService.Helix}/polls")
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            using var resp = await ModerationService.SendAsync(req, auth, CancellationToken.None);
            if (resp is { StatusCode: HttpStatusCode.Forbidden })
            {
                // DescribeAsync's 403 text is moderation-specific ("not a
                // moderator") and wrong here. On the broadcaster's own channel
                // a 403 is almost always the scope check - but read Twitch's
                // own message when it says something else.
                string detail = await BodyMessageAsync(resp);
                if (detail.Length == 0 || detail.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                    SetStatus("create test poll: the login lacks poll permissions - log out and back in once (Channels & account tab)");
                else
                    SetStatus($"create test poll: {detail}");
                return;
            }
            string? error = await ModerationService.DescribeAsync(resp, "create test poll");
            SetStatus(error == null
                ? "test poll created - the widget shows it for 30 seconds"
                : error.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    ? error + " (a login made before this feature needs one re-login)"
                    : error);
            Nudge();
        }
        catch (Exception ex)
        {
            SetStatus(Short(ex.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _testBusy, 0);
        }
    }

    // ---------- helpers ----------

    private void Publish(PollState? state, int gen)
    {
        // A loop from an older generation (fast disable/re-enable) must not
        // overwrite the fresh loop's state with a stale in-flight fetch.
        if (gen != Volatile.Read(ref _generation)) return;
        string fingerprint = Fingerprint(state);
        if (fingerprint != _lastFingerprint)
        {
            _lastFingerprint = fingerprint;
            Update?.Invoke(state);
        }
    }

    private static string Fingerprint(PollState? state)
    {
        if (state == null) return "hidden";
        var sb = new System.Text.StringBuilder();
        sb.Append(state.Kind).Append('|').Append(state.Title).Append('|').Append(state.TimeLeft)
          .Append('|').Append(state.Total);
        foreach (var row in state.Rows)
            sb.Append('|').Append(row.Title).Append('/').Append(row.Count).Append('/').Append(row.Bar.Length);
        return sb.ToString();
    }

    private void SetStatus(string message)
    {
        if (message == _lastStatus) return;
        _lastStatus = message;
        Status?.Invoke(message);
    }

    private static string? PollString(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(name, out var el) &&
        el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    // Twitch error bodies are JSON with a "message" field; fall back to the
    // raw body (trimmed) so nothing is ever reported as empty without reason.
    // BUG-68: this used to be a sync-over-async read - a stalled connection
    // froze the UI thread for the socket timeout on the rare non-scope 403 path.
    private static async Task<string> BodyMessageAsync(HttpResponseMessage resp)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.GetString() ?? "";
            return body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            return "";
        }
    }

    private static string Percent(long total, long part) =>
        total <= 0 ? "0%" : $"{(int)Math.Round(part * 100.0 / total)}%";

    private static string? TimeLeft(string? isoEndsAt, string suffix)
    {
        if (isoEndsAt == null ||
            !DateTime.TryParse(isoEndsAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ends))
            return null;
        TimeSpan left = ends.ToUniversalTime() - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return "ended";
        return left >= TimeSpan.FromHours(1)
            ? $"{(int)left.TotalHours}:{left.Minutes:D2}:{left.Seconds:D2} {suffix}"
            : $"{left.Minutes}:{left.Seconds:D2} {suffix}";
    }

    private static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 90 ? s : s[..90] + "...";
    }

    public void Dispose()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
