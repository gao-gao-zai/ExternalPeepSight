using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExternalPeepSight.Core;
using ExternalPeepSight.UI.Services;

namespace ExternalPeepSight.UI.ViewModels;

internal sealed class LocalizedOption<T> : ObservableObject
    where T : struct
{
    private readonly LocalizationService localization;
    private readonly string resourceKey;

    public LocalizedOption(T value, string resourceKey, LocalizationService localization)
    {
        Value = value;
        this.resourceKey = resourceKey;
        this.localization = localization;
        localization.CultureChanged += OnCultureChanged;
    }

    public T Value { get; }

    public string Name => localization[resourceKey];

    private void OnCultureChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Name));
}

internal abstract class WorkspaceViewModel : ObservableObject
{
    protected WorkspaceViewModel(ConfigurationWorkspace workspace)
    {
        Workspace = workspace;
        workspace.DocumentChanged += OnWorkspaceChanged;
    }

    protected ConfigurationWorkspace Workspace { get; }

    protected Profile Profile => Workspace.SelectedProfile;

    protected abstract void Refresh();

    private void OnWorkspaceChanged(object? sender, EventArgs e) => Refresh();
}

internal sealed class NavigationItemViewModel : ObservableObject
{
    private readonly LocalizationService localization;
    private readonly string resourceKey;

    public NavigationItemViewModel(
        string resourceKey,
        string iconGlyph,
        object content,
        LocalizationService localization)
    {
        this.resourceKey = resourceKey;
        IconGlyph = iconGlyph;
        Content = content;
        this.localization = localization;
        localization.CultureChanged += OnCultureChanged;
    }

    public string Name => localization[resourceKey];

    public string IconGlyph { get; }

    public object Content { get; }

    private void OnCultureChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Name));
}

internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationWorkspace workspace;
    private readonly LocalizationService localization;
    private NavigationItemViewModel selectedNavigation;

    public MainWindowViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IFileDialogService fileDialogs,
        IMonitorEnumerationService monitorEnumeration,
        ThemeService themeService,
        UiPreferencesStore preferencesStore,
        UiPreferences preferences)
    {
        this.workspace = workspace;
        this.localization = localization;
        Crosshair = new CrosshairEditorViewModel(workspace, localization, fileDialogs);
        Image = Crosshair.Image;
        Monitors = new MonitorSelectionViewModel(workspace, localization, monitorEnumeration);
        Switches = Crosshair.Switches;
        Profiles = new ProfileSetsViewModel(workspace, localization);
        Transfer = new ImportExportViewModel(workspace, localization, fileDialogs);
        Settings = new SettingsViewModel(
            workspace,
            localization,
            themeService,
            preferencesStore,
            preferences);
        Navigation =
        [
            new("Navigation.Crosshair", "\uE790", Crosshair, localization),
            new("Navigation.Monitors", "\uE7F4", Monitors, localization),
            new("Navigation.Profiles", "\uE8A5", Profiles, localization),
            new("Navigation.Transfer", "\uE8B5", Transfer, localization),
            new("Navigation.Settings", "\uE713", Settings, localization),
        ];
        selectedNavigation = Navigation[0];
        workspace.DocumentChanged += OnWorkspaceChanged;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        localization.CultureChanged += OnCultureChanged;
        DismissErrorCommand = new RelayCommand(workspace.DismissError);
    }

    public IReadOnlyList<NavigationItemViewModel> Navigation { get; }

    public NavigationItemViewModel SelectedNavigation
    {
        get => selectedNavigation;
        set => SetProperty(ref selectedNavigation, value);
    }

    public IReadOnlyList<Profile> AvailableProfiles => workspace.Document.Profiles;

    public Profile SelectedProfile
    {
        get => workspace.SelectedProfile;
        set
        {
            if (value.Id != workspace.SelectedProfileId)
            {
                workspace.SelectProfile(value.Id);
            }
        }
    }

    public bool IsHostConnected => workspace.IsHostConnected;

    public string HostStatus => localization[
        workspace.IsHostConnected ? "App.HostConnected" : "App.HostConnecting"];

    public string? ErrorMessage => workspace.ErrorMessage;

    public bool HasError => !string.IsNullOrEmpty(workspace.ErrorMessage);

    public CrosshairEditorViewModel Crosshair { get; }

    public ImageEditorViewModel Image { get; }

    public MonitorSelectionViewModel Monitors { get; }

    public SwitchesViewModel Switches { get; }

    public ProfileSetsViewModel Profiles { get; }

    public ImportExportViewModel Transfer { get; }

    public SettingsViewModel Settings { get; }

    public IRelayCommand DismissErrorCommand { get; }

    public void Dispose()
    {
        workspace.DocumentChanged -= OnWorkspaceChanged;
        workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        localization.CultureChanged -= OnCultureChanged;
        Crosshair.Dispose();
        workspace.Dispose();
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AvailableProfiles));
        OnPropertyChanged(nameof(SelectedProfile));
    }

    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationWorkspace.IsHostConnected))
        {
            OnPropertyChanged(nameof(IsHostConnected));
            OnPropertyChanged(nameof(HostStatus));
        }
        else if (e.PropertyName == nameof(ConfigurationWorkspace.ErrorMessage))
        {
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(HostStatus));
}

internal enum CrosshairEditorTab
{
    Overlay,
    Switches,
}

internal sealed class CrosshairEditorViewModel : WorkspaceViewModel, IDisposable
{
    private int selectedArmIndex;
    private CrosshairEditorTab selectedTab;

    public CrosshairEditorViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IFileDialogService fileDialogs)
        : base(workspace)
    {
        Image = new ImageEditorViewModel(workspace, localization, fileDialogs);
        Switches = new SwitchesViewModel(workspace, localization);
        Modes =
        [
            new(OverlayMode.Crosshair, "Common.Crosshair", localization),
            new(OverlayMode.Image, "Common.Image", localization),
        ];
        Anchors =
        [
            new(AnchorMode.ScreenCenter, "Common.ScreenCenter", localization),
            new(AnchorMode.TopLeft, "Common.TopLeft", localization),
        ];
        Arms =
        [
            new(0, "Crosshair.ArmTop", localization),
            new(1, "Crosshair.ArmRight", localization),
            new(2, "Crosshair.ArmBottom", localization),
            new(3, "Crosshair.ArmLeft", localization),
        ];
    }

    public IReadOnlyList<LocalizedOption<OverlayMode>> Modes { get; }

    public IReadOnlyList<LocalizedOption<AnchorMode>> Anchors { get; }

    public IReadOnlyList<LocalizedOption<int>> Arms { get; }

    public ImageEditorViewModel Image { get; }

    public SwitchesViewModel Switches { get; }

    public CrosshairEditorTab SelectedTab
    {
        get => selectedTab;
        set
        {
            if (SetProperty(ref selectedTab, value))
            {
                OnPropertyChanged(nameof(SelectedTabIndex));
                OnPropertyChanged(nameof(IsOverlayTab));
                OnPropertyChanged(nameof(IsSwitchesTab));
            }
        }
    }

    public int SelectedTabIndex
    {
        get => (int)SelectedTab;
        set
        {
            if (Enum.IsDefined((CrosshairEditorTab)value))
            {
                SelectedTab = (CrosshairEditorTab)value;
            }
        }
    }

    public bool IsOverlayTab
    {
        get => SelectedTab == CrosshairEditorTab.Overlay;
        set
        {
            if (value)
            {
                SelectedTab = CrosshairEditorTab.Overlay;
            }
        }
    }

    public bool IsSwitchesTab
    {
        get => SelectedTab == CrosshairEditorTab.Switches;
        set
        {
            if (value)
            {
                SelectedTab = CrosshairEditorTab.Switches;
            }
        }
    }

    public OverlayMode ActiveMode
    {
        get => Profile.ActiveMode;
        set => Workspace.UpdateSelectedProfile(profile => profile with { ActiveMode = value });
    }

    public bool IsCrosshairMode
    {
        get => ActiveMode == OverlayMode.Crosshair;
        set
        {
            if (value)
            {
                ActiveMode = OverlayMode.Crosshair;
            }
        }
    }

    public bool IsImageMode
    {
        get => ActiveMode == OverlayMode.Image;
        set
        {
            if (value)
            {
                ActiveMode = OverlayMode.Image;
            }
        }
    }

    public bool CanActivateImage =>
        Profile.Image.AssetId.HasValue &&
        Workspace.Document.Assets.Any(asset => asset.Id == Profile.Image.AssetId.Value);

    public AnchorMode Anchor
    {
        get => Profile.Crosshair.Anchor;
        set => UpdateCrosshair(crosshair => crosshair with { Anchor = value });
    }

    public bool IsScreenCenterAnchor
    {
        get => Anchor == AnchorMode.ScreenCenter;
        set
        {
            if (value)
            {
                Anchor = AnchorMode.ScreenCenter;
            }
        }
    }

    public bool IsTopLeftAnchor
    {
        get => Anchor == AnchorMode.TopLeft;
        set
        {
            if (value)
            {
                Anchor = AnchorMode.TopLeft;
            }
        }
    }

    public int OffsetX
    {
        get => Profile.Crosshair.OffsetPx.X;
        set => UpdateCrosshair(crosshair => crosshair with
        {
            OffsetPx = crosshair.OffsetPx with { X = value },
        });
    }

    public int OffsetY
    {
        get => Profile.Crosshair.OffsetPx.Y;
        set => UpdateCrosshair(crosshair => crosshair with
        {
            OffsetPx = crosshair.OffsetPx with { Y = value },
        });
    }

    public bool CenterVisible
    {
        get => Profile.Crosshair.Center.Visible;
        set => UpdateCrosshair(crosshair => crosshair with
        {
            Center = crosshair.Center with { Visible = value },
        });
    }

    public int CenterRadius
    {
        get => Profile.Crosshair.Center.RadiusPx;
        set => UpdateCrosshair(crosshair => crosshair with
        {
            Center = crosshair.Center with { RadiusPx = value },
        });
    }

    public Color CenterColor
    {
        get => ToColor(Profile.Crosshair.Center.Color);
        set => UpdateCrosshair(crosshair => crosshair with
        {
            Center = crosshair.Center with { Color = ToRgba(value) },
        });
    }

    public string CenterHex
    {
        get => Profile.Crosshair.Center.Color.ToString();
        set => UpdateColor(value, color => UpdateCrosshair(crosshair => crosshair with
        {
            Center = crosshair.Center with { Color = color },
        }));
    }

    public bool Linked
    {
        get => Profile.Crosshair.Linked;
        set => UpdateCrosshair(crosshair => crosshair with { Linked = value });
    }

    public int SelectedArmIndex
    {
        get => selectedArmIndex;
        set
        {
            if (SetProperty(ref selectedArmIndex, value))
            {
                NotifyArmSelection();
                RefreshArm();
            }
        }
    }

    public bool IsTopArmSelected
    {
        get => SelectedArmIndex == 0;
        set
        {
            if (value)
            {
                SelectedArmIndex = 0;
            }
        }
    }

    public bool IsRightArmSelected
    {
        get => SelectedArmIndex == 1;
        set
        {
            if (value)
            {
                SelectedArmIndex = 1;
            }
        }
    }

    public bool IsBottomArmSelected
    {
        get => SelectedArmIndex == 2;
        set
        {
            if (value)
            {
                SelectedArmIndex = 2;
            }
        }
    }

    public bool IsLeftArmSelected
    {
        get => SelectedArmIndex == 3;
        set
        {
            if (value)
            {
                SelectedArmIndex = 3;
            }
        }
    }

    public bool ArmVisible
    {
        get => SelectedArm.Visible;
        set => UpdateArm(arm => arm with { Visible = value }, applyWhenLinked: true);
    }

    public double ArmOrbitAngleOffset
    {
        get => SelectedArm.OrbitAngleOffsetDeg;
        set => UpdateArm(arm => arm with { OrbitAngleOffsetDeg = value }, applyWhenLinked: true);
    }

    public double ArmRotationAngleOffset
    {
        get => SelectedArm.RotationAngleOffsetDeg;
        set => UpdateArm(arm => arm with { RotationAngleOffsetDeg = value }, applyWhenLinked: true);
    }

    public int ArmGap
    {
        get => SelectedArm.GapPx;
        set => UpdateArm(arm => arm with { GapPx = value }, applyWhenLinked: true);
    }

    public int ArmLength
    {
        get => SelectedArm.LengthPx;
        set => UpdateArm(arm => arm with { LengthPx = value }, applyWhenLinked: true);
    }

    public int ArmWidth
    {
        get => SelectedArm.WidthPx;
        set => UpdateArm(arm => arm with { WidthPx = value }, applyWhenLinked: true);
    }

    public Color ArmColor
    {
        get => ToColor(SelectedArm.Color);
        set => UpdateArm(arm => arm with { Color = ToRgba(value) }, applyWhenLinked: true);
    }

    public string ArmHex
    {
        get => SelectedArm.Color.ToString();
        set => UpdateColor(
            value,
            color => UpdateArm(arm => arm with { Color = color }, applyWhenLinked: true));
    }

    public Crosshair Preview => Profile.Crosshair;

    public void Dispose() => Image.Dispose();

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(ActiveMode));
        OnPropertyChanged(nameof(IsCrosshairMode));
        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(CanActivateImage));
        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(IsScreenCenterAnchor));
        OnPropertyChanged(nameof(IsTopLeftAnchor));
        OnPropertyChanged(nameof(OffsetX));
        OnPropertyChanged(nameof(OffsetY));
        OnPropertyChanged(nameof(CenterVisible));
        OnPropertyChanged(nameof(CenterRadius));
        OnPropertyChanged(nameof(CenterColor));
        OnPropertyChanged(nameof(CenterHex));
        OnPropertyChanged(nameof(Linked));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsOverlayTab));
        OnPropertyChanged(nameof(IsSwitchesTab));
        RefreshArm();
    }

    private Arm SelectedArm => Profile.Crosshair.Arms[SelectedArmIndex];

    private void UpdateCrosshair(Func<Crosshair, Crosshair> update) =>
        Workspace.UpdateSelectedProfile(profile => profile with
        {
            Crosshair = update(profile.Crosshair),
        });

    private void UpdateArm(Func<Arm, Arm> update, bool applyWhenLinked)
    {
        UpdateCrosshair(crosshair => crosshair with
        {
            Arms = crosshair.Arms.Select((arm, index) =>
                index == SelectedArmIndex || (crosshair.Linked && applyWhenLinked)
                    ? update(arm)
                    : arm).ToArray(),
        });
    }

    private void RefreshArm()
    {
        OnPropertyChanged(nameof(ArmVisible));
        OnPropertyChanged(nameof(ArmOrbitAngleOffset));
        OnPropertyChanged(nameof(ArmRotationAngleOffset));
        OnPropertyChanged(nameof(ArmGap));
        OnPropertyChanged(nameof(ArmLength));
        OnPropertyChanged(nameof(ArmWidth));
        OnPropertyChanged(nameof(ArmColor));
        OnPropertyChanged(nameof(ArmHex));
    }

    private void NotifyArmSelection()
    {
        OnPropertyChanged(nameof(IsTopArmSelected));
        OnPropertyChanged(nameof(IsRightArmSelected));
        OnPropertyChanged(nameof(IsBottomArmSelected));
        OnPropertyChanged(nameof(IsLeftArmSelected));
    }

    private static void UpdateColor(string value, Action<RgbaColor> update)
    {
        try
        {
            update(RgbaColor.Parse(value));
        }
        catch (FormatException)
        {
        }
    }

    private static Color ToColor(RgbaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static RgbaColor ToRgba(Color color) => new(color.R, color.G, color.B, color.A);
}

internal sealed class ImageEditorViewModel : WorkspaceViewModel, IDisposable
{
    private readonly LocalizationService localization;
    private readonly IFileDialogService fileDialogs;
    private Bitmap? previewBitmap;

    public ImageEditorViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IFileDialogService fileDialogs)
        : base(workspace)
    {
        this.localization = localization;
        this.fileDialogs = fileDialogs;
        Modes =
        [
            new(OverlayMode.Crosshair, "Common.Crosshair", localization),
            new(OverlayMode.Image, "Common.Image", localization),
        ];
        Anchors =
        [
            new(AnchorMode.ScreenCenter, "Common.ScreenCenter", localization),
            new(AnchorMode.TopLeft, "Common.TopLeft", localization),
        ];
        ImportImageCommand = new AsyncRelayCommand(ImportImageAsync);
        RefreshPreview();
    }

    public IReadOnlyList<LocalizedOption<OverlayMode>> Modes { get; }

    public IReadOnlyList<LocalizedOption<AnchorMode>> Anchors { get; }

    public IReadOnlyList<AssetReference> Assets => Workspace.Document.Assets;

    public OverlayMode ActiveMode
    {
        get => Profile.ActiveMode;
        set
        {
            if (value != OverlayMode.Image || Profile.Image.AssetId.HasValue)
            {
                Workspace.UpdateSelectedProfile(profile => profile with { ActiveMode = value });
            }
        }
    }

    public bool IsCrosshairMode
    {
        get => ActiveMode == OverlayMode.Crosshair;
        set
        {
            if (value)
            {
                ActiveMode = OverlayMode.Crosshair;
            }
        }
    }

    public bool IsImageMode
    {
        get => ActiveMode == OverlayMode.Image;
        set
        {
            if (value)
            {
                ActiveMode = OverlayMode.Image;
            }
        }
    }

    public bool CanActivateImage =>
        Profile.Image.AssetId.HasValue &&
        Workspace.Document.Assets.Any(asset => asset.Id == Profile.Image.AssetId.Value);

    public AssetReference? SelectedAsset
    {
        get => Assets.FirstOrDefault(asset => asset.Id == Profile.Image.AssetId);
        set
        {
            if (value is null)
            {
                return;
            }

            Workspace.UpdateSelectedProfile(profile => profile with
            {
                ActiveMode = OverlayMode.Image,
                Image = profile.Image with { AssetId = value.Id },
            });
        }
    }

    public AnchorMode Anchor
    {
        get => Profile.Image.Anchor;
        set => UpdateImage(image => image with { Anchor = value });
    }

    public bool IsScreenCenterAnchor
    {
        get => Anchor == AnchorMode.ScreenCenter;
        set
        {
            if (value)
            {
                Anchor = AnchorMode.ScreenCenter;
            }
        }
    }

    public bool IsTopLeftAnchor
    {
        get => Anchor == AnchorMode.TopLeft;
        set
        {
            if (value)
            {
                Anchor = AnchorMode.TopLeft;
            }
        }
    }

    public int OffsetX
    {
        get => Profile.Image.OffsetPx.X;
        set => UpdateImage(image => image with { OffsetPx = image.OffsetPx with { X = value } });
    }

    public int OffsetY
    {
        get => Profile.Image.OffsetPx.Y;
        set => UpdateImage(image => image with { OffsetPx = image.OffsetPx with { Y = value } });
    }

    public double Scale
    {
        get => Profile.Image.Scale;
        set => UpdateImage(image => image with { Scale = value });
    }

    public bool KeepAspectRatio
    {
        get => Profile.Image.KeepAspectRatio;
        set => UpdateImage(image => image with { KeepAspectRatio = value });
    }

    public Bitmap? PreviewBitmap
    {
        get => previewBitmap;
        private set => SetProperty(ref previewBitmap, value);
    }

    public bool HasRasterPreview => PreviewBitmap is not null;

    public bool HasAsset => SelectedAsset is not null;

    public string AssetDetails => SelectedAsset is null
        ? localization["Image.NoAsset"]
        : $"{SelectedAsset.FileName}  {SelectedAsset.SizeBytes / 1024d:F1} KB";

    public IAsyncRelayCommand ImportImageCommand { get; }

    public void Dispose()
    {
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
    }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(Assets));
        OnPropertyChanged(nameof(SelectedAsset));
        OnPropertyChanged(nameof(ActiveMode));
        OnPropertyChanged(nameof(IsCrosshairMode));
        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(CanActivateImage));
        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(IsScreenCenterAnchor));
        OnPropertyChanged(nameof(IsTopLeftAnchor));
        OnPropertyChanged(nameof(OffsetX));
        OnPropertyChanged(nameof(OffsetY));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(KeepAspectRatio));
        OnPropertyChanged(nameof(HasAsset));
        OnPropertyChanged(nameof(AssetDetails));
        RefreshPreview();
    }

    private async Task ImportImageAsync()
    {
        string? path = await fileDialogs.OpenImageAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            AssetReference reference = Workspace.ImportImage(path);
            SelectedAsset = reference;
        }
        catch (Exception exception) when (
            exception is IOException or ConfigurationFormatException or ConfigurationValidationException)
        {
            Workspace.ReportError("Image.Invalid");
        }
    }

    private void UpdateImage(Func<ImageOverlay, ImageOverlay> update) =>
        Workspace.UpdateSelectedProfile(profile => profile with { Image = update(profile.Image) });

    private void RefreshPreview()
    {
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        AssetReference? asset = SelectedAsset;
        if (asset?.MediaType == "image/png")
        {
            string path = Path.Combine(Workspace.AssetsRoot, asset.FileName);
            if (File.Exists(path))
            {
                try
                {
                    PreviewBitmap = new Bitmap(path);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException)
                {
                }
            }
        }

        OnPropertyChanged(nameof(HasRasterPreview));
    }
}

internal sealed class MonitorItemViewModel : ObservableObject
{
    private readonly Action<MonitorItemViewModel, bool> selectionChanged;
    private bool isSelected;

    public MonitorItemViewModel(
        MonitorInfo monitor,
        bool isSelected,
        Action<MonitorItemViewModel, bool> selectionChanged)
    {
        Monitor = monitor;
        this.isSelected = isSelected;
        this.selectionChanged = selectionChanged;
    }

    public MonitorInfo Monitor { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                selectionChanged(this, value);
            }
        }
    }

    public string Bounds => $"{Monitor.Width} x {Monitor.Height}  ({Monitor.X}, {Monitor.Y})";
}

internal sealed class MonitorSelectionViewModel : WorkspaceViewModel
{
    private readonly IMonitorEnumerationService monitorEnumeration;

    public MonitorSelectionViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IMonitorEnumerationService monitorEnumeration)
        : base(workspace)
    {
        this.monitorEnumeration = monitorEnumeration;
        Modes =
        [
            new(MonitorSelectionMode.All, "Monitor.All", localization),
            new(MonitorSelectionMode.Explicit, "Monitor.Explicit", localization),
            new(MonitorSelectionMode.Focus, "Monitor.Focus", localization),
        ];
        FocusSources =
        [
            new(FocusMonitorSource.ForegroundWindowThenMouse, "Monitor.ForegroundThenMouse", localization),
            new(FocusMonitorSource.Mouse, "Monitor.Mouse", localization),
        ];
        RefreshCommand = new RelayCommand(RefreshMonitors);
        RefreshMonitors();
    }

    public IReadOnlyList<LocalizedOption<MonitorSelectionMode>> Modes { get; }

    public IReadOnlyList<LocalizedOption<FocusMonitorSource>> FocusSources { get; }

    public ObservableCollection<MonitorItemViewModel> Monitors { get; } = [];

    public bool HasMonitors => Monitors.Count > 0;

    public MonitorSelectionMode Mode
    {
        get => Workspace.Document.MonitorSelection.Mode;
        set
        {
            string[] ids = value == MonitorSelectionMode.Explicit
                ? SelectedOrFirstMonitorIds()
                : Workspace.Document.MonitorSelection.MonitorIds;
            Workspace.UpdateDocument(document => document with
            {
                MonitorSelection = document.MonitorSelection with { Mode = value, MonitorIds = ids },
            });
        }
    }

    public FocusMonitorSource FocusSource
    {
        get => Workspace.Document.MonitorSelection.FocusSource;
        set => Workspace.UpdateDocument(document => document with
        {
            MonitorSelection = document.MonitorSelection with { FocusSource = value },
        });
    }

    public bool IsExplicit => Mode == MonitorSelectionMode.Explicit;

    public bool IsFocus => Mode == MonitorSelectionMode.Focus;

    public bool IsAllMode
    {
        get => Mode == MonitorSelectionMode.All;
        set
        {
            if (value)
            {
                Mode = MonitorSelectionMode.All;
            }
        }
    }

    public bool IsExplicitMode
    {
        get => IsExplicit;
        set
        {
            if (value)
            {
                Mode = MonitorSelectionMode.Explicit;
            }
        }
    }

    public bool IsFocusMode
    {
        get => IsFocus;
        set
        {
            if (value)
            {
                Mode = MonitorSelectionMode.Focus;
            }
        }
    }

    public IRelayCommand RefreshCommand { get; }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(FocusSource));
        OnPropertyChanged(nameof(IsExplicit));
        OnPropertyChanged(nameof(IsFocus));
        OnPropertyChanged(nameof(IsAllMode));
        OnPropertyChanged(nameof(IsExplicitMode));
        OnPropertyChanged(nameof(IsFocusMode));
        OnPropertyChanged(nameof(HasMonitors));
        foreach (MonitorItemViewModel monitor in Monitors)
        {
            monitor.IsSelected = Workspace.Document.MonitorSelection.MonitorIds.Contains(monitor.Monitor.Id);
        }
    }

    private void RefreshMonitors()
    {
        string[] selectedIds = Workspace.Document.MonitorSelection.MonitorIds;
        Monitors.Clear();
        foreach (MonitorInfo monitor in monitorEnumeration.Enumerate())
        {
            Monitors.Add(new MonitorItemViewModel(
                monitor,
                selectedIds.Contains(monitor.Id),
                OnMonitorSelectionChanged));
        }

        OnPropertyChanged(nameof(HasMonitors));
    }

    private void OnMonitorSelectionChanged(MonitorItemViewModel item, bool selected)
    {
        if (Mode != MonitorSelectionMode.Explicit)
        {
            return;
        }

        string[] ids = selected
            ? Workspace.Document.MonitorSelection.MonitorIds.Append(item.Monitor.Id).Distinct().ToArray()
            : Workspace.Document.MonitorSelection.MonitorIds.Where(id => id != item.Monitor.Id).ToArray();
        if (ids.Length == 0)
        {
            item.IsSelected = true;
            return;
        }

        Workspace.UpdateDocument(document => document with
        {
            MonitorSelection = document.MonitorSelection with { MonitorIds = ids },
        });
    }

    private string[] SelectedOrFirstMonitorIds()
    {
        string[] selected = Monitors.Where(monitor => monitor.IsSelected)
            .Select(monitor => monitor.Monitor.Id)
            .ToArray();
        return selected.Length > 0
            ? selected
            : Monitors.Take(1).Select(monitor => monitor.Monitor.Id).ToArray();
    }
}

internal sealed class HotkeyEditorViewModel : ObservableObject
{
    private readonly ConfigurationWorkspace workspace;
    private readonly LogicalSwitch logicalSwitch;
    private HotkeyActivationMode mode;
    private KeyIdentity? toggleKey;
    private KeyIdentity? enableKey;
    private KeyIdentity? disableKey;
    private KeyIdentity? holdKey;
    private bool hasConflict;

    public HotkeyEditorViewModel(
        ConfigurationWorkspace workspace,
        LogicalSwitch logicalSwitch,
        LocalizationService localization)
    {
        this.workspace = workspace;
        this.logicalSwitch = logicalSwitch;
        Modes =
        [
            new(HotkeyActivationMode.Unbound, "Hotkey.Unbound", localization),
            new(HotkeyActivationMode.Toggle, "Hotkey.Toggle", localization),
            new(HotkeyActivationMode.Independent, "Hotkey.Independent", localization),
            new(HotkeyActivationMode.Hold, "Hotkey.Hold", localization),
        ];
        ClearCommand = new RelayCommand(Clear);
        Load(CurrentBinding);
        workspace.DocumentChanged += OnDocumentChanged;
    }

    public IReadOnlyList<LocalizedOption<HotkeyActivationMode>> Modes { get; }

    public HotkeyActivationMode Mode
    {
        get => mode;
        set
        {
            if (SetProperty(ref mode, value))
            {
                HasConflict = false;
                OnPropertyChanged(nameof(IsUnbound));
                OnPropertyChanged(nameof(IsToggle));
                OnPropertyChanged(nameof(IsIndependent));
                OnPropertyChanged(nameof(IsHold));
                OnPropertyChanged(nameof(IsToggleMode));
                OnPropertyChanged(nameof(IsIndependentMode));
                OnPropertyChanged(nameof(IsHoldMode));
                TryCommit();
            }
        }
    }

    public KeyIdentity? ToggleKey
    {
        get => toggleKey;
        set
        {
            if (SetProperty(ref toggleKey, value))
            {
                TryCommit();
            }
        }
    }

    public KeyIdentity? EnableKey
    {
        get => enableKey;
        set
        {
            if (SetProperty(ref enableKey, value))
            {
                TryCommit();
            }
        }
    }

    public KeyIdentity? DisableKey
    {
        get => disableKey;
        set
        {
            if (SetProperty(ref disableKey, value))
            {
                TryCommit();
            }
        }
    }

    public KeyIdentity? HoldKey
    {
        get => holdKey;
        set
        {
            if (SetProperty(ref holdKey, value))
            {
                TryCommit();
            }
        }
    }

    public bool IsToggle => Mode == HotkeyActivationMode.Toggle;

    public bool IsIndependent => Mode == HotkeyActivationMode.Independent;

    public bool IsHold => Mode == HotkeyActivationMode.Hold;

    public bool IsUnbound
    {
        get => Mode == HotkeyActivationMode.Unbound;
        set
        {
            if (value)
            {
                Mode = HotkeyActivationMode.Unbound;
            }
        }
    }

    public bool IsToggleMode
    {
        get => IsToggle;
        set
        {
            if (value)
            {
                Mode = HotkeyActivationMode.Toggle;
            }
        }
    }

    public bool IsIndependentMode
    {
        get => IsIndependent;
        set
        {
            if (value)
            {
                Mode = HotkeyActivationMode.Independent;
            }
        }
    }

    public bool IsHoldMode
    {
        get => IsHold;
        set
        {
            if (value)
            {
                Mode = HotkeyActivationMode.Hold;
            }
        }
    }

    public bool HasConflict
    {
        get => hasConflict;
        private set => SetProperty(ref hasConflict, value);
    }

    public IRelayCommand ClearCommand { get; }

    private HotkeyBinding CurrentBinding =>
        logicalSwitch == LogicalSwitch.A
            ? workspace.SelectedProfile.Switches.SwitchA
            : workspace.SelectedProfile.Switches.SwitchB;

    private void TryCommit()
    {
        var candidate = new HotkeyBinding(
            Mode,
            Mode == HotkeyActivationMode.Toggle ? ToggleKey : null,
            Mode == HotkeyActivationMode.Independent ? EnableKey : null,
            Mode == HotkeyActivationMode.Independent ? DisableKey : null,
            Mode == HotkeyActivationMode.Hold ? HoldKey : null);
        if (!IsComplete(candidate))
        {
            return;
        }

        bool applied = workspace.UpdateSelectedProfile(profile => profile with
        {
            Switches = logicalSwitch == LogicalSwitch.A
                ? profile.Switches with { SwitchA = candidate }
                : profile.Switches with { SwitchB = candidate },
        });
        HasConflict = !applied;
    }

    private void Clear()
    {
        mode = HotkeyActivationMode.Unbound;
        toggleKey = null;
        enableKey = null;
        disableKey = null;
        holdKey = null;
        OnPropertyChanged(string.Empty);
        TryCommit();
    }

    private void Load(HotkeyBinding binding)
    {
        mode = binding.Mode;
        toggleKey = binding.ToggleKey;
        enableKey = binding.EnableKey;
        disableKey = binding.DisableKey;
        holdKey = binding.HoldKey;
        HasConflict = false;
        OnPropertyChanged(string.Empty);
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        HotkeyBinding current = CurrentBinding;
        if (current != CreateDraft() &&
            !HasConflict)
        {
            Load(current);
        }
    }

    private HotkeyBinding CreateDraft() => new(
        Mode,
        Mode == HotkeyActivationMode.Toggle ? ToggleKey : null,
        Mode == HotkeyActivationMode.Independent ? EnableKey : null,
        Mode == HotkeyActivationMode.Independent ? DisableKey : null,
        Mode == HotkeyActivationMode.Hold ? HoldKey : null);

    private static bool IsComplete(HotkeyBinding binding) => binding.Mode switch
    {
        HotkeyActivationMode.Unbound => true,
        HotkeyActivationMode.Toggle => binding.ToggleKey.HasValue,
        HotkeyActivationMode.Independent => binding.EnableKey.HasValue && binding.DisableKey.HasValue,
        HotkeyActivationMode.Hold => binding.HoldKey.HasValue,
        _ => false,
    };
}

internal sealed class SwitchesViewModel : WorkspaceViewModel
{
    public SwitchesViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization)
        : base(workspace)
    {
        VisibilityRules =
        [
            new(VisibilityRule.SwitchA, "Switch.RuleA", localization),
            new(VisibilityRule.SwitchB, "Switch.RuleB", localization),
            new(VisibilityRule.Both, "Switch.RuleBoth", localization),
            new(VisibilityRule.Either, "Switch.RuleEither", localization),
        ];
        SwitchA = new HotkeyEditorViewModel(workspace, LogicalSwitch.A, localization);
        SwitchB = new HotkeyEditorViewModel(workspace, LogicalSwitch.B, localization);
    }

    public IReadOnlyList<LocalizedOption<VisibilityRule>> VisibilityRules { get; }

    public VisibilityRule VisibilityRule
    {
        get => Workspace.SelectedProfile.Switches.VisibilityRule;
        set => Workspace.UpdateSelectedProfile(profile => profile with
        {
            Switches = profile.Switches with { VisibilityRule = value },
        });
    }

    public bool InitialStateA
    {
        get => Workspace.SelectedProfile.Switches.InitialStateA;
        set => Workspace.UpdateSelectedProfile(profile => profile with
        {
            Switches = profile.Switches with { InitialStateA = value },
        });
    }

    public bool InitialStateB
    {
        get => Workspace.SelectedProfile.Switches.InitialStateB;
        set => Workspace.UpdateSelectedProfile(profile => profile with
        {
            Switches = profile.Switches with { InitialStateB = value },
        });
    }

    public HotkeyEditorViewModel SwitchA { get; }

    public HotkeyEditorViewModel SwitchB { get; }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(VisibilityRule));
        OnPropertyChanged(nameof(InitialStateA));
        OnPropertyChanged(nameof(InitialStateB));
    }
}

internal sealed class ProfileSetsViewModel : WorkspaceViewModel
{
    private Profile? selectedAvailableProfile;

    public ProfileSetsViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization)
        : base(workspace)
    {
        AddProfileCommand = new RelayCommand(() => workspace.AddProfile(localization["Profiles.NewProfile"]));
        DuplicateProfileCommand = new RelayCommand(workspace.DuplicateSelectedProfile);
        DeleteProfileCommand = new RelayCommand(workspace.DeleteSelectedProfile);
        AddSetCommand = new RelayCommand(() => workspace.AddProfileSet(localization["Profiles.NewSet"]));
        DeleteSetCommand = new RelayCommand(workspace.DeleteSelectedProfileSet);
        ActivateSetCommand = new RelayCommand(
            () => workspace.ActivateProfileSet(workspace.SelectedProfileSetId));
        AssignCommand = new RelayCommand(
            () =>
            {
                if (SelectedAvailableProfile is { } profile)
                {
                    workspace.AssignProfile(workspace.SelectedProfileSetId, profile.Id);
                }
            });
        RemoveCommand = new RelayCommand(
            () => workspace.RemoveProfileFromSelectedSet(workspace.SelectedProfileId));
    }

    public IReadOnlyList<ProfileSet> ProfileSets => Workspace.Document.ProfileSets;

    public ProfileSet? SelectedProfileSet
    {
        get => Workspace.SelectedProfileSet;
        set
        {
            if (value is not null && value.Id != Workspace.SelectedProfileSetId)
            {
                Workspace.SelectProfileSet(value.Id);
            }
        }
    }

    public IReadOnlyList<Profile> ProfilesInSet
    {
        get
        {
            HashSet<Guid> ids = Workspace.SelectedProfileSet?.ProfileIds.ToHashSet() ?? [];
            return Workspace.Document.Profiles.Where(profile => ids.Contains(profile.Id)).ToArray();
        }
    }

    public Profile SelectedProfile
    {
        get => Workspace.SelectedProfile;
        set => Workspace.SelectProfile(value.Id);
    }

    public IReadOnlyList<Profile> AvailableProfiles => Workspace.Document.Profiles;

    public Profile? SelectedAvailableProfile
    {
        get => selectedAvailableProfile;
        set => SetProperty(ref selectedAvailableProfile, value);
    }

    public bool IsActiveSet =>
        Workspace.Document.ProfileSets.FirstOrDefault()?.Id == Workspace.SelectedProfileSetId;

    public IRelayCommand AddProfileCommand { get; }

    public IRelayCommand DuplicateProfileCommand { get; }

    public IRelayCommand DeleteProfileCommand { get; }

    public IRelayCommand AddSetCommand { get; }

    public IRelayCommand DeleteSetCommand { get; }

    public IRelayCommand ActivateSetCommand { get; }

    public IRelayCommand AssignCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    public void AssignProfile(Guid profileId, Guid profileSetId) =>
        Workspace.AssignProfile(profileSetId, profileId);

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(ProfileSets));
        OnPropertyChanged(nameof(SelectedProfileSet));
        OnPropertyChanged(nameof(ProfilesInSet));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(AvailableProfiles));
        OnPropertyChanged(nameof(IsActiveSet));
    }
}

internal sealed class ImportExportViewModel : WorkspaceViewModel
{
    private readonly LocalizationService localization;
    private readonly IFileDialogService fileDialogs;
    private ConfigurationMergeResult? preview;
    private string? status;

    public ImportExportViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        IFileDialogService fileDialogs)
        : base(workspace)
    {
        this.localization = localization;
        this.fileDialogs = fileDialogs;
        PreviewImportCommand = new AsyncRelayCommand(PreviewImportAsync);
        ConfirmImportCommand = new RelayCommand(ConfirmImport);
        CancelImportCommand = new RelayCommand(CancelImport);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        localization.CultureChanged += OnCultureChanged;
    }

    public bool HasPreview => Preview is not null;

    public ConfigurationMergeResult? Preview
    {
        get => preview;
        private set
        {
            if (SetProperty(ref preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(Conflicts));
            }
        }
    }

    public string Summary => Preview is null
        ? localization["Transfer.NoPreview"]
        : string.Format(
            localization.Culture,
            localization["Transfer.Summary"],
            Preview.Document.Profiles.Length - Workspace.Document.Profiles.Length,
            Preview.Document.ProfileSets.Length - Workspace.Document.ProfileSets.Length,
            Preview.Assets.Length,
            Preview.Conflicts.Length);

    public IReadOnlyList<string> Conflicts => Preview?.Conflicts.Select(conflict =>
        $"{conflict.Kind}: {conflict.OriginalId:D} -> {conflict.ResolvedId:D}").ToArray() ?? [];

    public string? Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public IAsyncRelayCommand PreviewImportCommand { get; }

    public IRelayCommand ConfirmImportCommand { get; }

    public IRelayCommand CancelImportCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
    }

    private async Task PreviewImportAsync()
    {
        string? path = await fileDialogs.OpenPackageAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            Preview = EpsxArchive.Import(stream, Workspace.Document);
            Status = null;
        }
        catch (Exception exception) when (
            exception is IOException or ConfigurationFormatException or ConfigurationValidationException)
        {
            Preview = null;
            Status = localization["Transfer.Failed"];
        }
    }

    private void ConfirmImport()
    {
        if (Preview is null)
        {
            return;
        }

        try
        {
            Workspace.ApplyImport(Preview);
            Preview = null;
            Status = localization["Transfer.Imported"];
        }
        catch (Exception exception) when (
            exception is IOException or ConfigurationValidationException)
        {
            Status = localization["Transfer.Failed"];
        }
    }

    private void CancelImport()
    {
        Preview = null;
        Status = null;
    }

    private async Task ExportAsync()
    {
        string? path = await fileDialogs.SavePackageAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.Create(path);
            EpsxArchive.Export(stream, Workspace.Document, Workspace.ReadAssets());
            Status = localization["Transfer.Exported"];
        }
        catch (Exception exception) when (
            exception is IOException or ConfigurationFormatException)
        {
            Status = localization["Transfer.Failed"];
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Summary));
    }
}

internal sealed class SettingsViewModel : WorkspaceViewModel
{
    private readonly LocalizationService localization;
    private readonly ThemeService themeService;
    private readonly UiPreferencesStore preferencesStore;
    private AppTheme theme;
    private string cultureName;

    public SettingsViewModel(
        ConfigurationWorkspace workspace,
        LocalizationService localization,
        ThemeService themeService,
        UiPreferencesStore preferencesStore,
        UiPreferences preferences)
        : base(workspace)
    {
        this.localization = localization;
        this.themeService = themeService;
        this.preferencesStore = preferencesStore;
        theme = preferences.Theme;
        cultureName = preferences.CultureName;
        Themes =
        [
            new(AppTheme.System, "Settings.ThemeSystem", localization),
            new(AppTheme.Light, "Settings.ThemeLight", localization),
            new(AppTheme.Dark, "Settings.ThemeDark", localization),
        ];
        InputBackends =
        [
            new(InputCaptureBackend.RawInput, "Settings.InputBackendRawInput", localization),
            new(InputCaptureBackend.LowLevelHook, "Settings.InputBackendLowLevelHook", localization),
        ];
        ToastPositions =
        [
            new(ToastPosition.TopLeft, "Settings.TopLeft", localization),
            new(ToastPosition.TopCenter, "Settings.TopCenter", localization),
            new(ToastPosition.TopRight, "Settings.TopRight", localization),
            new(ToastPosition.BottomLeft, "Settings.BottomLeft", localization),
            new(ToastPosition.BottomCenter, "Settings.BottomCenter", localization),
            new(ToastPosition.BottomRight, "Settings.BottomRight", localization),
        ];
    }

    public IReadOnlyList<LocalizedOption<AppTheme>> Themes { get; }

    public IReadOnlyList<LocalizedOption<InputCaptureBackend>> InputBackends { get; }

    public IReadOnlyList<LocalizedOption<ToastPosition>> ToastPositions { get; }

    public AppTheme Theme
    {
        get => theme;
        set
        {
            if (SetProperty(ref theme, value))
            {
                themeService.Apply(value);
                SavePreferences();
            }
        }
    }

    public string CultureName
    {
        get => cultureName;
        set
        {
            if (SetProperty(ref cultureName, value))
            {
                localization.Apply(value);
                SavePreferences();
            }
        }
    }

    public InputCaptureBackend InputBackend
    {
        get => Workspace.Document.InputBackend;
        set => Workspace.UpdateDocument(document => document with { InputBackend = value });
    }

    public bool ToastEnabled
    {
        get => Workspace.Document.Toasts.Enabled;
        set => UpdateToasts(toasts => toasts with { Enabled = value });
    }

    public ToastPosition ToastPosition
    {
        get => Workspace.Document.Toasts.Position;
        set => UpdateToasts(toasts => toasts with { Position = value });
    }

    public int ToastDuration
    {
        get => Workspace.Document.Toasts.DurationMs;
        set => UpdateToasts(toasts => toasts with { DurationMs = value });
    }

    public string ToastFontFamily
    {
        get => Workspace.Document.Toasts.FontFamily;
        set => UpdateToasts(toasts => toasts with { FontFamily = value });
    }

    public double ToastFontSize
    {
        get => Workspace.Document.Toasts.FontSizePx;
        set => UpdateToasts(toasts => toasts with { FontSizePx = value });
    }

    public Color ToastForeground
    {
        get => ToColor(Workspace.Document.Toasts.Foreground);
        set => UpdateToasts(toasts => toasts with { Foreground = ToRgba(value) });
    }

    public string ToastForegroundHex
    {
        get => Workspace.Document.Toasts.Foreground.ToString();
        set => UpdateColor(value, color => UpdateToasts(toasts => toasts with { Foreground = color }));
    }

    public Color ToastBackground
    {
        get => ToColor(Workspace.Document.Toasts.Background);
        set => UpdateToasts(toasts => toasts with { Background = ToRgba(value) });
    }

    public string ToastBackgroundHex
    {
        get => Workspace.Document.Toasts.Background.ToString();
        set => UpdateColor(value, color => UpdateToasts(toasts => toasts with { Background = color }));
    }

    protected override void Refresh()
    {
        OnPropertyChanged(nameof(InputBackend));
        OnPropertyChanged(nameof(ToastEnabled));
        OnPropertyChanged(nameof(ToastPosition));
        OnPropertyChanged(nameof(ToastDuration));
        OnPropertyChanged(nameof(ToastFontFamily));
        OnPropertyChanged(nameof(ToastFontSize));
        OnPropertyChanged(nameof(ToastForeground));
        OnPropertyChanged(nameof(ToastForegroundHex));
        OnPropertyChanged(nameof(ToastBackground));
        OnPropertyChanged(nameof(ToastBackgroundHex));
    }

    private void UpdateToasts(Func<ToastConfiguration, ToastConfiguration> update) =>
        Workspace.UpdateDocument(document => document with { Toasts = update(document.Toasts) });

    private void SavePreferences() =>
        preferencesStore.Save(new UiPreferences(Theme, CultureName));

    private static void UpdateColor(string value, Action<RgbaColor> update)
    {
        try
        {
            update(RgbaColor.Parse(value));
        }
        catch (FormatException)
        {
        }
    }

    private static Color ToColor(RgbaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static RgbaColor ToRgba(Color color) => new(color.R, color.G, color.B, color.A);
}
