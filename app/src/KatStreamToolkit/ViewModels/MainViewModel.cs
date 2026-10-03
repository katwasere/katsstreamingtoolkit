using System.Collections.ObjectModel;
using System.IO;
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

    public OverlayConfig? SelectedOverlay { get => _selectedOverlay; set => Set(ref _selectedOverlay, value); }
    public DestinationConfig? SelectedDestination { get => _selectedDestination; set => Set(ref _selectedDestination, value); }

    public string NginxPreview { get => _nginxPreview; private set => Set(ref _nginxPreview, value); }

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

    public ICommand AddDestinationCommand { get; }
    public ICommand RemoveDestinationCommand { get; }
    public ICommand AddOverlayCommand { get; }
    public ICommand RemoveOverlayCommand { get; }
    public ICommand ExportServerCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenKeysFileCommand { get; }
    public ICommand MoveKeysFileCommand { get; }

    private readonly DispatcherTimer _autoSave;

    public MainViewModel()
    {
        Config = ConfigStore.Load();
        _secrets = LoadSecrets();
        SecretsStore.Apply(Config, _secrets);

        foreach (var d in Config.Destinations) AttachDestination(d);
        Destinations.CollectionChanged += (_, e) =>
        {
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
        Config.MyChannels.PropertyChanged += (_, _) => Save();

        AddDestinationCommand = new RelayCommand(_ => AddDestination());
        RemoveDestinationCommand = new RelayCommand(_ =>
        {
            if (SelectedDestination != null) RemoveDestination(SelectedDestination);
        }, _ => SelectedDestination != null);
        AddOverlayCommand = new RelayCommand(_ => AddOverlay());
        RemoveOverlayCommand = new RelayCommand(_ =>
        {
            if (SelectedOverlay != null) RemoveOverlay(SelectedOverlay);
        }, _ => SelectedOverlay != null);
        ExportServerCommand = new RelayCommand(_ => ExportServer());
        SaveCommand = new RelayCommand(_ => Save());
        OpenKeysFileCommand = new RelayCommand(_ => OpenKeysFile());
        MoveKeysFileCommand = new RelayCommand(_ => MoveKeysFile());

        foreach (var d in Config.Destinations) Destinations.Add(d);
        foreach (var o in Config.Overlays) Overlays.Add(o);

        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoSave.Tick += (_, _) => Save();
        _autoSave.Start();

        Refresh();
    }

    private void AttachDestination(DestinationConfig d)
    {
        d.PropertyChanged += (_, _) => Refresh();
    }

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
        Overlays.Add(overlay);
        Config.Overlays.Add(overlay);
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
            SecretsStore.SaveFromConfig(EffectiveKeysPath, Config);
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

        SecretsStore.SaveFromConfig(dialog.FileName, Config);
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
        Save();
    }
}
