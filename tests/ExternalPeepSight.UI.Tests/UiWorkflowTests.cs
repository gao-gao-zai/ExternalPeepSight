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

        Assert.Equal(6, context.ViewModel.Navigation.Count);
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

    [Fact]
    public async Task AdvancedScriptValidationPersistsDeclarationsAndEnablesProfileControl()
    {
        using TestContext context = CreateContext();
        ScriptManagerViewModel manager = context.ViewModel.Scripts;

        manager.OpenProfileAssignment();
        manager.Source = "return eps.script {}";
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(DisplayControlMode.Lua, context.Workspace.SelectedProfile.ControlMode);
        Assert.True(context.Workspace.SelectedProfile.Script?.Enabled);
        Assert.Single(context.ViewModel.Crosshair.Script.Bindings);
        Assert.Equal("toggle", context.ViewModel.Crosshair.Script.Bindings[0].Id);
        Assert.Equal(
            new KeyIdentity(InputDeviceKind.Keyboard, 0x31, false, KeyModifiers.Ctrl),
            context.ViewModel.Crosshair.Script.Bindings[0].Key);
    }

    [Fact]
    public async Task ScriptDefaultBindingDoesNotReplaceAUserClearedValue()
    {
        using TestContext context = CreateContext();
        ScriptManagerViewModel manager = context.ViewModel.Scripts;

        manager.OpenProfileAssignment();
        manager.Source = "return eps.script {}";
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);
        context.ViewModel.Crosshair.Script.Bindings[0].Key = null;
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);

        Assert.Null(context.Workspace.SelectedProfile.Script?.Bindings[0].Key);
    }

    [Fact]
    public void AdvancedSummaryBuildsTrustedUiAndUpdatesConditionalVisibility()
    {
        using TestContext context = CreateContext();
        ScriptConfiguration script = new(
            true,
            "2",
            "return eps.script { api_version = \"2\" }",
            new string('A', 64),
            [],
            [
                new("enabled", "Enabled", ScriptSettingType.Boolean, "false", [], null, null),
                new("opacity", "Opacity", ScriptSettingType.Double, "0.5", [], 0, 1),
            ],
            new ScriptUiLayout(
            [
                new ScriptUiSection(
                    "general",
                    "General",
                    "Primary controls",
                    false,
                    true,
                    2,
                    [
                        new ScriptUiItem(
                            "enabled",
                            ScriptUiControlType.Switch,
                            string.Empty,
                            string.Empty,
                            null,
                            null),
                        new ScriptUiItem(
                            "opacity",
                            ScriptUiControlType.Slider,
                            "Opacity",
                            "%",
                            0.05,
                            new ScriptUiVisibilityCondition("enabled", "true")),
                    ]),
            ]));
        context.Workspace.UpdateSelectedProfile(profile => profile with
        {
            ControlMode = DisplayControlMode.Lua,
            Script = script,
        });

        ScriptAdvancedSummaryViewModel summary = context.ViewModel.Crosshair.Script;

        Assert.True(summary.HasCustomUi);
        ScriptUiSectionEditorViewModel section = Assert.Single(summary.Sections);
        Assert.Single(section.Rows);
        Assert.False(summary.Settings.Single(setting => setting.Id == "opacity").IsVisible);

        summary.Settings.Single(setting => setting.Id == "enabled").BooleanValue = true;

        Assert.True(summary.Settings.Single(setting => setting.Id == "opacity").IsVisible);
        Assert.Single(section.Rows);
        Assert.True(section.Rows[0].HasSecond);
        Assert.Equal("true", context.Workspace.SelectedProfile.Script?.Settings[0].Value);
    }

    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void ScriptEditorRendersTrustedUiControls()
    {
        using TestContext context = CreateContext();
        context.Workspace.UpdateSelectedProfile(profile => profile with
        {
            ControlMode = DisplayControlMode.Lua,
            Script = new ScriptConfiguration(
                true,
                "2",
                "return eps.script { api_version = \"2\" }",
                new string('A', 64),
                [],
                [
                    new("enabled", "Enabled", ScriptSettingType.Boolean, "true", [], null, null),
                    new("opacity", "Opacity", ScriptSettingType.Double, "0.5", [], 0, 1),
                ],
                new ScriptUiLayout(
                [
                    new ScriptUiSection(
                        "general",
                        "General",
                        string.Empty,
                        true,
                        true,
                        1,
                        [
                            new ScriptUiItem(
                                "enabled",
                                ScriptUiControlType.Switch,
                                string.Empty,
                                string.Empty,
                                null,
                                null),
                            new ScriptUiItem(
                                "opacity",
                                ScriptUiControlType.Slider,
                                string.Empty,
                                "%",
                                0.05,
                                null),
                        ]),
                ])),
        });
        var view = new ScriptEditorView
        {
            DataContext = context.ViewModel.Crosshair.Script,
            Width = 420,
            Height = 600,
        };
        var window = new Window
        {
            Content = view,
            Width = 420,
            Height = 600,
        };

        window.Show();
        window.Measure(new Size(420, 600));
        window.Arrange(new Rect(0, 0, 420, 600));

        Assert.True(context.ViewModel.Crosshair.Script.HasCustomUi);
        Assert.Single(context.ViewModel.Crosshair.Script.Sections);
        Assert.Equal(
            ScriptUiControlType.Switch,
            context.ViewModel.Crosshair.Script.Settings[0].Control);
        Assert.Equal(
            ScriptUiControlType.Slider,
            context.ViewModel.Crosshair.Script.Settings[1].Control);
        window.Close();
    }

    [Fact]
    public async Task LibraryScriptPersistsOutsideConfigurationAndCopiesToCurrentProfile()
    {
        using TestContext context = CreateContext();
        ScriptManagerViewModel manager = context.ViewModel.Scripts;

        Assert.False(manager.HasLibraryScripts);
        manager.NewScriptCommand.Execute(null);
        Assert.True(manager.HasLibraryScripts);
        manager.Name = "Reusable profile script";
        manager.Source = "return eps.script {}";
        manager.SaveScriptCommand.Execute(null);
        await manager.CopyToTargetCommand.ExecuteAsync(null);

        Assert.Equal("return eps.script {}", context.Workspace.SelectedProfile.Script?.Source);
        var store = new ScriptLibraryStore(Path.Combine(context.Root, "scripts.json"));
        ScriptLibraryEntry saved = Assert.Single(store.Load());
        Assert.Equal("Reusable profile script", saved.Name);
        Assert.Equal(ScriptScope.Profile, saved.Scope);
    }

    [Fact]
    public async Task ScriptManagerAppliesProfileSetAndGlobalScopes()
    {
        using TestContext context = CreateContext();
        ScriptManagerViewModel manager = context.ViewModel.Scripts;

        manager.IsProfileSetScope = true;
        manager.Source = "return eps.script {}";
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);

        manager.IsGlobalScope = true;
        manager.Source = "return eps.script {}";
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);

        Assert.True(context.Workspace.SelectedProfileSet?.Script?.Enabled);
        Assert.True(context.Workspace.Document.GlobalScript?.Enabled);
    }

    [Fact]
    public async Task AdvancedSummaryPersistsBindingAndOpensSelectedProfileInManager()
    {
        using TestContext context = CreateContext();
        ScriptManagerViewModel manager = context.ViewModel.Scripts;
        manager.Source = "return eps.script {}";
        await manager.ValidateAndApplyCommand.ExecuteAsync(null);
        var key = new KeyIdentity(InputDeviceKind.Mouse, (ushort)InputMouseButton.X1, false, KeyModifiers.None);

        context.ViewModel.Crosshair.Script.Bindings[0].Key = key;
        context.ViewModel.Crosshair.Script.OpenScriptManagerCommand.Execute(null);

        Assert.Equal(key, context.Workspace.SelectedProfile.Script?.Bindings[0].Key);
        Assert.Same(context.ViewModel.Scripts, context.ViewModel.SelectedNavigation.Content);
        Assert.True(context.ViewModel.Scripts.IsAssignmentSelected);
        Assert.Equal(ScriptScope.Profile, context.ViewModel.Scripts.Scope);
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

    [AvaloniaFact(
        "ExternalPeepSight.UI.Tests.AvaloniaTestSetup.BuildAvaloniaApp",
        30_000)]
    public void ClickingHotkeyFieldShowsCaptureFeedback()
    {
        var field = new HotkeyCaptureBox
        {
            PlaceholderText = "Click to bind",
            CapturingText = "Waiting for input",
        };
        var window = new Window
        {
            Content = field,
            Width = 400,
            Height = 100,
        };

        window.Show();
        field.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(field.IsCapturing);
        Assert.Equal(field.CapturingText, field.Content);

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
    public void InputBackendSelectionUpdatesConfigurationDocument()
    {
        using TestContext context = CreateContext();

        context.ViewModel.Settings.InputBackend = InputCaptureBackend.LowLevelHook;

        Assert.Equal(InputCaptureBackend.LowLevelHook, context.Workspace.Document.InputBackend);
        Assert.Contains(
            context.ViewModel.Settings.InputBackends,
            option => option.Value == InputCaptureBackend.LowLevelHook);
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
            UiPreferences.Default,
            new ScriptLibraryStore(Path.Combine(root, "scripts.json")));
        return new TestContext(root, workspace, viewModel);
    }

    private sealed class TestContext(
        string root,
        ConfigurationWorkspace workspace,
        MainWindowViewModel viewModel) : IDisposable
    {
        public string Root => root;

        public ConfigurationWorkspace Workspace { get; } = workspace;

        public MainWindowViewModel ViewModel { get; } = viewModel;

        public void Dispose()
        {
            ViewModel.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHostSession : IHostSession, IScriptValidationSession
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

        public Task<JsonElement> ValidateScriptAsync(
            JsonElement payload,
            CancellationToken cancellationToken = default)
        {
            string defaultKey = payload.GetProperty("scope").GetString() switch
            {
                "profileSet" => """{"device":"mouse","code":4,"extended":false,"modifiers":"none"}""",
                "global" => """{"device":"mouse","code":5,"extended":false,"modifiers":"none"}""",
                _ => """{"device":"keyboard","code":49,"extended":false,"modifiers":"ctrl"}""",
            };
            using JsonDocument response = JsonDocument.Parse(
                """{"declarations":{"apiVersion":"1","bindings":[{"id":"toggle","displayName":"Toggle","pressed":true,"released":false,"defaultEnabled":true,"defaultKey":""" +
                defaultKey +
                """}],"settings":[],"ui":null}}""");
            return Task.FromResult(response.RootElement.Clone());
        }
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
