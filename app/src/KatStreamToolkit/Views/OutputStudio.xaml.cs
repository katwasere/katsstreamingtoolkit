using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KatStreamToolkit.Models;
using KatStreamToolkit.Services;
using KatStreamToolkit.ViewModels;
using Microsoft.Win32;

namespace KatStreamToolkit.Views;

public sealed record LiveWindow(string Title, IntPtr Hwnd)
{
    public override string ToString() => Title;
}

// Output Studio: pipeline flow (drag to reorder), per-destination inspector,
// layered rendition preview (auto-playing local mirror + true server snapshots)
// and the generated nginx.conf lines for the selection.
public partial class OutputStudio : UserControl
{
    private static readonly Brush ShadeBlur = Frozen(0x59, 0x00, 0x00, 0x00);
    private static readonly Brush ShadeImage = Frozen(0x26, 0x00, 0x00, 0x00);
    private static readonly Brush FrostBrush = Frozen(0x66, 0x3A, 0x41, 0x52);
    private static readonly Brush SelectBrush = Frozen(0xFF, 0x8F, 0xD6, 0x94);

    private MainViewModel? _vm;
    private DestinationConfig? _watchedDestination;
    private UpstreamConfig? _watchedUpstream;
    private readonly HashSet<Guid> _hookedLayers = new();
    private DestinationConfig? _dragItem;
    private Point _dragStartPoint;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _serverTimer;
    private int _serverBusy;
    private int _liveTick;
    private IntPtr _autoHwnd;
    private bool _updatingLayerProps;
    private bool _loaded;

    private bool _cropDragging;
    private Point _cropDragOffset;
    private bool _fgDragging;
    private Point _fgDragOffset;
    private OutputLayer? _layerDrag;
    private Point _layerDragOffset;

    private OutputLayer? _selectedLayer;

    private ImageSource? _bgImage;
    private string _bgImagePath = "";
    private ImageSource? _mirrorFrame;
    private readonly Dictionary<string, BitmapImage?> _imageCache = new();

    public OutputStudio()
    {
        InitializeComponent();
        blurBg.Effect = new BlurEffect { Radius = 40, RenderingBias = RenderingBias.Performance };
        customBgBlur.Effect = new BlurEffect { Radius = 40, RenderingBias = RenderingBias.Performance };
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) =>
        {
            HookViewModel();
            // The mirror preview auto-plays while editing; no manual controls.
            EnsureLiveTimer().Start();
        };
        Unloaded += (_, _) =>
        {
            _liveTimer?.Stop();
            _serverTimer?.Stop();
            RelayPreviewService.Shutdown();
        };
        _loaded = true;
    }

    private void HookViewModel()
    {
        var vm = DataContext as MainViewModel;
        if (!ReferenceEquals(_vm, vm))
        {
            if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
        }
        if (_vm != null && !ReferenceEquals(_watchedUpstream, _vm.Config.Upstream))
        {
            if (_watchedUpstream != null) _watchedUpstream.PropertyChanged -= Upstream_PropertyChanged;
            _watchedUpstream = _vm.Config.Upstream;
            _watchedUpstream.PropertyChanged += Upstream_PropertyChanged;
        }
        WatchSelectedDestination();
        UpdateLayerUi();
        UpdatePreview();
    }

    private void WatchSelectedDestination()
    {
        var selected = _vm?.SelectedDestination;
        if (ReferenceEquals(_watchedDestination, selected)) return;
        if (_watchedDestination != null)
        {
            _watchedDestination.PropertyChanged -= Destination_PropertyChanged;
            _watchedDestination.Layers.CollectionChanged -= Layers_CollectionChanged;
            foreach (var layer in _watchedDestination.Layers) UnhookLayer(layer);
        }
        _watchedDestination = selected;
        _selectedLayer = null;
        if (_watchedDestination != null)
        {
            _watchedDestination.PropertyChanged += Destination_PropertyChanged;
            _watchedDestination.Layers.CollectionChanged += Layers_CollectionChanged;
            if (!ReferenceEquals(LayersList.ItemsSource, _watchedDestination.Layers))
                LayersList.ItemsSource = _watchedDestination.Layers;
            foreach (var layer in _watchedDestination.Layers) HookLayer(layer);
        }
    }

    private void Layers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (OutputLayer l in e.NewItems) HookLayer(l);
        if (e.OldItems != null) foreach (OutputLayer l in e.OldItems) UnhookLayer(l);
        if (_selectedLayer is not null && e.OldItems?.Contains(_selectedLayer) == true) _selectedLayer = null;
        UpdateLayerUi();
        UpdatePreview();
    }

    private void HookLayer(OutputLayer layer)
    {
        if (!_hookedLayers.Add(layer.Id)) return;
        layer.PropertyChanged += Layer_PropertyChanged;
    }

    // The old code never detached: the Id set and the handler closures grew
    // forever across long sessions.
    private void UnhookLayer(OutputLayer layer)
    {
        if (!_hookedLayers.Remove(layer.Id)) return;
        layer.PropertyChanged -= Layer_PropertyChanged;
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateLayerUi();
        UpdatePreview();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedDestination))
        {
            WatchSelectedDestination();
            UpdateLayerUi();
            UpdatePreview();
        }
    }

    private void Destination_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DestinationConfig.Orientation)
            or nameof(DestinationConfig.PortraitStyle)
            or nameof(DestinationConfig.CropX)
            or nameof(DestinationConfig.CropY)
            or nameof(DestinationConfig.CropW)
            or nameof(DestinationConfig.FgX)
            or nameof(DestinationConfig.FgY)
            or nameof(DestinationConfig.FgScale)
            or nameof(DestinationConfig.CustomBackgroundPath)
            or nameof(DestinationConfig.Layers)))
            return;

        if (e.PropertyName == nameof(DestinationConfig.PortraitStyle)
            && _watchedDestination is { PortraitStyle: PortraitStyle.Custom } dest)
            EnsureCustomDefaults(dest);

        UpdateLayerUi();
        UpdatePreview();
    }

    private void Upstream_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpstreamConfig.Width) or nameof(UpstreamConfig.Height))
            UpdatePreview();
    }

    // Switching a destination to Custom seeds one Source cut covering the whole
    // feed (landscape) or the center-crop equivalent (portrait), so the layer
    // stack starts sane and the user drags from there.
    private void EnsureCustomDefaults(DestinationConfig dest)
    {
        if (dest.Layers.Count > 0 || dest.CropW > 0 || dest.FgScale > 0) return;
        var up = _vm?.Config.Upstream;
        if (up is null) return;
        int srcW = Math.Max(2, up.Width), srcH = Math.Max(2, up.Height);
        OutputLayer layer;
        if (dest.Orientation == Orientation.Landscape)
        {
            layer = new OutputLayer { Type = LayerType.Source, Name = "Source cut 1", X = 0, Y = 0, W = 1, H = 1 };
            layer.SrcX = 0;
            layer.SrcY = 0;
            layer.SrcW = 1;
            layer.SrcH = 1;
        }
        else
        {
            var c = RelayConfigGenerator.ComputeCropRect(srcW, srcH);
            layer = new OutputLayer
            {
                Type = LayerType.Source,
                Name = "Source cut 1",
                X = 0,
                Y = 0,
                W = 1,
                H = 1,
            };
            layer.SrcX = Math.Min(1, (double)c.X / srcW);
            layer.SrcY = Math.Min(1, (double)c.Y / srcH);
            layer.SrcW = Math.Min(1, (double)c.CropW / srcW);
            layer.SrcH = Math.Min(1, (double)c.CropH / srcH);
        }
        dest.Layers.Add(layer);
        SelectLayer(layer);
    }

    // ---------------- layer list ----------------

    private void SelectLayer(OutputLayer? layer)
    {
        _selectedLayer = layer;
        UpdateLayerUi();
        UpdatePreview();
    }

    private void UpdateLayerUi()
    {
        var dest = _watchedDestination;
        bool custom = dest is { PortraitStyle: PortraitStyle.Custom };
        LayerEditorPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        LegacyCustomTools.Visibility = dest is { PortraitStyle: PortraitStyle.Custom, Orientation: Orientation.Portrait, Layers: { Count: 0 } }
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!custom)
        {
            _selectedLayer = null;
            LayerPropsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!ReferenceEquals(LayersList.ItemsSource, dest!.Layers)) LayersList.ItemsSource = dest.Layers;
        if (_selectedLayer is null || !dest.Layers.Contains(_selectedLayer)) _selectedLayer = dest.Layers.FirstOrDefault();
        _updatingLayerProps = true;
        LayersList.SelectedItem = _selectedLayer;
        _updatingLayerProps = false;
        UpdateLayerProps();
    }

    private void UpdateLayerProps()
    {
        var layer = _selectedLayer;
        if (layer is null)
        {
            LayerPropsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LayerPropsPanel.Visibility = Visibility.Visible;
        LayerPropsTitle.Text = $"{OutputLayer.DefaultName(layer.Type)}: {layer.Name}";
        LayerSrcPanel.Visibility = layer.Type == LayerType.Source ? Visibility.Visible : Visibility.Collapsed;
        LayerImagePanel.Visibility = layer.Type == LayerType.Image ? Visibility.Visible : Visibility.Collapsed;
        LayerImagePathBox.Text = layer.Type == LayerType.Image ? layer.Path : "";

        _updatingLayerProps = true;
        LayerXSlider.Value = layer.X;
        LayerYSlider.Value = layer.Y;
        LayerWSlider.Value = layer.W;
        LayerHSlider.Value = layer.H;
        SrcXSlider.Value = layer.SrcX;
        SrcYSlider.Value = layer.SrcY;
        SrcWSlider.Value = layer.SrcW;
        SrcHSlider.Value = layer.SrcH;
        _updatingLayerProps = false;
    }

    private void LayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLayerProps) return;
        SelectLayer(LayersList.SelectedItem as OutputLayer);
    }

    private void LayerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingLayerProps || _selectedLayer is null) return;
        _updatingLayerProps = true;
        var l = _selectedLayer;
        if (sender == LayerXSlider) l.X = e.NewValue;
        else if (sender == LayerYSlider) l.Y = e.NewValue;
        else if (sender == LayerWSlider) l.W = e.NewValue;
        else if (sender == LayerHSlider) l.H = e.NewValue;
        else if (sender == SrcXSlider) l.SrcX = e.NewValue;
        else if (sender == SrcYSlider) l.SrcY = e.NewValue;
        else if (sender == SrcWSlider) l.SrcW = e.NewValue;
        else if (sender == SrcHSlider) l.SrcH = e.NewValue;
        UpdateLayerProps();
        _updatingLayerProps = false;
    }

    private void AddLayer(OutputLayer layer)
    {
        if (_watchedDestination is not { PortraitStyle: PortraitStyle.Custom } dest) return;
        int same = dest.Layers.Count(l => l.Type == layer.Type) + 1;
        layer.Name = $"{OutputLayer.DefaultName(layer.Type)} {same}";
        dest.Layers.Add(layer);
        SelectLayer(layer);
    }

    private void AddSourceLayer_Click(object sender, RoutedEventArgs e)
    {
        var (sx, sy, sw, sh) = DefaultSourceCut();
        AddLayer(new OutputLayer
        {
            Type = LayerType.Source,
            SrcX = sx, SrcY = sy, SrcW = sw, SrcH = sh,
            X = 0.05, Y = 0.05, W = 0.35, H = 0.35,
        });
    }

    private (double X, double Y, double W, double H) DefaultSourceCut()
    {
        var dest = _watchedDestination;
        var up = _vm?.Config.Upstream;
        if (dest is { Orientation: Orientation.Landscape } || up is null)
            return (0, 0, 1, 1);
        int srcW = Math.Max(2, up.Width), srcH = Math.Max(2, up.Height);
        var c = RelayConfigGenerator.ComputeCropRect(srcW, srcH);
        return (Math.Min(1, (double)c.X / srcW), Math.Min(1, (double)c.Y / srcH),
                Math.Min(1, (double)c.CropW / srcW), Math.Min(1, (double)c.CropH / srcH));
    }

    private void AddImageLayer_Click(object sender, RoutedEventArgs e)
    {
        var layer = new OutputLayer { Type = LayerType.Image, X = 0.05, Y = 0.05, W = 0.25, H = 0.25 };
        string? path = PickImage("Pick a transparent PNG overlay");
        if (path is not null) layer.Path = path;
        AddLayer(layer);
    }

    private void AddBlurLayer_Click(object sender, RoutedEventArgs e)
        => AddLayer(new OutputLayer { Type = LayerType.Blur, X = 0.25, Y = 0.4, W = 0.5, H = 0.2 });

    private void AddRevealLayer_Click(object sender, RoutedEventArgs e)
        => AddLayer(new OutputLayer { Type = LayerType.BackgroundReveal, X = 0.3, Y = 0.35, W = 0.4, H = 0.3 });

    private void LayerForward_Click(object sender, RoutedEventArgs e) => MoveLayer((sender as FrameworkElement)?.DataContext as OutputLayer, +1);
    private void LayerBackward_Click(object sender, RoutedEventArgs e) => MoveLayer((sender as FrameworkElement)?.DataContext as OutputLayer, -1);

    private void MoveLayer(OutputLayer? layer, int delta)
    {
        if (_watchedDestination is null || layer is null) return;
        var layers = _watchedDestination.Layers;
        int i = layers.IndexOf(layer);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= layers.Count) return;
        layers.Move(i, j);
    }

    private void LayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_watchedDestination is null) return;
        if ((sender as FrameworkElement)?.DataContext is not OutputLayer layer) return;
        _watchedDestination.Layers.Remove(layer);
        if (ReferenceEquals(_selectedLayer, layer)) SelectLayer(null);
    }

    private void LayerImage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutputLayer layer) return;
        string? path = PickImage("Pick a transparent PNG overlay");
        if (path is not null) layer.Path = path;
    }

    private void LayerImageClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OutputLayer layer) layer.Path = "";
    }

    private string? PickImage(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Images|*.png;*.gif;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void SwitchToCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedDestination is { PortraitStyle: not PortraitStyle.Custom } dest)
            dest.PortraitStyle = PortraitStyle.Custom; // PropertyChanged seeds the default Source cut
    }

    // ---------------- rendition preview ----------------

    private bool RoutesThroughFfmpeg(DestinationConfig dest)
        => dest.Orientation == Orientation.Portrait || dest.PortraitStyle == PortraitStyle.Custom;

    private void ShowOnly(bool landscape, bool crop, bool blur, bool custom, bool server)
    {
        LandscapeNote.Visibility = landscape ? Visibility.Visible : Visibility.Collapsed;
        CropSection.Visibility = crop ? Visibility.Visible : Visibility.Collapsed;
        BlurSection.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;
        CustomSection.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ServerSection.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
    }

    // Renders the rendition preview with the same math the relay uses,
    // so what you see here is what the platform receives.
    private void UpdatePreview()
    {
        if (!_loaded) return;
        var dest = _vm?.SelectedDestination;
        var up = _vm?.Config.Upstream;
        bool serverMode = PreviewSourceCombo?.SelectedIndex == 1;

        if (dest is null || up is null)
        {
            ShowOnly(landscape: false, crop: false, blur: false, custom: false, server: false);
            StopServerPoll();
            return;
        }

        if (serverMode)
        {
            _liveTimer?.Stop();
            bool hasFfmpeg = RoutesThroughFfmpeg(dest);
            ShowOnly(landscape: !hasFfmpeg, crop: false, blur: false, custom: false, server: hasFfmpeg);
            ServerPreviewBox.Width = dest.Orientation == Orientation.Portrait ? 158 : 280;
            ServerPreviewBox.Height = dest.Orientation == Orientation.Portrait ? 280 : 158;
            ServerPreviewImage.Source = null;
            ServerWaitingText.Text = hasFfmpeg
                ? "waiting for frames - deploy with snapshots enabled, then go live"
                : "landscape plain-copy outputs have no snapshot - switch them to the Custom layout, or use Mirror";
            if (hasFfmpeg) StartServerPoll(dest.Id);
            else StopServerPoll();
            return;
        }

        StopServerPoll();
        EnsureLiveTimer().Start();
        ShowOnly(landscape: false, crop: false, blur: false, custom: false, server: false);

        int srcW = Math.Max(2, up.Width);
        int srcH = Math.Max(2, up.Height);
        bool isCrop = dest.PortraitStyle == PortraitStyle.CenterCrop;
        bool isBlur = dest.PortraitStyle == PortraitStyle.BlurredBackground;
        bool isCustom = dest.PortraitStyle == PortraitStyle.Custom;
        bool portraitOut = dest.Orientation == Orientation.Portrait;
        bool layered = isCustom && dest.Layers.Count > 0;

        if (dest.Orientation == Orientation.Landscape && !isCustom)
        {
            LandscapeNote.Visibility = Visibility.Visible;
            return;
        }

        ShowOnly(landscape: false, crop: isCrop || (isCustom && portraitOut), blur: isBlur, custom: isCustom, server: false);
        CropResultGroup.Visibility = isCrop && portraitOut ? Visibility.Visible : Visibility.Collapsed;
        customFgCanvas.Visibility = isCustom && !layered ? Visibility.Visible : Visibility.Collapsed;
        layerHost.Visibility = isCustom && layered ? Visibility.Visible : Visibility.Collapsed;
        CropDragHint.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        CropDragHint.Text = layered
            ? "Drag on 'Your output' with a Source cut selected to set its area; drag pieces in the result to move them."
            : "Drag the green box over your output to set the crop.";

        // Result canvas: 9:16 for portrait, 16:9-ish for landscape custom.
        double canvasW = portraitOut ? 158 : 280;
        double canvasH = portraitOut ? 280 : 158;
        CustomResultBox.Width = canvasW;
        CustomResultBox.Height = canvasH;

        if (isCrop || (isCustom && portraitOut))
        {
            double cropPxW, cropPxH;
            if (isCustom)
            {
                var n = RelayConfigGenerator.NormalizedCustomLayout(dest, srcW, srcH);
                cropPxW = n.CropW * srcW;
                cropPxH = n.CropH * srcH;
            }
            else
            {
                var c = RelayConfigGenerator.ComputeCropRect(srcW, srcH);
                cropPxW = c.CropW;
                cropPxH = c.CropH;
            }

            double fit = Math.Min(380.0 / srcW, 200.0 / srcH);
            srcFrame.Width = srcW * fit;
            srcFrame.Height = srcH * fit;
            srcOverlay.Width = srcW * fit;
            srcOverlay.Height = srcH * fit;
            srcOverlay.SourceW = srcW;
            srcOverlay.SourceH = srcH;
            srcOverlay.CropW = cropPxW;
            srcOverlay.CropH = cropPxH;
        }

        if (isCrop && portraitOut)
        {
            var crop = RelayConfigGenerator.ComputeCropRect(srcW, srcH);
            double s = 280.0 / crop.CropH;
            resFrame.Width = srcW * s;
            resFrame.Height = srcH * s;
            Canvas.SetLeft(resFrame, -crop.X * s);
            Canvas.SetTop(resFrame, -crop.Y * s);
        }

        if (isBlur)
        {
            double cover = Math.Max(158.0 / srcW, 280.0 / srcH);
            blurBg.Width = srcW * cover;
            blurBg.Height = srcH * cover;
            Canvas.SetLeft(blurBg, (158 - blurBg.Width) / 2);
            Canvas.SetTop(blurBg, (280 - blurBg.Height) / 2);
        }

        if (!isCustom) return;

        bool hasImage = !string.IsNullOrWhiteSpace(dest.CustomBackgroundPath);
        if (hasImage)
        {
            if (_bgImagePath != dest.CustomBackgroundPath) LoadBackgroundImage(dest.CustomBackgroundPath);
            hasImage = _bgImage != null;
        }
        customBgImg.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        customBgBlur.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        customBgShade.Background = hasImage ? ShadeImage : ShadeBlur;
        if (hasImage)
        {
            customBgImg.Width = canvasW;
            customBgImg.Height = canvasH;
        }
        else
        {
            double cover = Math.Max(canvasW / srcW, canvasH / srcH);
            customBgBlur.Width = srcW * cover;
            customBgBlur.Height = srcH * cover;
            Canvas.SetLeft(customBgBlur, (canvasW - customBgBlur.Width) / 2);
            Canvas.SetTop(customBgBlur, (canvasH - customBgBlur.Height) / 2);
        }

        if (layered)
        {
            RenderLayers(dest, srcW, srcH, canvasW, canvasH);
        }
        else if (portraitOut)
        {
            RenderLegacyCustom(dest, srcW, srcH);
        }
        else
        {
            // Landscape custom, no layers yet: full-frame mirror.
            customFg.Width = canvasW;
            customFg.Height = canvasH;
            Canvas.SetLeft(customFg, 0);
            Canvas.SetTop(customFg, 0);
        }
    }

    private void RenderLegacyCustom(DestinationConfig dest, int srcW, int srcH)
    {
        var n = RelayConfigGenerator.NormalizedCustomLayout(dest, srcW, srcH);
        double cropPxH = n.CropH * srcH;
        double fgH = n.FgScale * 280;
        double fgW = fgH * 9.0 / 16.0;
        customFgCanvas.Width = fgW;
        customFgCanvas.Height = fgH;
        Canvas.SetLeft(customFgCanvas, n.FgX * 158);
        Canvas.SetTop(customFgCanvas, n.FgY * 280);
        double s = cropPxH > 0 ? fgH / cropPxH : 1;
        customFg.Width = srcW * s;
        customFg.Height = srcH * s;
        Canvas.SetLeft(customFg, -n.CropX * srcW * s);
        Canvas.SetTop(customFg, -n.CropY * srcH * s);
    }

    // Renders the layer stack exactly as the layered filter graph composites it
    // (blur areas are approximated locally; the server snapshot shows the truth).
    private void RenderLayers(DestinationConfig dest, int srcW, int srcH, double canvasW, double canvasH)
    {
        layerHost.Children.Clear();
        foreach (var layer in dest.Layers)
        {
            double w = Math.Clamp(layer.W, 0.005, 1) * canvasW;
            double h = Math.Clamp(layer.H, 0.005, 1) * canvasH;
            double x = Math.Clamp(layer.X, 0, Math.Max(0, 1 - Math.Clamp(layer.W, 0.005, 1))) * canvasW;
            double y = Math.Clamp(layer.Y, 0, Math.Max(0, 1 - Math.Clamp(layer.H, 0.005, 1))) * canvasH;

            FrameworkElement element;
            switch (layer.Type)
            {
                case LayerType.Source:
                {
                    double sw = Math.Clamp(layer.SrcW, 0.005, 1) * srcW;
                    double sh = Math.Clamp(layer.SrcH, 0.005, 1) * srcH;
                    double sx = Math.Clamp(layer.SrcX, 0, Math.Max(0, 1 - Math.Clamp(layer.SrcW, 0.005, 1))) * srcW;
                    double sy = Math.Clamp(layer.SrcY, 0, Math.Max(0, 1 - Math.Clamp(layer.SrcH, 0.005, 1))) * srcH;
                    var wrapper = new Canvas { Width = w, Height = h, ClipToBounds = true };
                    double s = sh > 0 ? h / sh : 1;
                    var fp = new FramePreview { Source = _mirrorFrame, Width = srcW * s, Height = srcH * s };
                    Canvas.SetLeft(fp, -sx * s);
                    Canvas.SetTop(fp, -sy * s);
                    wrapper.Children.Add(fp);
                    element = wrapper;
                    break;
                }
                case LayerType.Image:
                {
                    var source = LoadCachedImage(layer.Path);
                    if (source is null) continue;
                    element = new Image { Source = source, Stretch = Stretch.Fill, Width = w, Height = h };
                    break;
                }
                case LayerType.Blur:
                {
                    element = new Border
                    {
                        Background = FrostBrush,
                        BorderBrush = Frozen(0x88, 0x9A, 0xA0, 0xA8),
                        BorderThickness = new Thickness(0.5),
                        CornerRadius = new CornerRadius(2),
                    };
                    break;
                }
                case LayerType.BackgroundReveal:
                {
                    if (_bgImage is not null)
                    {
                        var img = new Image
                        {
                            Source = _bgImage,
                            Stretch = Stretch.Fill,
                            Width = canvasW,
                            Height = canvasH,
                        };
                        Canvas.SetLeft(img, 0);
                        Canvas.SetTop(img, 0);
                        img.Clip = new RectangleGeometry(new Rect(x, y, w, h));
                        var revealWrap = new Canvas { Width = canvasW, Height = canvasH };
                        revealWrap.Children.Add(img);
                        element = revealWrap;
                    }
                    else
                    {
                        double cover = Math.Max(canvasW / srcW, canvasH / srcH);
                        var fp = new FramePreview
                        {
                            Source = _mirrorFrame,
                            Width = srcW * cover,
                            Height = srcH * cover,
                            Effect = new BlurEffect { Radius = 40, RenderingBias = RenderingBias.Performance },
                        };
                        Canvas.SetLeft(fp, (canvasW - fp.Width) / 2);
                        Canvas.SetTop(fp, (canvasH - fp.Height) / 2);
                        var wrap = new Canvas { Width = canvasW, Height = canvasH, ClipToBounds = true };
                        wrap.Children.Add(fp);
                        element = wrap;
                    }
                    break;
                }
                default:
                    continue;
            }

            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            layerHost.Children.Add(element);
        }

        if (_selectedLayer is not null && dest.Layers.Contains(_selectedLayer))
        {
            double w = Math.Clamp(_selectedLayer.W, 0.005, 1) * canvasW;
            double h = Math.Clamp(_selectedLayer.H, 0.005, 1) * canvasH;
            double x = Math.Clamp(_selectedLayer.X, 0, Math.Max(0, 1 - Math.Clamp(_selectedLayer.W, 0.005, 1))) * canvasW;
            double y = Math.Clamp(_selectedLayer.Y, 0, Math.Max(0, 1 - Math.Clamp(_selectedLayer.H, 0.005, 1))) * canvasH;
            var outline = new Border
            {
                BorderBrush = SelectBrush,
                BorderThickness = new Thickness(1.5),
                Width = w,
                Height = h,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(outline, x);
            Canvas.SetTop(outline, y);
            layerHost.Children.Add(outline);
        }
    }

    private BitmapImage? LoadCachedImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_imageCache.TryGetValue(path, out var cached)) return cached;
        BitmapImage? image = null;
        try
        {
            if (File.Exists(path))
            {
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
            }
        }
        catch
        {
            image = null;
        }
        _imageCache[path] = image;
        return image;
    }

    private void LoadBackgroundImage(string path)
    {
        _bgImagePath = path;
        _bgImage = LoadCachedImage(path);
    }

    private void SetFrameSource(ImageSource? source)
    {
        _mirrorFrame = source;
        srcFrame.Source = source;
        resFrame.Source = source;
        blurBg.Source = source;
        blurFg.Source = source;
        customFg.Source = source;
        customBgBlur.Source = source;
        if (_loaded) UpdatePreview(); // layered view composites the new frame
    }

    private void LoadBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedDestination is not { PortraitStyle: PortraitStyle.Custom } dest)
        {
            MessageBox.Show("Pick a destination with the Custom layout first.", "Output Studio",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? path = PickImage("Pick a background image (it will be stretched to the output size)");
        if (path is not null) dest.CustomBackgroundPath = path;
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedDestination is { PortraitStyle: PortraitStyle.Custom } dest)
            dest.CustomBackgroundPath = "";
    }

    // ---------- live mirror (auto-playing window/screen capture) ----------

    private DispatcherTimer EnsureLiveTimer()
    {
        if (_liveTimer != null) return _liveTimer;
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _liveTimer.Tick += (_, _) => LiveTick();
        return _liveTimer;
    }

    private void LiveTick()
    {
        // Re-evaluate the capture source every ~5s or when it disappears, so a
        // newly opened OBS window takes over automatically.
        _liveTick++;
        if (_autoHwnd == IntPtr.Zero || !IsWindow(_autoHwnd) || _liveTick % 50 == 0)
            RefreshAutoSource();
        CaptureFrame();
    }

    private void RefreshAutoSource()
    {
        IntPtr previous = _autoHwnd;
        var windows = ListCapturableWindows();
        var obs = windows.FirstOrDefault(w =>
            w.Hwnd != IntPtr.Zero && w.Title.Contains("obs", StringComparison.OrdinalIgnoreCase));
        _autoHwnd = obs?.Hwnd ?? IntPtr.Zero;
        if (_autoHwnd != previous)
        {
            MirrorHint.Text = _autoHwnd != IntPtr.Zero
                ? "Mirror auto-plays while you edit: capturing your OBS window at ~10 fps. The relay never streams video back continuously - the Server option below pulls tiny 2 fps snapshots over SSH instead."
                : "Mirror auto-plays while you edit, but no OBS window was found - capturing your whole screen at ~10 fps. The relay never streams video back continuously - the Server option below pulls tiny 2 fps snapshots over SSH instead.";
        }
    }

    private static List<LiveWindow> ListCapturableWindows()
    {
        var windows = new List<LiveWindow>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
                windows.Add(new LiveWindow(title.ToString(), hwnd));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private void CaptureFrame()
    {
        try
        {
            if (_autoHwnd != IntPtr.Zero && IsWindow(_autoHwnd))
                CaptureWindow(_autoHwnd);
            else
                CaptureScreen();
        }
        catch
        {
            // preview only - a failed capture frame is not fatal
        }
    }

    private void CaptureScreen()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        var bounds = screen.Bounds;
        using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        CommitFrame(bmp);
    }

    private void CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect)) return;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT (2) also captures DirectX-rendered windows (OBS, games).
                if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) return;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        CommitFrame(bmp);
    }

    private void CommitFrame(System.Drawing.Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            SetFrameSource(source);
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    // ---------- true server output (SSH snapshots, ~2 fps) ----------

    private void TestCardServer_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _watchedDestination is null) return;
        ServerWaitingText.Visibility = Visibility.Visible;
        ServerWaitingText.Text = "starting test encoders on the server...";
        try
        {
            var target = _vm.BuildTarget();
            var config = _vm.Config;
            Task.Run(() => RelayPreviewService.StartTestEncoders(target, config))
                .ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result == 0)
                    {
                        ServerWaitingText.Text = "could not start the test encoders (check the SSH user's docker rights)";
                        ServerWaitingText.Visibility = Visibility.Visible;
                    }
                    // frames arrive via the normal poll within a second or two
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex)
        {
            ServerWaitingText.Text = ex.Message;
        }
    }

    private void PreviewSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return; // XAML parse fires this before the control is built
        UpdatePreview();
    }

    private void StartServerPoll(Guid destinationId)
    {
        if (_serverTimer is null)
        {
            _serverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _serverTimer.Tick += (_, _) => PollServerSnapshot();
        }
        _serverTimer.Start();
    }

    private void StopServerPoll() => _serverTimer?.Stop();

    private void PollServerSnapshot()
    {
        if (_vm is null || _watchedDestination is null) return;
        if (Interlocked.CompareExchange(ref _serverBusy, 1, 0) != 0) return;
        var dest = _watchedDestination;
        DeployTarget target;
        try
        {
            target = _vm.BuildTarget();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _serverBusy, 0);
            ServerWaitingText.Text = ex.Message;
            return;
        }
        Task.Run(() => RelayPreviewService.FetchSnapshot(target, dest))
            .ContinueWith(t =>
            {
                Interlocked.Exchange(ref _serverBusy, 0);
                var snapshot = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (snapshot is null || !snapshot.HasFrame)
                {
                    ServerWaitingText.Text = snapshot?.Status ?? "waiting for frames...";
                    ServerWaitingText.Visibility = Visibility.Visible;
                    return;
                }
                try
                {
                    var image = new BitmapImage();
                    using var stream = new MemoryStream(snapshot.Data!);
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    ServerPreviewImage.Source = image;
                    // Always say which mode the frame came from, so a test card is
                    // never mistaken for the live stream.
                    ServerWaitingText.Text = snapshot.IsTestFrame
                        ? "TEST CARD (auto-cleans shortly) - go live or re-click the button to refresh"
                        : "LIVE - this is what the platform is receiving";
                    ServerWaitingText.Visibility = Visibility.Visible;
                }
                catch
                {
                    // torn snapshot (read mid-write) - the next poll fixes it
                    ServerWaitingText.Text = "snapshot was torn mid-write - retrying...";
                    ServerWaitingText.Visibility = Visibility.Visible;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------- drag on the previews ----------

    private (double X, double Y) SourceNormalized(Point pos)
    {
        double left = (SourceBox.ActualWidth - srcFrame.Width) / 2;
        double top = (SourceBox.ActualHeight - srcFrame.Height) / 2;
        if (srcFrame.Width <= 0 || srcFrame.Height <= 0) return (0, 0);
        return ((pos.X - left) / srcFrame.Width, (pos.Y - top) / srcFrame.Height);
    }

    private OutputLayer? HitTestLayer(Point normalized)
    {
        var dest = _watchedDestination;
        if (dest is null) return null;
        for (int i = dest.Layers.Count - 1; i >= 0; i--)
        {
            var l = dest.Layers[i];
            double w = Math.Clamp(l.W, 0.005, 1);
            double h = Math.Clamp(l.H, 0.005, 1);
            double x = Math.Clamp(l.X, 0, Math.Max(0, 1 - w));
            double y = Math.Clamp(l.Y, 0, Math.Max(0, 1 - h));
            if (normalized.X >= x && normalized.X <= x + w && normalized.Y >= y && normalized.Y <= y + h)
                return l;
        }
        return null;
    }

    private void SourceCrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedDestination is not { PortraitStyle: PortraitStyle.Custom } dest) return;
        var (nx, ny) = SourceNormalized(e.GetPosition(SourceBox));

        // Layered: with a Source cut selected, dragging here moves its source area.
        if (dest.Layers.Count > 0)
        {
            if (_selectedLayer is { Type: LayerType.Source } src)
            {
                _cropDragging = true;
                _cropDragOffset = new Point(nx - src.SrcX, ny - src.SrcY);
                SourceBox.CaptureMouse();
                e.Handled = true;
            }
            return;
        }

        var n = NormalizedLayoutFor(dest);
        if (nx < n.CropX - 0.01 || nx > n.CropX + n.CropW + 0.01 || ny < n.CropY - 0.01 || ny > n.CropY + n.CropH + 0.01)
            return;
        _cropDragging = true;
        _cropDragOffset = new Point(nx - n.CropX, ny - n.CropY);
        SourceBox.CaptureMouse();
        e.Handled = true;
    }

    private void SourceCrop_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging || _vm?.SelectedDestination is not { PortraitStyle: PortraitStyle.Custom } dest) return;
        var (nx, ny) = SourceNormalized(e.GetPosition(SourceBox));
        _updatingLayerProps = true;
        if (dest.Layers.Count > 0 && _selectedLayer is { Type: LayerType.Source } src)
        {
            src.SrcX = Math.Clamp(nx - _cropDragOffset.X, 0, Math.Max(0, 1 - src.SrcW));
            src.SrcY = Math.Clamp(ny - _cropDragOffset.Y, 0, Math.Max(0, 1 - src.SrcH));
        }
        else
        {
            var n = NormalizedLayoutFor(dest);
            dest.CropX = Math.Clamp(nx - _cropDragOffset.X, 0, Math.Max(0, 1 - n.CropW));
            dest.CropY = Math.Clamp(ny - _cropDragOffset.Y, 0, Math.Max(0, 1 - n.CropH));
        }
        UpdateLayerProps();
        _updatingLayerProps = false;
        e.Handled = true;
    }

    private void SourceCrop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging) return;
        _cropDragging = false;
        SourceBox.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CustomResult_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedDestination is not { PortraitStyle: PortraitStyle.Custom } dest) return;
        var pos = e.GetPosition(CustomResultBox);
        if (CustomResultBox.ActualWidth <= 0 || CustomResultBox.ActualHeight <= 0) return;
        double nx = pos.X / CustomResultBox.ActualWidth;
        double ny = pos.Y / CustomResultBox.ActualHeight;

        if (dest.Layers.Count > 0)
        {
            var hit = HitTestLayer(new Point(nx, ny));
            SelectLayer(hit);
            if (hit is null) return;
            _layerDrag = hit;
            _layerDragOffset = new Point(nx - hit.X, ny - hit.Y);
            CustomResultBox.CaptureMouse();
            e.Handled = true;
            return;
        }

        var n = NormalizedLayoutFor(dest);
        if (nx < n.FgX || nx > n.FgX + n.FgScale * 9.0 / 16.0 || ny < n.FgY || ny > n.FgY + n.FgScale)
            return;
        _fgDragging = true;
        _fgDragOffset = new Point(nx - n.FgX, ny - n.FgY);
        CustomResultBox.CaptureMouse();
        e.Handled = true;
    }

    private void CustomResult_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.SelectedDestination is not { PortraitStyle: PortraitStyle.Custom } dest) return;
        if (CustomResultBox.ActualWidth <= 0 || CustomResultBox.ActualHeight <= 0) return;
        var pos = e.GetPosition(CustomResultBox);
        double nx = pos.X / CustomResultBox.ActualWidth;
        double ny = pos.Y / CustomResultBox.ActualHeight;

        if (_layerDrag is not null)
        {
            _layerDrag.X = Math.Clamp(nx - _layerDragOffset.X, 0, Math.Max(0, 1 - _layerDrag.W));
            _layerDrag.Y = Math.Clamp(ny - _layerDragOffset.Y, 0, Math.Max(0, 1 - _layerDrag.H));
            e.Handled = true;
            return;
        }
        if (!_fgDragging) return;
        var n = NormalizedLayoutFor(dest);
        dest.FgX = Math.Clamp(nx - _fgDragOffset.X, 0, Math.Max(0, 1 - n.FgScale * 9.0 / 16.0));
        dest.FgY = Math.Clamp(ny - _fgDragOffset.Y, 0, Math.Max(0, 1 - n.FgScale));
        e.Handled = true;
    }

    private void CustomResult_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_layerDrag is not null)
        {
            _layerDrag = null;
            CustomResultBox.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_fgDragging) return;
        _fgDragging = false;
        CustomResultBox.ReleaseMouseCapture();
        e.Handled = true;
    }

    private (double CropX, double CropY, double CropW, double CropH, double FgX, double FgY, double FgScale) NormalizedLayoutFor(DestinationConfig dest)
    {
        var up = _vm?.Config.Upstream;
        return RelayConfigGenerator.NormalizedCustomLayout(dest,
            up is null ? 1920 : Math.Max(2, up.Width),
            up is null ? 1080 : Math.Max(2, up.Height));
    }

    // ---------- pipeline card drag to reorder ----------

    private void CardsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = FindListBoxItem(e.OriginalSource as DependencyObject)?.Content as DestinationConfig;
        _dragStartPoint = e.GetPosition(null);
    }

    private void CardsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(CardsList, new DataObject("dest", item), DragDropEffects.Move);
    }

    private void CardsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("dest") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void CardsList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm is null || e.Data.GetData("dest") is not DestinationConfig src) return;

        var hitItem = FindListBoxItem(e.OriginalSource as DependencyObject);
        int target;
        if (hitItem?.Content is DestinationConfig over && !ReferenceEquals(over, src))
        {
            int overIndex = _vm.Destinations.IndexOf(over);
            var pos = e.GetPosition(hitItem);
            target = pos.X > hitItem.ActualWidth / 2 ? overIndex + 1 : overIndex;
        }
        else
        {
            target = _vm.Destinations.Count;
        }
        _vm.MoveDestinationTo(src, target);
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? start)
    {
        var d = start;
        while (d != null && d is not ListBoxItem)
            d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void PreviewBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---------- win32 ----------

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
