using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using ExternalPeepSight.Core;
using ExternalPeepSight.UI.Controls;
using ExternalPeepSight.UI.Services;
using ExternalPeepSight.UI.ViewModels;
using ExternalPeepSight.UI.Views;

namespace ExternalPeepSight.UI.Tests;

public sealed class UiWorkflowTests
{
    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void MainWindowLoadsAllNavigationPagesWithCompiledBindings()
    {
        using TestContext context = CreateContext();
        var window = new MainWindow
        {
            DataContext = context.ViewModel,
            Width = 1040,
            Height = 680,
        };

        window.Show();

        Assert.Equal(5, context.ViewModel.Navigation.Count);
        foreach (NavigationItemViewModel item in context.ViewModel.Navigation)
        {
            context.ViewModel.SelectedNavigation = item;
            window.Measure(new Size(1040, 680));
            window.Arrange(new Rect(0, 0, 1040, 680));
            Assert.True(window.Bounds.Width >= 1040);
            Assert.True(window.Bounds.Height >= 680);
        }

        window.Close();
    }

    [Fact]
    public void SegmentedChoicesUpdateAnchorArmAndMonitorMode()
    {
        using TestContext context = CreateContext();
        CrosshairEditorViewModel editor = context.ViewModel.Crosshair;

        editor.IsTopLeftAnchor = true;
        editor.IsLeftArmSelected = true;
        context.ViewModel.Monitors.IsExplicitMode = true;

        Assert.Equal(AnchorMode.TopLeft, context.Workspace.SelectedProfile.Crosshair.Anchor);
        Assert.Equal(3, editor.SelectedArmIndex);
        Assert.True(editor.IsLeftArmSelected);
        Assert.Equal(
            MonitorSelectionMode.Explicit,
            context.Workspace.Document.MonitorSelection.Mode);
    }

    [Fact]
    public void OverlayModeSwitchesTheActiveEditorConfiguration()
    {
        using TestContext context = CreateContext();
        CrosshairEditorViewModel editor = context.ViewModel.Crosshair;
        Guid assetId = Guid.NewGuid();
        context.Workspace.UpdateDocument(document => document with
        {
            Assets =
            [
                new AssetReference(assetId, "test.png", "image/png", 1, new string('A', 64)),
            ],
            Profiles =
            [
                document.Profiles[0] with
                {
                    Image = document.Profiles[0].Image with { AssetId = assetId },
                },
            ],
        });

        editor.IsImageMode = true;

        Assert.Equal(OverlayMode.Image, context.Workspace.SelectedProfile.ActiveMode);
        Assert.True(editor.IsImageMode);
        Assert.False(editor.IsCrosshairMode);

        editor.Image.Scale = 1.75;

        Assert.Equal(1.75, context.Workspace.SelectedProfile.Image.Scale);
    }

    [Fact]
    public void CrosshairEditorTabsSwitchBetweenOverlayAndSwitchConfiguration()
    {
        using TestContext context = CreateContext();
        CrosshairEditorViewModel editor = context.ViewModel.Crosshair;

        editor.IsSwitchesTab = true;

        Assert.True(editor.IsSwitchesTab);
        Assert.False(editor.IsOverlayTab);
        Assert.Same(editor.Switches, context.ViewModel.Switches);
    }

    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void ColorFieldInitializesWithBoundValues()
    {
        var field = new ColorField
        {
            Color = Colors.CornflowerBlue,
            Hex = "#FF6495ED",
        };

        Assert.Equal(Colors.CornflowerBlue, field.Color);
        Assert.Equal("#FF6495ED", field.Hex);
    }

    [Fact]
    public void LinkedArmEditUpdatesAllArmsAndInvalidValueIsRejected()
    {
        using TestContext context = CreateContext();
        CrosshairEditorViewModel editor = context.ViewModel.Crosshair;

        editor.Linked = true;
        editor.ArmLength = 36;

        Assert.All(context.Workspace.SelectedProfile.Crosshair.Arms, arm => Assert.Equal(36, arm.LengthPx));
        editor.ArmWidth = 0;
        Assert.All(context.Workspace.SelectedProfile.Crosshair.Arms, arm => Assert.NotEqual(0, arm.WidthPx));
        Assert.NotNull(context.Workspace.ErrorMessage);
    }

    [Fact]
    public void LinkedArmEditSynchronizesAllGeometricOffsets()
    {
        using TestContext context = CreateContext();
        CrosshairEditorViewModel editor = context.ViewModel.Crosshair;

        editor.Linked = true;
        editor.ArmOrbitAngleOffset = 15;
        editor.ArmRotationAngleOffset = -20;
        editor.ArmGap = 2;
        editor.ArmLength = 4;
        editor.ArmWidth = 1;

        Assert.All(
            context.Workspace.SelectedProfile.Crosshair.Arms,
            arm =>
            {
                Assert.Equal(15, arm.OrbitAngleOffsetDeg);
                Assert.Equal(-20, arm.RotationAngleOffsetDeg);
                Assert.Equal(2, arm.GapPx);
                Assert.Equal(4, arm.LengthPx);
                Assert.Equal(1, arm.WidthPx);
            });
    }

    [Fact]
    public void DuplicateHotkeyIsRejectedWithoutReplacingLastValidBinding()
    {
        using TestContext context = CreateContext();
        var key = new KeyIdentity(InputDeviceKind.Keyboard, 0x1E, false, KeyModifiers.Ctrl);

        context.ViewModel.Switches.SwitchA.Mode = HotkeyActivationMode.Toggle;
        context.ViewModel.Switches.SwitchA.ToggleKey = key;
        context.ViewModel.Switches.SwitchB.Mode = HotkeyActivationMode.Toggle;
        context.ViewModel.Switches.SwitchB.ToggleKey = key;

        Assert.Equal(key, context.Workspace.SelectedProfile.Switches.SwitchA.ToggleKey);
        Assert.Equal(HotkeyActivationMode.Unbound, context.Workspace.SelectedProfile.Switches.SwitchB.Mode);
        Assert.True(context.ViewModel.Switches.SwitchB.HasConflict);
    }

    [Fact]
    public void EditingHotkeyChangesOnlySelectedProfile()
    {
        using TestContext context = CreateContext();
        Guid firstProfileId = context.Workspace.SelectedProfileId;
        context.Workspace.AddProfile("Second");
        Guid secondProfileId = context.Workspace.SelectedProfileId;
        var key = new KeyIdentity(InputDeviceKind.Keyboard, 0x31, false, KeyModifiers.None);

        context.ViewModel.Switches.SwitchA.Mode = HotkeyActivationMode.Toggle;
        context.ViewModel.Switches.SwitchA.ToggleKey = key;
        context.Workspace.SelectProfile(firstProfileId);

        Profile first = context.Workspace.Document.Profiles.Single(profile => profile.Id == firstProfileId);
        Profile second = context.Workspace.Document.Profiles.Single(profile => profile.Id == secondProfileId);
        Assert.Equal(HotkeyActivationMode.Unbound, first.Switches.SwitchA.Mode);
        Assert.Null(first.Switches.SwitchA.ToggleKey);
        Assert.Equal(HotkeyActivationMode.Toggle, second.Switches.SwitchA.Mode);
        Assert.Equal(key, second.Switches.SwitchA.ToggleKey);
    }

    [Fact]
    public void HostRejectionDisplaysTheReturnedErrorCodeAndMessage()
    {
        using TestContext context = CreateContext(new RejectingHostSession());

        context.ViewModel.Crosshair.ArmLength = 36;

        Assert.True(SpinWait.SpinUntil(
            () => context.Workspace.ErrorMessage?.Contains("InputRegistrationFailed", StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(1)));
        Assert.Contains("RegisterHotKey failed for F8", context.Workspace.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceSubscribesBeforeStartingHostAndPushesSavedConfiguration()
    {
        var host = new StartingHostSession();

        using TestContext context = CreateContext(host);

        Assert.True(host.Started);
        Assert.Equal(1UL, host.QueuedConfigurationVersion);
        Assert.Equal(
            ConfigurationJson.Serialize(context.Workspace.Document),
            host.QueuedSnapshot.GetRawText());
    }

    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void SelectingHotkeyModeShowsRecognizableCaptureField()
    {
        using TestContext context = CreateContext();
        var view = new HotkeyEditorView
        {
            DataContext = context.ViewModel.Switches.SwitchA,
        };
        var window = new Window
        {
            Content = view,
            Width = 800,
            Height = 260,
        };

        window.Show();
        context.ViewModel.Switches.SwitchA.IsToggleMode = true;
        window.Measure(new Size(800, 260));
        window.Arrange(new Rect(0, 0, 800, 260));

        HotkeyCaptureBox field = view.GetVisualDescendants()
            .OfType<HotkeyCaptureBox>()
            .Single(control => control.IsEffectivelyVisible);
        Assert.True(field.MinHeight >= 38);
        Assert.Equal(new Thickness(1), field.BorderThickness);
        Assert.False(string.IsNullOrWhiteSpace(field.PlaceholderText));
        Assert.Equal(field.PlaceholderText, field.Content);

        window.Close();
    }

    [Fact]
    public void MouseHotkeyHasRecognizableDisplayText()
    {
        var field = new HotkeyCaptureBox
        {
            Value = new KeyIdentity(
                InputDeviceKind.Mouse,
                (ushort)InputMouseButton.X1,
                false,
                KeyModifiers.Ctrl),
        };

        Assert.Equal("Ctrl + Mouse X1", field.Content);
    }

    [Fact]
    public void AssigningProfileToSetProducesUniqueMembership()
    {
        using TestContext context = CreateContext();
        context.Workspace.AddProfile("Second");
        Guid profileId = context.Workspace.SelectedProfileId;
        context.Workspace.AddProfileSet("Second set");
        Guid setId = context.Workspace.SelectedProfileSetId;

        context.ViewModel.Profiles.AssignProfile(profileId, setId);
        context.ViewModel.Profiles.AssignProfile(profileId, setId);

        ProfileSet set = context.Workspace.Document.ProfileSets.Single(item => item.Id == setId);
        Assert.Single(set.ProfileIds, id => id == profileId);
    }

    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void ThemeAndLanguageChangesUpdateApplicationResources()
    {
        using TestContext context = CreateContext();

        context.ViewModel.Settings.Theme = AppTheme.Dark;
        context.ViewModel.Settings.CultureName = "en-US";

        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        Assert.Equal("Settings", Application.Current.Resources["Loc.Navigation.Settings"]);
    }

    [Fact]
    public void MonitorIdentityMatchesNativeNormalizationRules()
    {
        string first = MonitorIdentity.Create(
            @"\\?\DISPLAY#ABC#123",
            @"\\.\DISPLAY1",
            0,
            0,
            1920,
            1080);
        string second = MonitorIdentity.Create(
            @"\\?\display#abc#123",
            @"\\.\DISPLAY9",
            -1920,
            0,
            0,
            1080);

        Assert.Equal(first, second);
        Assert.StartsWith("monitor-", first, StringComparison.Ordinal);
    }

    private static TestContext CreateContext(IHostSession? hostSession = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "ExternalPeepSight.UI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var localization = new LocalizationService();
        localization.Apply("zh-CN");
        IHostSession host = hostSession ?? new FakeHostSession();
        var workspace = new ConfigurationWorkspace(
            host,
            new AtomicConfigurationStore(Path.Combine(root, "configuration.json")),
            Path.Combine(root, "assets"),
            localization,
            ConfigurationDefaults.Create());
        var preferencesStore = new UiPreferencesStore(Path.Combine(root, "preferences.json"));
        var viewModel = new MainWindowViewModel(
            workspace,
            localization,
            new FakeFileDialogs(),
            new FakeMonitorEnumeration(),
            new ThemeService(),
            preferencesStore,
            UiPreferences.Default);
        return new TestContext(root, workspace, viewModel);
    }

    private sealed class TestContext(
        string root,
        ConfigurationWorkspace workspace,
        MainWindowViewModel viewModel) : IDisposable
    {
        public ConfigurationWorkspace Workspace { get; } = workspace;

        public MainWindowViewModel ViewModel { get; } = viewModel;

        public void Dispose()
        {
            ViewModel.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHostSession : IHostSession
    {
        public event EventHandler<JsonElement>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<bool>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public bool IsConnected => true;

        public void Start()
        {
        }

        public Task QueueSnapshotAsync(
            ulong configurationVersion,
            JsonElement snapshot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingHostSession : IHostSession
    {
        public event EventHandler<JsonElement>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<bool>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public bool IsConnected => true;

        public void Start()
        {
        }

        public Task QueueSnapshotAsync(
            ulong configurationVersion,
            JsonElement snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromException(
                new HostRequestException(
                    "InputRegistrationFailed",
                    "RegisterHotKey failed for F8 with Win32 error 1409."));
    }

    private sealed class StartingHostSession : IHostSession
    {
        public event EventHandler<JsonElement>? StateChanged;

        public event EventHandler<bool>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public bool IsConnected => false;

        public bool Started { get; private set; }

        public ulong QueuedConfigurationVersion { get; private set; }

        public JsonElement QueuedSnapshot { get; private set; }

        public void Start()
        {
            Started = true;
            using JsonDocument state = JsonDocument.Parse(
                """{"configurationVersion":0,"snapshot":null}""");
            StateChanged?.Invoke(this, state.RootElement.Clone());
        }

        public Task QueueSnapshotAsync(
            ulong configurationVersion,
            JsonElement snapshot,
            CancellationToken cancellationToken = default)
        {
            QueuedConfigurationVersion = configurationVersion;
            QueuedSnapshot = snapshot.Clone();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileDialogs : IFileDialogService
    {
        public Task<string?> OpenImageAsync() => Task.FromResult<string?>(null);

        public Task<string?> OpenPackageAsync() => Task.FromResult<string?>(null);

        public Task<string?> SavePackageAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeMonitorEnumeration : IMonitorEnumerationService
    {
        public IReadOnlyList<MonitorInfo> Enumerate() =>
        [
            new("monitor-test", @"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
        ];
    }
}
