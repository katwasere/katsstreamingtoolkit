using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using KatStreamToolkit.Models;
using KatStreamToolkit.Services;
using Microsoft.Win32;

namespace KatStreamToolkit.ViewModels;

public class MainViewModel : ObservableBase
{
    private string _nginxPreview = "";
    private OverlayConfig? _selectedOverlay;
    private DestinationConfig? _selectedDestination;
    private SecretsData _secrets = new();
    private bool _showSecretsInPreview;

    public event Action<OverlayConfig>? OverlayAdded;
    public event Action<OverlayConfig>? OverlayRemoved;

    public AppConfig Config { get; }
    public ObservableCollection<DestinationConfig> Destinations { get; } = new();
    public ObservableCollection<OverlayConfig> Overlays { get; } = new();

    public static Platform[] AllPlatforms { get; } = Enum.GetValues<Platform>();
    public static Orientation[] AllOrientations { get; } = Enum.GetValues<Orientation>();
    public static PortraitStyle[] AllPortraitStyles { get; } = Enum.GetValues<PortraitStyle>();
    public static ChatMode[] AllChatModes { get; } = Enum.GetValues<ChatMode>();
    public static string[] AllEncoderPresets { get; } = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" };

    public OverlayConfig? SelectedOverlay { get => _selectedOverlay; set => Set(ref _selectedOverlay, value); }
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

    private void ApplySnapshot(bool reachable, bool receiving)
    {
        RelayLight = reachable ? VerifyLight.Ok : VerifyLight.Error;
        ObsLight = !reachable ? VerifyLight.Unknown : receiving ? VerifyLight.Ok : VerifyLight.Idle;
        RelayStatusText = !reachable
            ? "relay not responding - deploy the bundle first"
            : receiving ? "receiving your stream" : "relay running, waiting for OBS";
        foreach (var d in Destinations)
            d.PushLight = !reachable ? VerifyLight.Unknown : receiving ? VerifyLight.Ok : VerifyLight.Idle;
        Raise(nameof(ObsLight));
        Raise(nameof(ObsLightText));
        Raise(nameof(RelayLight));
        Raise(nameof(RelayLightText));
        Raise(nameof(RelayStatusText));
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
            ApplySnapshot(false, false);
            RelayLight = VerifyLight.Unknown;
            ObsLight = VerifyLight.Unknown;
            RelayStatusText = "not checked yet";
            foreach (var d in Destinations) d.PushLight = VerifyLight.Unknown;
            Raise(nameof(ObsLight));
            Raise(nameof(ObsLightText));
            Raise(nameof(RelayLight));
            Raise(nameof(RelayLightText));
            Raise(nameof(RelayStatusText));
            Interlocked.Exchange(ref _monitorBusy, 0);
            return;
        }
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() =>
        {
            try
            {
                return DeployService.FetchSnapshot(target);
            }
            catch
            {
                return (false, false);
            }
        }).ContinueWith(t =>
        {
            ApplySnapshot(t.Result.Item1, t.Result.Item2);
            Interlocked.Exchange(ref _monitorBusy, 0);
        }, ui);
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

    private readonly DispatcherTimer _autoSave;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _autoStatusTimer;
    private readonly DispatcherTimer _monitorTimer;

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
            Save();
        };
        Config.Upstream.PropertyChanged += (_, _) => Refresh();
        Config.MyChannels.PropertyChanged += (_, _) => ScheduleSave();

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
        TestConnectionCommand = new RelayCommand(_ => RunBackground(log => DeployService.TestConnection(BuildTarget(), log)),
            _ => !IsRunning);
        DeployCommand = new RelayCommand(_ => RunBackground(log =>
        {
            DeployService.Deploy(Config, BuildTarget(), log);
            log("checking relay status...");
            RelayStatusText = DeployService.FetchStatus(BuildTarget());
            Raise(nameof(RelayStatusText));
        }), _ => !IsRunning);
        RefreshStatusCommand = new RelayCommand(_ => RefreshStatus());

        foreach (var d in Config.Destinations) Destinations.Add(d);
        foreach (var o in Config.Overlays) Overlays.Add(o);

        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoSave.Tick += (_, _) => Save();
        _autoSave.Start();

        // Debounced save: Refresh() is wired to every property change (sliders,
        // text boxes), so saving inline rewrote config.json dozens of times per
        // second while editing. The 15s auto-save above is the safety net.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            Save();
        };

        _autoStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoStatusTimer.Tick += (_, _) => { if (AutoStatus && !IsRunning) RefreshStatus(); };
        _autoStatusTimer.Start();

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _monitorTimer.Tick += (_, _) => MonitorTick();
        _monitorTimer.Start();
        MonitorTick();

        Refresh();
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
        if (e.PropertyName is nameof(DestinationConfig.Layers) && sender is DestinationConfig d)
        {
            UnhookLayers(d);
            HookLayers(d);
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
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            var target = BuildTarget();
            Task.Run(() =>
            {
                try
                {
                    work(Log);
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
                return DeployService.FetchStatus(target);
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
        Raise(nameof(KeysFilePathDisplay));
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
        _secrets = SecretsStore.Capture(Config);
        Raise(nameof(KeysFilePathDisplay));
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
        ScheduleSave();
    }
}
