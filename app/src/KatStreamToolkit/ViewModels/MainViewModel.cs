using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using KatStreamToolkit.Chat;
using KatStreamToolkit.Models;
using KatStreamToolkit.Services;
using Microsoft.Win32;

namespace KatStreamToolkit.ViewModels;

public class MainViewModel : ObservableBase
{
    private string _nginxPreview = "";
    private OverlayConfig? _selectedOverlay;
    private DestinationConfig? _selectedDestination;
    private CommandConfig? _selectedCommand;
    private SecretsData _secrets = new();
    private bool _showSecretsInPreview;
    private KatStreamToolkit.Chat.TwitchAuthData? _twitchAuth;
    private CommandRunner? _commandRunner;
    private int _twitchBusy;

    public event Action<OverlayConfig>? OverlayAdded;
    public event Action<OverlayConfig>? OverlayRemoved;

    public AppConfig Config { get; }
    public ObservableCollection<DestinationConfig> Destinations { get; } = new();
    public ObservableCollection<OverlayConfig> Overlays { get; } = new();
    public ObservableCollection<CommandConfig> Commands { get; } = new();

    public static Platform[] AllPlatforms { get; } = Enum.GetValues<Platform>();
    public static Orientation[] AllOrientations { get; } = Enum.GetValues<Orientation>();
    public static PortraitStyle[] AllPortraitStyles { get; } = Enum.GetValues<PortraitStyle>();
    public static ChatMode[] AllChatModes { get; } = Enum.GetValues<ChatMode>();
    public static string[] AllEncoderPresets { get; } = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" };

    public OverlayConfig? SelectedOverlay { get => _selectedOverlay; set => Set(ref _selectedOverlay, value); }
    public CommandConfig? SelectedCommand { get => _selectedCommand; set => Set(ref _selectedCommand, value); }
    public DestinationConfig? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (Set(ref _selectedDestination, value))
            {
                // Recompute immediately: the change notification alone would make
                // the binding re-read the previous card's preview text.
                SelectedCommandPreview = value is null
                    ? ""
                    : RelayConfigGenerator.DescribeDestination(Config, value, ShowSecretsInPreview);
                Raise(nameof(SelectedCommandPreview));
            }
        }
    }

    public string NginxPreview { get => _nginxPreview; private set => Set(ref _nginxPreview, value); }

    // Non-empty when the toolkit's generated relay config no longer matches
    // what the last successful deploy shipped (rotated keys, edited URLs,
    // changed destinations...). Keys and destinations only reach the VPS on
    // redeploy - this is the local, no-round-trip version of the server-side
    // OUTDATED check.
    public string ServerConfigOutdatedText { get; private set; } = "";

    // The exact nginx.conf lines the selected destination contributes (Output Studio preview).
    public string SelectedCommandPreview { get; private set; } = "";

    public string BandwidthSummary { get; private set; } = "";

    public bool ShowSecretsInPreview
    {
        get => _showSecretsInPreview;
        set { if (Set(ref _showSecretsInPreview, value)) Refresh(); }
    }

    public string EffectiveKeysPath => string.IsNullOrWhiteSpace(Config.KeysFilePath)
        ? SecretsStore.DefaultPath
        : Config.KeysFilePath;

    public string KeysFilePathDisplay => EffectiveKeysPath;

    public string SshPassword
    {
        get => _secrets.SshPassword;
        set
        {
            if (_secrets.SshPassword == value) return;
            _secrets.SshPassword = value;
            ScheduleSave();
        }
    }

    // Euler Stream API key (TikTok chat). Secret like everything else here: it
    // lives in secrets.json only, never in config.json.
    public string EulerApiKey
    {
        get => _secrets.EulerApiKey;
        set
        {
            if (_secrets.EulerApiKey == value) return;
            _secrets.EulerApiKey = value;
            SyncEulerKey();
            ScheduleSave();
        }
    }

    private void SyncEulerKey() =>
        TikTokChatClient.ApiKey = string.IsNullOrWhiteSpace(_secrets.EulerApiKey)
            ? null
            : _secrets.EulerApiKey.Trim();

    // ---------- Twitch account (OAuth: chat send, !commands, moderation) ----------

    public string TwitchAccountText => _twitchAuth != null
        ? $"logged in as {_twitchAuth.Login}"
        : "not logged in - chat is read-only";

    public string TwitchLoginStatus { get; private set; } = "";

    public string CommandActivityText { get; private set; } = "no commands triggered yet";

    public ICommand LoginTwitchCommand { get; }
    public ICommand LogoutTwitchCommand { get; }
    public ICommand AddCommandCommand { get; }
    public ICommand RemoveCommandCommand { get; }

    private void SetTwitchLoginStatus(string text)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            TwitchLoginStatus = text;
            Raise(nameof(TwitchLoginStatus));
        });
    }

    private void SetCommandActivity(string text)
    {
        CommandActivityText = text;
        Raise(nameof(CommandActivityText));
    }

    private KatStreamToolkit.Chat.TwitchAuthData? BuildAuthFromSecrets() =>
        string.IsNullOrWhiteSpace(_secrets.TwitchAccessToken)
            ? null
            : new KatStreamToolkit.Chat.TwitchAuthData(
                _secrets.TwitchAccessToken, _secrets.TwitchRefreshToken,
                _secrets.TwitchTokenExpiresUtc, _secrets.TwitchLogin, _secrets.TwitchUserId);

    // One function that mirrors secrets -> ChatAuthStore; called after login,
    // logout, load and every refresh. Chat clients pick the auth up when their
    // sources restart - so unchanged auth (e.g. typing in the Client ID field)
    // must NOT repush, or every keystroke would reconnect every overlay.
    private void SyncTwitchAuth()
    {
        ModerationService.ClientId = string.IsNullOrWhiteSpace(Config.TwitchClientId)
            ? null
            : Config.TwitchClientId.Trim();
        var auth = BuildAuthFromSecrets();
        bool unchanged = (auth == null && _twitchAuth == null) ||
                         (auth != null && _twitchAuth != null &&
                          auth.AccessToken == _twitchAuth.AccessToken);
        _twitchAuth = auth;
        if (!unchanged)
        {
            KatStreamToolkit.Chat.ChatAuthStore.SetTwitch(auth);
            Raise(nameof(TwitchAccountText));
        }
        if (auth != null &&
            auth.ExpiresUtc < DateTime.UtcNow + TimeSpan.FromMinutes(30) &&
            !string.IsNullOrWhiteSpace(auth.RefreshToken))
            _ = RefreshTwitchTokenAsync();
    }

    private async Task RefreshTwitchTokenAsync()
    {
        if (Interlocked.CompareExchange(ref _twitchBusy, 1, 0) != 0) return;
        try
        {
            var auth = await TwitchAuthService.RefreshAsync(
                Config.TwitchClientId, _secrets.TwitchRefreshToken);
            StoreTwitchAuth(auth);
            SetTwitchLoginStatus($"session refreshed ({auth.Login})");
        }
        catch (Exception ex)
        {
            SetTwitchLoginStatus($"token refresh failed ({ex.Message}) - log in again");
        }
        finally
        {
            Interlocked.Exchange(ref _twitchBusy, 0);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void StoreTwitchAuth(KatStreamToolkit.Chat.TwitchAuthData auth)
    {
        _secrets.TwitchAccessToken = auth.AccessToken;
        _secrets.TwitchRefreshToken = auth.RefreshToken;
        _secrets.TwitchTokenExpiresUtc = auth.ExpiresUtc;
        _secrets.TwitchLogin = auth.Login;
        _secrets.TwitchUserId = auth.UserId;
        Save();
        SyncTwitchAuth();
    }

    private void LoginTwitch()
    {
        if (Interlocked.CompareExchange(ref _twitchBusy, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            try
            {
                SetTwitchLoginStatus("waiting for the browser login...");
                var auth = await TwitchAuthService.LoginAsync(Config.TwitchClientId, SetTwitchLoginStatus);
                StoreTwitchAuth(auth);
                SetTwitchLoginStatus("logged in - overlays reconnect with your account");
            }
            catch (Exception ex)
            {
                SetTwitchLoginStatus($"login failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _twitchBusy, 0);
                CommandManager.InvalidateRequerySuggested();
            }
        });
    }

    private void LogoutTwitch()
    {
        _secrets.TwitchAccessToken = "";
        _secrets.TwitchRefreshToken = "";
        _secrets.TwitchTokenExpiresUtc = default;
        _secrets.TwitchLogin = "";
        _secrets.TwitchUserId = "";
        Save();
        SyncTwitchAuth();
        SetTwitchLoginStatus("logged out");
    }

    private async Task SwitchObsSceneAsync(string scene)
    {
        if (_obs is not { IsConnected: true })
            throw new Exception("OBS control is off or not connected (Relay & Routing tab)");
        await _obs.SetCurrentProgramScene(scene);
    }

    private void AddCommand()
    {
        var cmd = new CommandConfig { Name = $"command{Commands.Count + 1}" };
        Commands.Add(cmd);
        SelectedCommand = cmd;
    }

    private void OnOverlayChannelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayConfig.TwitchChannel)
            or nameof(OverlayConfig.KickChannel)
            or nameof(OverlayConfig.KickChatroomId)
            or nameof(OverlayConfig.YouTubeUrl))
            _commandRunner?.SyncSources();
    }

    // Called when the main window closes: releases the command runner's chat
    // entries (the overlays release their own on window close).
    public void Shutdown() => _commandRunner?.Dispose();

    // ---------- OBS control (obs-websocket) ----------

    private ObsWebSocketClient? _obs;

    public string ObsPassword
    {
        get => _secrets.ObsPassword;
        set
        {
            if (_secrets.ObsPassword == value) return;
            _secrets.ObsPassword = value;
            RecreateObsClient();
            ScheduleSave();
        }
    }

    public string ObsConnectionText { get; private set; } = "obs control off";

    public VerifyLight ObsConnectionLight { get; private set; } = VerifyLight.Unknown;

    public string GoLiveStatusText { get; private set; } = "not started";

    private void EnsureObsClient()
    {
        if (Config.ObsEnabled && _obs == null)
        {
            RecreateObsClient();
        }
        else if (!Config.ObsEnabled && _obs != null)
        {
            _obs.Dispose();
            _obs = null;
            SetObsStatus("obs control off", VerifyLight.Unknown);
        }
    }

    private void RecreateObsClient()
    {
        _obs?.Dispose();
        _obs = null;
        if (!Config.ObsEnabled) return;
        _obs = new ObsWebSocketClient(Config.ObsWebSocketUrl, ObsPassword);
        _obs.StatusChanged += s => Application.Current?.Dispatcher.BeginInvoke(() =>
            SetObsStatus("obs: " + s,
                s == "connected" ? VerifyLight.Ok
                : s.StartsWith("wrong password") ? VerifyLight.Error
                : VerifyLight.Idle));
        _obs.Start();
        SetObsStatus("obs: connecting...", VerifyLight.Idle);
    }

    private void SetObsStatus(string text, VerifyLight light)
    {
        ObsConnectionText = text;
        ObsConnectionLight = light;
        Raise(nameof(ObsConnectionText));
        Raise(nameof(ObsConnectionLight));
    }

    private void SetGoLiveStatus(string text)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            GoLiveStatusText = text;
            Raise(nameof(GoLiveStatusText));
        });
    }

    private readonly StringBuilder _deployLog = new();
    private int _statusBusy;
    private int _monitorBusy;
    private bool _isRunning;

    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

    public string DeployLog => _deployLog.ToString();

    public string RelayStatusText { get => _relayStatusText; private set => Set(ref _relayStatusText, value); }
    private string _relayStatusText = "not checked yet";

    public bool AutoStatus { get => _autoStatus; set => Set(ref _autoStatus, value); }
    private bool _autoStatus;

    public VerifyLight ObsLight { get; private set; } = VerifyLight.Unknown;
    public string ObsLightText => ObsLight switch
    {
        VerifyLight.Ok => "sending",
        VerifyLight.Idle => "not sending",
        VerifyLight.Error => "error",
        _ => "not connected",
    };

    public VerifyLight RelayLight { get; private set; } = VerifyLight.Unknown;
    public string RelayLightText => RelayLight switch
    {
        VerifyLight.Ok => "alive",
        VerifyLight.Error => "not responding",
        _ => "not connected",
    };

    private void ApplyHealth(DeployTarget target, RelayHealthSnapshot snap)
    {
        bool reachable = snap.Reachable;
        bool receiving = snap.Receiving;

        RelayLight = reachable ? VerifyLight.Ok : VerifyLight.Error;
        ObsLight = !reachable ? VerifyLight.Unknown : receiving ? VerifyLight.Ok : VerifyLight.Idle;
        RelayStatusText = !reachable
            ? "relay not responding - deploy the bundle first"
            : receiving ? "receiving your stream" : "relay running, waiting for OBS";
        Raise(nameof(ObsLight));
        Raise(nameof(ObsLightText));
        Raise(nameof(RelayLight));
        Raise(nameof(RelayLightText));
        Raise(nameof(RelayStatusText));

        // A deploy/test restarts containers and drops publishers by design -
        // track state but alarm over nothing.
        if (IsRunning)
        {
            _wasReachable = reachable;
            _wasPublishing = receiving;
            return;
        }

        // Relay down, debounced over two ticks so a single SSH hiccup stays silent.
        if (!reachable)
        {
            _unreachableTicks++;
            if (_unreachableTicks >= 2)
                RaiseAlarm(new WatchdogAlarm("relay",
                    "RELAY DOWN - the relay stopped answering health checks. Click 'Restart relay', or redeploy from the Deploy tab if the container stays gone.", null));
        }
        else
        {
            _unreachableTicks = 0;
            ClearAlarm("relay");
        }

        // Publisher transition: the stream was arriving and now it is not.
        if (reachable && _wasPublishing == true && !receiving)
            RaiseAlarm(new WatchdogAlarm("obs",
                "OBS FEED DROPPED - the relay was receiving your stream and now sees no publisher. OBS usually reconnects by itself; if this repeats, check OBS and the server's TCP port 1935.", null));
        else if (receiving)
            ClearAlarm("obs");

        foreach (var d in Destinations)
        {
            if (!reachable)
            {
                d.PushLight = VerifyLight.Unknown;
                ClearAlarm("enc:" + d.Id);
                ClearAlarm("push:" + d.Id);
                continue;
            }

            bool routed = d.Enabled
                          && (d.Orientation == Orientation.Portrait || d.PortraitStyle == PortraitStyle.Custom)
                          && !string.IsNullOrWhiteSpace(d.IngestUrl);

            if (!receiving)
            {
                // Nothing publishing: no per-destination truth exists.
                d.PushLight = d.Enabled ? VerifyLight.Idle : VerifyLight.Unknown;
                ClearAlarm("enc:" + d.Id);
                ClearAlarm("push:" + d.Id);
                _encoderDownTicks.Remove(d.Id);
                _encoderNoFrameTicks.Remove(d.Id);
                _pushErrorTicks.Remove(d.Id);
                continue;
            }

            if (routed)
            {
                bool running = snap.Encoders.Contains(d.Id);
                // A MISSING age is not "0s old": the old TryGetValue default read
                // as brand-new, marked the encoder healthy instantly and made the
                // frozen condition unreachable whenever the age probe yielded
                // nothing (BUG-35).
                bool hasAge = snap.SnapshotAges.TryGetValue(d.Id, out int age);
                if (running && hasAge && age >= 0 && age <= 30)
                {
                    _encoderWasHealthy[d.Id] = true;
                    _encoderNoFrameTicks.Remove(d.Id);
                }

                if (!running)
                {
                    int ticks = _encoderDownTicks.TryGetValue(d.Id, out int seen) ? seen + 1 : 1;
                    _encoderDownTicks[d.Id] = ticks;
                    _encoderNoFrameTicks.Remove(d.Id);
                    if (ticks >= 2)
                    {
                        string? reason = DeployService.FindLogReason(snap.LogTail, Config, d);
                        RaiseAlarm(new WatchdogAlarm("enc:" + d.Id,
                            $"'{d.Name}' ENCODER IS DOWN - your stream is reaching the relay, but this output's ffmpeg is not running (it keeps dying)."
                            + (reason != null ? " Relay log: " + reason : ""), d.Id));
                        d.PushLight = VerifyLight.Error;
                    }
                    else
                    {
                        d.PushLight = VerifyLight.Idle;
                    }
                    continue;
                }
                _encoderDownTicks.Remove(d.Id);

                // Frozen: fresh frames before, none now. A stale FILE is
                // unambiguous (>60s old); a missing/unreadable age needs two
                // ticks so an encoder restart gap (kill -> nginx respawn ->
                // first frame) stays silent.
                bool wasHealthy = _encoderWasHealthy.TryGetValue(d.Id, out bool wh) && wh;
                bool frozen;
                if (hasAge)
                {
                    frozen = wasHealthy && age > 60;
                }
                else
                {
                    int noFrame = _encoderNoFrameTicks.TryGetValue(d.Id, out int nft) ? nft + 1 : 1;
                    _encoderNoFrameTicks[d.Id] = noFrame;
                    frozen = wasHealthy && noFrame >= 2;
                }
                if (frozen)
                {
                    RaiseAlarm(new WatchdogAlarm("enc:" + d.Id,
                        $"'{d.Name}' ENCODER FROZEN - alive but no new frames"
                        + (hasAge ? $" for ~{age}s." : " (its snapshot is gone or unreadable)."), d.Id) { CanRestart = true });
                    d.PushLight = VerifyLight.Error;
                    TryAutoRestartEncoder(d);
                    continue;
                }

                ClearAlarm("enc:" + d.Id);
                d.PushLight = VerifyLight.Ok;
            }
            else if (d.Enabled)
            {
                // Landscape push: nginx reconnects on its own, so the only
                // failure signal is repeated relay-log errors naming the host.
                string? host = DeployService.IngestHost(d.IngestUrl);
                bool erroring = host != null && DeployService.LogHasHostError(snap.LogTail, host);
                if (erroring)
                {
                    int ticks = _pushErrorTicks.TryGetValue(d.Id, out int pe) ? pe + 1 : 1;
                    _pushErrorTicks[d.Id] = ticks;
                    if (ticks >= 2)
                    {
                        string? reason = DeployService.FindLogReason(snap.LogTail, Config, d);
                        RaiseAlarm(new WatchdogAlarm("push:" + d.Id,
                            $"'{d.Name}' PUSH FAILING - the relay cannot push to {host}."
                            + (reason != null ? " Relay log: " + reason : ""), d.Id));
                        d.PushLight = VerifyLight.Error;
                    }
                }
                else
                {
                    _pushErrorTicks.Remove(d.Id);
                    ClearAlarm("push:" + d.Id);
                    d.PushLight = VerifyLight.Ok;
                }
            }
            else
            {
                d.PushLight = VerifyLight.Unknown;
            }
        }

        _wasReachable = reachable;
        _wasPublishing = receiving;
    }

    // ---------- watchdog state ----------
    private bool? _wasReachable;
    private bool? _wasPublishing;
    private int _unreachableTicks;
    private readonly Dictionary<Guid, int> _encoderDownTicks = new();
    private readonly Dictionary<Guid, bool> _encoderWasHealthy = new();
    private readonly Dictionary<Guid, int> _encoderNoFrameTicks = new();
    private readonly Dictionary<Guid, int> _pushErrorTicks = new();
    private readonly Dictionary<Guid, DateTime> _lastAutoRestart = new();

    public ObservableCollection<WatchdogAlarm> Alarms { get; } = new();

    public bool HasAlarms => Alarms.Count > 0;

    private void RaiseAlarm(WatchdogAlarm alarm)
    {
        var existing = Alarms.FirstOrDefault(a => a.Id == alarm.Id);
        if (existing != null)
        {
            // Update in place without re-triggering the alarm sound.
            if (existing.Text != alarm.Text) existing.Text = alarm.Text;
            return;
        }
        Alarms.Add(alarm);
        Raise(nameof(HasAlarms));
        if (Config.AlarmSound)
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    private void ClearAlarm(string id)
    {
        var existing = Alarms.FirstOrDefault(a => a.Id == id);
        if (existing == null) return;
        Alarms.Remove(existing);
        Raise(nameof(HasAlarms));
    }

    private void ClearAllAlarms()
    {
        Alarms.Clear();
        Raise(nameof(HasAlarms));
    }

    private void TryAutoRestartEncoder(DestinationConfig d)
    {
        if (!Config.WatchdogAutoRestart) return;
        if (_lastAutoRestart.TryGetValue(d.Id, out var last) &&
            DateTime.UtcNow - last < TimeSpan.FromSeconds(90)) return;
        _lastAutoRestart[d.Id] = DateTime.UtcNow;
        Log($"[watchdog] '{d.Name}' encoder frozen - killing it so nginx respawns a fresh one");
        RestartEncoderInner(d);
    }

    private void RestartEncoderInner(DestinationConfig d)
    {
        try
        {
            var target = BuildTarget();
            string command =
                $"sh -c 'docker exec kat-relay pkill -f kat-preview-{d.Id:N}' || sudo -n docker exec kat-relay pkill -f kat-preview-{d.Id:N}";
            Task.Run(() =>
            {
                try
                {
                    RelayPreviewService.RunOnServer(target, command, 30);
                    Log($"[watchdog] killed '{d.Name}' encoder - nginx respawns it automatically");
                }
                catch (Exception ex)
                {
                    Log("[watchdog] could not restart the encoder: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log("[watchdog] could not restart the encoder: " + ex.Message);
        }
    }

    private void RestartRelay(Action<string> log)
    {
        log("restarting the relay container...");
        string output = RelayPreviewService.RunOnServer(BuildTarget(),
            "sh -c 'docker restart kat-relay' || sudo -n docker restart kat-relay", 90);
        log("docker restart kat-relay: " + (string.IsNullOrWhiteSpace(output) ? "done" : output));
        log("the relay is booting - OBS reconnects by itself and every encoder restarts with it");
    }

    // ---------- go-live orchestration ----------

    private async Task GoLiveFlowAsync(Action<string> log)
    {
        SetGoLiveStatus("checking the relay...");
        var target = BuildTarget();
        log("[go-live] checking the relay...");

        var (reachable, receiving) = DeployService.FetchSnapshot(target);
        if (!reachable)
            throw new Exception("the relay is not responding - deploy the bundle first (Deploy tab)");
        log(receiving
            ? "[go-live] the relay is already receiving a stream"
            : "[go-live] relay is up");

        if (!Destinations.Any(d => d.Enabled))
            log("[go-live] note: no destinations are enabled - the stream will arrive but go nowhere");

        var obs = _obs;
        if (Config.ObsEnabled)
        {
            SetGoLiveStatus("talking to OBS...");
            if (obs is not { IsConnected: true })
                throw new Exception("OBS control is on but not connected (is OBS running with obs-websocket enabled, and the port/password right?)");

            bool active = await obs.GetStreamingStatus();
            if (active)
            {
                log("[go-live] OBS is already streaming");
            }
            else
            {
                log("[go-live] starting OBS streaming...");
                await obs.StartStream();
                log("[go-live] OBS started");
            }
        }
        else
        {
            log("[go-live] OBS control is off - start streaming in OBS yourself; this only verifies the relay");
        }

        SetGoLiveStatus("waiting for your stream to arrive...");
        log("[go-live] waiting for the relay to see your stream...");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var (_, rec) = DeployService.FetchSnapshot(target);
            if (rec)
            {
                log("[go-live] LIVE - the relay is receiving your stream and pushing every enabled destination. The watchdog and the destination lights take it from here.");
                SetGoLiveStatus("LIVE");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        throw new Exception(
            "the relay still sees no stream after 45s - in OBS set Server rtmp://" + target.Host +
            "/live and Key = your stream name, then try again");
    }

    private async Task EndStreamFlowAsync(Action<string> log)
    {
        SetGoLiveStatus("ending the stream...");
        log("[go-live] ending the stream...");
        var target = BuildTarget();

        if (Config.ObsEnabled)
        {
            var obs = _obs;
            if (obs is not { IsConnected: true })
                throw new Exception("OBS control is on but not connected (is OBS running?)");
            await obs.StopStream();
            log("[go-live] OBS stopped streaming");
        }
        else
        {
            log("[go-live] OBS control is off - stop streaming in OBS yourself");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var (reachable, receiving) = DeployService.FetchSnapshot(target);
            if (reachable && !receiving)
            {
                log("[go-live] done - the relay no longer sees a publisher. Every output stopped with it.");
                SetGoLiveStatus("ended");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        log("[go-live] note: the relay still shows a publisher - OBS may reconnect on its own (drop_idle_publisher clears it within 30s).");
        SetGoLiveStatus("ended (relay slow to notice)");
    }

    private void MonitorTick()
    {
        if (Interlocked.CompareExchange(ref _monitorBusy, 1, 0) != 0) return;
        DeployTarget target;
        try
        {
            target = BuildTarget();
        }
        catch
        {
            RelayLight = VerifyLight.Unknown;
            ObsLight = VerifyLight.Unknown;
            RelayStatusText = "not checked yet";
            foreach (var d in Destinations) d.PushLight = VerifyLight.Unknown;
            Raise(nameof(ObsLight));
            Raise(nameof(ObsLightText));
            Raise(nameof(RelayLight));
            Raise(nameof(RelayLightText));
            Raise(nameof(RelayStatusText));
            // No target = no watchdog: state resets so appearing later does not
            // look like a "relay down"/"OBS dropped" transition.
            _unreachableTicks = 0;
            _wasReachable = null;
            _wasPublishing = null;
            ClearAllAlarms();
            Interlocked.Exchange(ref _monitorBusy, 0);
            return;
        }
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            try
            {
                return DeployService.FetchHealthSnapshot(target, Config);
            }
            catch
            {
                return RelayHealthSnapshot.Down;
            }
        }).ContinueWith(t =>
        {
            ApplyHealth(target, t.Result);
            Interlocked.Exchange(ref _monitorBusy, 0);
        }, ui);
    }

    private int _liveCheckBusy;

    // Per-destination live checks: query each platform for "actually live" and
    // light the destination card's second lamp. The relay cannot see what happens
    // after its push, so a green push light next to a dark live light means the
    // server is sending but the platform is not showing it.
    private void LiveCheckTick()
    {
        if (Interlocked.CompareExchange(ref _liveCheckBusy, 1, 0) != 0) return;
        var jobs = new List<(DestinationConfig Dest, Platform Platform, string Handle)>();
        foreach (var d in Destinations)
        {
            if (!d.Enabled || d.Platform == Platform.Custom)
            {
                d.LiveLight = VerifyLight.Unknown;
                continue;
            }
            var handle = ResolveHandle(d);
            if (handle == null)
            {
                d.LiveLight = VerifyLight.Unknown;
                continue;
            }
            jobs.Add((d, d.Platform, handle));
        }
        if (jobs.Count == 0)
        {
            Interlocked.Exchange(ref _liveCheckBusy, 0);
            return;
        }
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(async () =>
        {
            var results = new List<(DestinationConfig Dest, LiveState State)>();
            // Sequential on purpose: four platforms polled in parallel every 30s
            // invites rate limiting; the 25s cap keeps a slow platform from
            // stalling the next tick.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            foreach (var (dest, platform, handle) in jobs)
            {
                try
                {
                    results.Add((dest, await LiveCheckService.CheckAsync(platform, handle, cts.Token)));
                }
                catch
                {
                    results.Add((dest, LiveState.Unknown));
                }
            }
            return results;
        }).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                foreach (var (dest, state) in t.Result)
                    dest.LiveLight = state switch
                    {
                        LiveState.Live => VerifyLight.Ok,
                        LiveState.Offline => VerifyLight.Idle,
                        _ => VerifyLight.Unknown,
                    };
            Interlocked.Exchange(ref _liveCheckBusy, 0);
        }, ui);
    }

    private string? ResolveHandle(DestinationConfig d)
    {
        var mc = Config.MyChannels;
        var specific = string.IsNullOrWhiteSpace(d.ChannelHandle) ? null : d.ChannelHandle.Trim();
        return d.Platform switch
        {
            Platform.Twitch => specific ?? Blank(mc.TwitchChannel),
            Platform.Kick => specific ?? Blank(mc.KickChannel),
            Platform.YouTube => specific ?? Blank(mc.YouTubeUrl),
            Platform.TikTok => specific ?? Blank(mc.TikTokHandle),
            _ => null,
        };

        static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public ICommand AddDestinationCommand { get; }
    public ICommand RemoveDestinationCommand { get; }
    public ICommand MoveDestinationLeftCommand { get; }
    public ICommand MoveDestinationRightCommand { get; }
    public ICommand AddOverlayCommand { get; }
    public ICommand RemoveOverlayCommand { get; }
    public ICommand ExportServerCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenKeysFileCommand { get; }
    public ICommand MoveKeysFileCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand DeployCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand RestartRelayCommand { get; }
    public ICommand RestartEncoderCommand { get; }
    public ICommand GoLiveCommand { get; }
    public ICommand EndStreamCommand { get; }

    private readonly DispatcherTimer _autoSave;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _autoStatusTimer;
    private readonly DispatcherTimer _monitorTimer;
    private readonly DispatcherTimer _liveCheckTimer;

    // Debounced save: Refresh() is wired to every property change (sliders,
    // text boxes), so saving inline rewrote config.json dozens of times per
    // second while editing. The 15s auto-save timer is the safety net.
    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public MainViewModel()
    {
        Config = ConfigStore.Load();
        _secrets = LoadSecrets();
        SecretsStore.Apply(Config, _secrets);
        SyncEulerKey();
        SyncChannelSnapshot();

        // Attach/detach destination handlers ONLY here (CollectionChanged owns the
        // lifetime) - the old ctor pre-attach loop double-subscribed everything.
        Destinations.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null) foreach (DestinationConfig d in e.OldItems) DetachDestination(d);
            if (e.NewItems != null) foreach (DestinationConfig d in e.NewItems) AttachDestination(d);
            Config.Destinations = Destinations.ToList();
            Refresh();
        };
        Overlays.CollectionChanged += (_, e) =>
        {
            Config.Overlays = Overlays.ToList();
            // The command runner watches every overlay's channels too.
            if (e.NewItems != null) foreach (OverlayConfig o in e.NewItems) o.PropertyChanged += OnOverlayChannelChanged;
            if (e.OldItems != null) foreach (OverlayConfig o in e.OldItems) o.PropertyChanged -= OnOverlayChannelChanged;
            Save();
            _commandRunner?.SyncSources();
        };
        Commands.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (CommandConfig c in e.NewItems)
                    c.PropertyChanged += (_, _) => ScheduleSave();
            Config.Commands = Commands.ToList();
            ScheduleSave();
        };
        Config.Upstream.PropertyChanged += (_, _) => Refresh();
        Config.MyChannels.PropertyChanged += (_, _) =>
        {
            PropagateMyChannelsToOverlays();
            ScheduleSave();
            _commandRunner?.SyncSources();
        };
        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppConfig.ObsEnabled) or nameof(AppConfig.ObsWebSocketUrl))
                EnsureObsClient();
            else if (e.PropertyName is nameof(AppConfig.TwitchClientId))
            {
                SyncTwitchAuth();
                ScheduleSave();
            }
        };

        // Created BEFORE the collections below are populated: adding the loaded
        // destinations fires CollectionChanged -> Refresh -> ScheduleSave, and a
        // null timer here crashed the app at startup.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            Save();
        };

        AddDestinationCommand = new RelayCommand(_ => AddDestination());
        RemoveDestinationCommand = new RelayCommand(_ =>
        {
            if (SelectedDestination != null) RemoveDestination(SelectedDestination);
        }, _ => SelectedDestination != null);
        MoveDestinationLeftCommand = new RelayCommand(p => MoveDestination(p as DestinationConfig, -1), p => p is DestinationConfig);
        MoveDestinationRightCommand = new RelayCommand(p => MoveDestination(p as DestinationConfig, +1), p => p is DestinationConfig);
        AddOverlayCommand = new RelayCommand(_ => AddOverlay());
        RemoveOverlayCommand = new RelayCommand(_ =>
        {
            if (SelectedOverlay != null) RemoveOverlay(SelectedOverlay);
        }, _ => SelectedOverlay != null);
        ExportServerCommand = new RelayCommand(_ => ExportServer());
        SaveCommand = new RelayCommand(_ => Save());
        OpenKeysFileCommand = new RelayCommand(_ => OpenKeysFile());
        MoveKeysFileCommand = new RelayCommand(_ => MoveKeysFile());
        TestConnectionCommand = new RelayCommand(_ => RunBackground(log => DeployService.TestConnection(BuildTarget(), log, Config)),
            _ => !IsRunning);
        DeployCommand = new RelayCommand(_ => RunBackground(log =>
        {
            DeployService.Deploy(Config, BuildTarget(), log);
            RecordDeployedConfig();
            log("checking relay status...");
            RelayStatusText = DeployService.FetchStatus(BuildTarget(), Config);
            Raise(nameof(RelayStatusText));
        }), _ => !IsRunning);
        RefreshStatusCommand = new RelayCommand(_ => RefreshStatus());
        RestartRelayCommand = new RelayCommand(_ => RunBackground(RestartRelay), _ => !IsRunning);
        RestartEncoderCommand = new RelayCommand(p =>
        {
            if (p is WatchdogAlarm alarm && alarm.DestinationId is Guid id)
            {
                var d = Destinations.FirstOrDefault(x => x.Id == id);
                if (d != null) RestartEncoderInner(d);
            }
        }, _ => !IsRunning);
        GoLiveCommand = new RelayCommand(_ => RunBackground(GoLiveFlowAsync), _ => !IsRunning);
        EndStreamCommand = new RelayCommand(_ => RunBackground(EndStreamFlowAsync), _ => !IsRunning);
        LoginTwitchCommand = new RelayCommand(_ => LoginTwitch(), _ => _twitchBusy == 0);
        LogoutTwitchCommand = new RelayCommand(_ => LogoutTwitch(), _ => _twitchAuth != null);
        AddCommandCommand = new RelayCommand(_ => AddCommand());
        RemoveCommandCommand = new RelayCommand(_ =>
        {
            if (SelectedCommand != null) Commands.Remove(SelectedCommand);
        }, _ => SelectedCommand != null);
        EnsureObsClient();

        foreach (var d in Config.Destinations) Destinations.Add(d);
        foreach (var o in Config.Overlays) Overlays.Add(o);
        foreach (var c in Config.Commands) Commands.Add(c);

        // The command runner watches all configured channels for !commands;
        // its ChatHub entries share connections with the overlays.
        _commandRunner = new CommandRunner(Config, SwitchObsSceneAsync);
        _commandRunner.Activity += s => Application.Current?.Dispatcher.BeginInvoke(() => SetCommandActivity(s));
        SyncTwitchAuth();
        _commandRunner.SyncSources();

        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoSave.Tick += (_, _) => Save();
        _autoSave.Start();

        _autoStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoStatusTimer.Tick += (_, _) => { if (AutoStatus && !IsRunning) RefreshStatus(); };
        _autoStatusTimer.Start();

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _monitorTimer.Tick += (_, _) => MonitorTick();
        _monitorTimer.Start();
        MonitorTick();

        _liveCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _liveCheckTimer.Tick += (_, _) => LiveCheckTick();
        _liveCheckTimer.Start();

        Refresh();
        LiveCheckTick();
    }

    private void AttachDestination(DestinationConfig d)
    {
        d.PropertyChanged += DestinationPropertyChanged;
        HookLayers(d);
    }

    private void DetachDestination(DestinationConfig d)
    {
        d.PropertyChanged -= DestinationPropertyChanged;
        UnhookLayers(d);
    }

    private void DestinationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The lights are status OUTPUT, not config input: Refresh() regenerates
        // the whole nginx preview and queues a config+secrets save, and the
        // watchdog/live checks rewrite the lights every 10s/30s - refreshing
        // for each flip was pure churn (BUG-38).
        if (e.PropertyName is nameof(DestinationConfig.PushLight)
            or nameof(DestinationConfig.PushLightText)
            or nameof(DestinationConfig.LiveLight)
            or nameof(DestinationConfig.LiveLightText))
            return;

        if (e.PropertyName is nameof(DestinationConfig.Layers) && sender is DestinationConfig d)
        {
            UnhookLayers(d);
            HookLayers(d);
        }
        if (e.PropertyName is nameof(DestinationConfig.ChannelHandle)
            or nameof(DestinationConfig.Enabled)
            or nameof(DestinationConfig.Platform))
        {
            LiveCheckTick();
        }
        Refresh();
    }

    // Tracked subscriptions: the old code never detached handlers from replaced
    // or cleared layer collections, so handlers stacked up across long sessions.
    private readonly Dictionary<DestinationConfig, (ObservableCollection<OutputLayer> Layers, NotifyCollectionChangedEventHandler Handler)> _hookedLayers = new();

    private void HookLayers(DestinationConfig d)
    {
        if (_hookedLayers.ContainsKey(d)) return;
        NotifyCollectionChangedEventHandler handler = (_, e) =>
        {
            if (e.OldItems != null) foreach (OutputLayer l in e.OldItems) l.PropertyChanged -= LayerPropertyChanged;
            if (e.NewItems != null) foreach (OutputLayer l in e.NewItems) l.PropertyChanged += LayerPropertyChanged;
            Refresh();
        };
        _hookedLayers[d] = (d.Layers, handler);
        d.Layers.CollectionChanged += handler;
        foreach (var l in d.Layers) l.PropertyChanged += LayerPropertyChanged;
    }

    private void UnhookLayers(DestinationConfig d)
    {
        if (!_hookedLayers.Remove(d, out var hooked)) return;
        hooked.Layers.CollectionChanged -= hooked.Handler;
        foreach (var l in hooked.Layers) l.PropertyChanged -= LayerPropertyChanged;
    }

    private void LayerPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void AddDestination()
    {
        var dest = new DestinationConfig
        {
            Name = "New destination",
            Platform = Platform.Custom,
            IngestUrl = "",
            StreamKey = "",
        };
        SelectedDestination = dest;
        Destinations.Add(dest);
    }

    private void RemoveDestination(DestinationConfig dest)
    {
        Destinations.Remove(dest);
        Config.Destinations.Remove(dest);
        SelectedDestination = null;
        Refresh();
    }

    // Places dest at the given list index (drag-drop semantics: "remove, then insert").
    public void MoveDestinationTo(DestinationConfig dest, int index)
    {
        int old = Destinations.IndexOf(dest);
        if (old < 0) return;
        if (index > old) index--;
        index = Math.Clamp(index, 0, Destinations.Count - 1);
        if (index == old) return;
        Destinations.Move(old, index);
        Refresh();
    }

    // The arrows move exactly one slot: calling MoveDestinationTo(i + 1) here
    // cancelled out against its drag-drop decrement (a silent no-op), which is
    // why the "move right" button never did anything.
    private void MoveDestination(DestinationConfig? dest, int delta)
    {
        if (dest is null) return;
        int i = Destinations.IndexOf(dest);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Destinations.Count) return;
        Destinations.Move(i, j);
        Refresh();
    }

    private void AddOverlay()
    {
        var overlay = new OverlayConfig
        {
            Name = $"Overlay {Overlays.Count + 1}",
            Mode = ChatMode.All,
            TwitchChannel = Config.MyChannels.TwitchChannel,
            KickChannel = Config.MyChannels.KickChannel,
            YouTubeUrl = Config.MyChannels.YouTubeUrl,
            TikTokHandle = Config.MyChannels.TikTokHandle,
        };
        if (Overlays.Count == 0)
        {
            overlay.X = SystemParameters.WorkArea.Width - overlay.Width - 40;
            overlay.Y = 60;
        }
        SelectedOverlay = overlay;
        // Overlays.CollectionChanged already re-syncs Config.Overlays - the old
        // explicit Add here inserted the entry twice.
        Overlays.Add(overlay);
        OverlayAdded?.Invoke(overlay);
        Save();
    }

    private void RemoveOverlay(OverlayConfig overlay)
    {
        Overlays.Remove(overlay);
        Config.Overlays.Remove(overlay);
        OverlayRemoved?.Invoke(overlay);
        if (SelectedOverlay == overlay) SelectedOverlay = null;
        Save();
    }

    public void ToggleOverlayLock(OverlayConfig overlay)
    {
        overlay.Locked = !overlay.Locked;
        Save();
    }

    private void ExportServer()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick a folder for the relay server bundle",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            ServerExporter.Export(dialog.FolderName, Config);
            MessageBox.Show(
                $"Server bundle written to:\n{dialog.FolderName}\n\nOpen SETUP.md inside it for the full walk-through.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public DeployTarget BuildTarget()
    {
        if (string.IsNullOrWhiteSpace(Config.ServerHost))
            throw new Exception("set your server host (IP) first - Relay tab or the field above");
        if (string.IsNullOrWhiteSpace(Config.SshUser))
            throw new Exception("set your SSH user first (usually 'root')");
        return new DeployTarget(
            Config.ServerHost.Trim(),
            Config.SshUser.Trim(),
            Config.SshUseKey,
            SshPassword,
            Config.SshKeyPath,
            string.IsNullOrWhiteSpace(Config.RemotePath) ? "/opt/kat-relay" : Config.RemotePath.Trim());
    }

    private void Log(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _deployLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_deployLog.Length > 16000)
                _deployLog.Remove(0, _deployLog.Length - 12000);
            Raise(nameof(DeployLog));
        });
    }

    private void RunBackground(Action<Action<string>> work)
    {
        RunBackground(log =>
        {
            work(log);
            return Task.CompletedTask;
        });
    }

    private void RunBackground(Func<Action<string>, Task> work)
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            // Validated once up front so the button fails fast with a clear message.
            BuildTarget();
            Task.Run(async () =>
            {
                try
                {
                    await work(Log);
                }
                catch (Exception ex)
                {
                    Log("[error] " + ex.Message);
                }
                finally
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        IsRunning = false;
                        CommandManager.InvalidateRequerySuggested();
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Log("[error] " + ex.Message);
            IsRunning = false;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshStatus()
    {
        if (Interlocked.CompareExchange(ref _statusBusy, 1, 0) != 0) return;
        DeployTarget target;
        try
        {
            target = BuildTarget();
        }
        catch (Exception ex)
        {
            RelayStatusText = ex.Message;
            Interlocked.Exchange(ref _statusBusy, 0);
            return;
        }
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            try
            {
                return DeployService.FetchStatus(target, Config);
            }
            catch (Exception ex)
            {
                return "status check failed: " + ex.Message;
            }
        }).ContinueWith(t =>
        {
            RelayStatusText = t.Result;
            Interlocked.Exchange(ref _statusBusy, 0);
        }, ui);
    }

    public void Save()
    {
        Config.Destinations = Destinations.ToList();
        Config.Overlays = Overlays.ToList();
        try
        {
            ConfigStore.Save(Config);
        }
        catch
        {
            // Non-fatal.
        }
        try
        {
            SecretsStore.SaveFromConfig(EffectiveKeysPath, Config, _secrets);
        }
        catch
        {
            // Non-fatal.
        }
    }

    private SecretsData LoadSecrets()
    {
        var path = EffectiveKeysPath;
        if (File.Exists(path))
            return SecretsStore.Load(path);

        var legacy = ConfigStore.TryReadLegacySecrets();
        if (legacy != null)
        {
            try { SecretsStore.Save(path, legacy); } catch { }
            return legacy;
        }
        return new SecretsData();
    }

    private void OpenKeysFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open keys file",
            Filter = "Keys file (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(EffectiveKeysPath) ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        Config.KeysFilePath = dialog.FileName;
        _secrets = SecretsStore.Load(dialog.FileName);
        SecretsStore.Apply(Config, _secrets);
        SyncEulerKey();
        RecreateObsClient();
        Raise(nameof(KeysFilePathDisplay));
        Raise(nameof(EulerApiKey));
        Raise(nameof(ObsPassword));
        Refresh();
    }

    private void MoveKeysFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Move keys file to...",
            Filter = "Keys file (*.json)|*.json",
            FileName = "kat-keys.json",
            InitialDirectory = Path.GetDirectoryName(EffectiveKeysPath) ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        SecretsStore.SaveFromConfig(dialog.FileName, Config, _secrets);
        Config.KeysFilePath = dialog.FileName;
        // Reload the file we just wrote instead of Capture(): Capture builds from
        // config alone and would drop SshPassword/EulerApiKey out of _secrets,
        // silently wiping them from the new file on the next save.
        _secrets = SecretsStore.Load(dialog.FileName);
        SyncEulerKey();
        RecreateObsClient();
        Raise(nameof(KeysFilePathDisplay));
        Raise(nameof(SshPassword));
        Raise(nameof(EulerApiKey));
        Raise(nameof(ObsPassword));
        Save();
    }

    private void Refresh()
    {
        Config.Destinations = Destinations.ToList();

        double serverMbps = Destinations.Where(d => d.Enabled)
            .Sum(d => (d.VideoBitrateKbps + d.AudioBitrateKbps) / 1000.0) * 1.1;
        double homeMbps = Config.Upstream.VideoBitrateKbps / 1000.0 * 1.1;
        double tbMonth = serverMbps * 0.45 * 720 / 1000;

        BandwidthSummary = $"Home uploads ONE stream to the server: ~{homeMbps:F1} Mbps.   " +
                           $"Server sends everything: ~{serverMbps:F1} Mbps (~{tbMonth:F1} TB/month if live 24/7).";
        Raise(nameof(BandwidthSummary));

        NginxPreview = RelayConfigGenerator.GenerateNginxConf(Config, includeKeys: ShowSecretsInPreview);
        SelectedCommandPreview = SelectedDestination is null
            ? ""
            : RelayConfigGenerator.DescribeDestination(Config, SelectedDestination, ShowSecretsInPreview);
        Raise(nameof(SelectedCommandPreview));
        UpdateDeployStaleness();
        ScheduleSave();
    }

    private void UpdateDeployStaleness()
    {
        string stored = _secrets.LastDeployedNginxHash;
        ServerConfigOutdatedText = stored.Length == 0
            ? "" // never deployed from a build that records this - stay quiet
            : ServerExporter.ComputeNginxConfigHash(Config).Equals(stored, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"the server still runs the config from {LastDeployedDisplay} - rotated keys and destination changes only go live after 'Deploy to server'";
        Raise(nameof(ServerConfigOutdatedText));
    }

    private string LastDeployedDisplay => _secrets.LastDeployedAtUtc == default
        ? "an older deploy"
        : _secrets.LastDeployedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private void RecordDeployedConfig()
    {
        _secrets.LastDeployedNginxHash = ServerExporter.ComputeNginxConfigHash(Config);
        _secrets.LastDeployedAtUtc = DateTime.UtcNow;
        Save();
        UpdateDeployStaleness();
    }

    // ---------- "My channels" -> overlay propagation ----------

    // Last-seen "My channels" values. When one changes, overlays that still
    // carry the OLD value (pre-filled and never individually edited) follow
    // to the new one - otherwise a rotated channel leaves every existing
    // overlay (and the command runner) connected to the dead channel.
    private readonly string[] _syncedChannels = new string[4];

    private void SyncChannelSnapshot()
    {
        var mc = Config.MyChannels;
        _syncedChannels[0] = mc.TwitchChannel;
        _syncedChannels[1] = mc.KickChannel;
        _syncedChannels[2] = mc.YouTubeUrl;
        _syncedChannels[3] = mc.TikTokHandle;
    }

    private void PropagateMyChannelsToOverlays()
    {
        var mc = Config.MyChannels;
        string[] current = { mc.TwitchChannel, mc.KickChannel, mc.YouTubeUrl, mc.TikTokHandle };
        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (current[i] == _syncedChannels[i]) continue;
                foreach (var o in Overlays)
                {
                    string overlayValue = i switch
                    {
                        0 => o.TwitchChannel,
                        1 => o.KickChannel,
                        2 => o.YouTubeUrl,
                        _ => o.TikTokHandle,
                    };
                    // Only follow overlays that still carry the previous value;
                    // one pointed at a different channel on purpose stays put.
                    if (overlayValue != _syncedChannels[i]) continue;
                    switch (i)
                    {
                        case 0: o.TwitchChannel = current[i]; break;
                        case 1: o.KickChannel = current[i]; break;
                        case 2: o.YouTubeUrl = current[i]; break;
                        default: o.TikTokHandle = current[i]; break;
                    }
                }
            }
        }
        finally
        {
            _syncedChannels[0] = current[0];
            _syncedChannels[1] = current[1];
            _syncedChannels[2] = current[2];
            _syncedChannels[3] = current[3];
        }
    }
}
