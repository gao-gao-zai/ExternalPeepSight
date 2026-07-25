using System.Globalization;

namespace ExternalPeepSight.Core;

/// <summary>
/// Selects the visual element rendered by a profile.
/// </summary>
public enum OverlayMode
{
    Crosshair,
    Image,
}

/// <summary>
/// Defines the origin used by an overlay position.
/// </summary>
public enum AnchorMode
{
    ScreenCenter,
    TopLeft,
}

/// <summary>
/// Selects the monitors that receive an overlay.
/// </summary>
public enum MonitorSelectionMode
{
    All,
    Explicit,
    Focus,
}

/// <summary>
/// Selects the fallback source used to resolve the focus monitor.
/// </summary>
public enum FocusMonitorSource
{
    ForegroundWindowThenMouse,
    Mouse,
}

/// <summary>
/// Selects the activation behavior of a hotkey binding.
/// </summary>
public enum HotkeyActivationMode
{
    Unbound,
    Independent,
    Toggle,
    Hold,
}

/// <summary>
/// Selects the runtime that controls overlay visibility for a profile.
/// </summary>
public enum DisplayControlMode
{
    Basic,
    Lua,
}

/// <summary>
/// Identifies the configuration object owned by a Lua script.
/// </summary>
public enum ScriptScope
{
    Profile,
    ProfileSet,
    Global,
}

/// <summary>
/// Identifies the phase of an input event exposed to a Lua script.
/// </summary>
public enum ScriptInputPhase
{
    Pressed,
    Released,
}

/// <summary>
/// Identifies the value type declared by a Lua script setting.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "These member names are the stable script API type names exposed to users.")]
public enum ScriptSettingType
{
    Boolean,
    Integer,
    Double,
    String,
    Enum,
}

/// <summary>
/// Selects the trusted editor control used to present a script setting.
/// </summary>
public enum ScriptUiControlType
{
    Auto,
    Switch,
    Checkbox,
    Slider,
    Number,
    Textbox,
    Select,
    Segmented,
}

/// <summary>
/// Identifies one of the two logical switches.
/// </summary>
public enum LogicalSwitch
{
    A,
    B,
}

/// <summary>
/// Selects the position of a rendered toast.
/// </summary>
public enum ToastPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>
/// Identifies the modifier keys included in a key binding.
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// Represents a monitor-local physical-pixel point or offset.
/// </summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>
/// Represents a physical-pixel size.
/// </summary>
public readonly record struct PixelSize(int Width, int Height);

/// <summary>
/// Represents a color in RGBA byte order.
/// </summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>
    /// Gets opaque white.
    /// </summary>
    public static RgbaColor White => new(255, 255, 255, 255);

    /// <summary>
    /// Gets transparent black.
    /// </summary>
    public static RgbaColor Transparent => new(0, 0, 0, 0);

    /// <summary>
    /// Parses a six- or eight-digit hexadecimal color.
    /// </summary>
    /// <param name="value">The color in <c>#RRGGBB</c> or <c>#RRGGBBAA</c> form.</param>
    /// <returns>The parsed color.</returns>
    /// <exception cref="FormatException">The value is not a supported hexadecimal color.</exception>
    public static RgbaColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string hex = value[0] == '#' ? value[1..] : value;
        if (hex.Length is not (6 or 8) ||
            !byte.TryParse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            throw new FormatException("Color must use #RRGGBB or #RRGGBBAA format.");
        }

        byte a = 255;
        if (hex.Length == 8 &&
            !byte.TryParse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
        {
            throw new FormatException("Color must use #RRGGBB or #RRGGBBAA format.");
        }

        return new RgbaColor(r, g, b, a);
    }

    /// <summary>
    /// Formats the color as <c>#RRGGBBAA</c>.
    /// </summary>
    /// <returns>The hexadecimal color string.</returns>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

/// <summary>
/// Defines the center point of a crosshair.
/// </summary>
public sealed record CenterPoint(
    bool Visible,
    RgbaColor Color,
    int RadiusPx);

/// <summary>
/// Defines one crosshair arm using angle offsets and absolute physical dimensions.
/// </summary>
public sealed record Arm(
    double OrbitAngleOffsetDeg,
    double RotationAngleOffsetDeg,
    int GapPx,
    int LengthPx,
    int WidthPx,
    RgbaColor Color,
    bool Visible);

/// <summary>
/// Defines the default geometric values for one crosshair arm.
/// </summary>
public readonly record struct ArmDefaults(
    double OrbitAngleDeg,
    int GapPx,
    int LengthPx,
    int WidthPx);

/// <summary>
/// Resolves the stable defaults assigned to the four crosshair arms.
/// </summary>
public static class CrosshairArmDefaults
{
    private static readonly ArmDefaults[] Values =
    [
        new(0, 6, 12, 2),
        new(90, 6, 12, 2),
        new(180, 6, 12, 2),
        new(270, 6, 12, 2),
    ];

    /// <summary>
    /// Gets the defaults for an arm in up, right, down, and left order.
    /// </summary>
    /// <param name="armIndex">The zero-based arm index.</param>
    /// <returns>The arm defaults.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The arm index is outside the four-arm range.</exception>
    public static ArmDefaults Get(int armIndex)
    {
        if ((uint)armIndex >= Values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(armIndex));
        }

        return Values[armIndex];
    }
}

/// <summary>
/// Defines a four-arm crosshair.
/// </summary>
public sealed record Crosshair(
    AnchorMode Anchor,
    PixelPoint OffsetPx,
    CenterPoint Center,
    Arm[] Arms,
    bool Linked);

/// <summary>
/// Identifies an imported image resource without embedding its bytes in JSON.
/// </summary>
public sealed record AssetReference(
    Guid Id,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Defines the placement and scale of an image overlay.
/// </summary>
public sealed record ImageOverlay(
    Guid? AssetId,
    AnchorMode Anchor,
    PixelPoint OffsetPx,
    double Scale,
    bool KeepAspectRatio);

/// <summary>
/// Defines the monitor targeting policy for a configuration.
/// </summary>
public sealed record MonitorSelection(
    MonitorSelectionMode Mode,
    string[] MonitorIds,
    FocusMonitorSource FocusSource);

/// <summary>
/// Identifies the device that produces an input binding.
/// </summary>
public enum InputDeviceKind
{
    Keyboard,
    Mouse,
}

/// <summary>
/// Selects the Windows input API used to capture configured bindings.
/// </summary>
public enum InputCaptureBackend
{
    RawInput,
    LowLevelHook,
}

/// <summary>
/// Identifies a bindable physical mouse button.
/// </summary>
public enum InputMouseButton : ushort
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>
/// Identifies a physical keyboard key independently of its display label.
/// </summary>
public readonly record struct KeyIdentity(
    InputDeviceKind Device,
    ushort Code,
    bool Extended,
    KeyModifiers Modifiers);

/// <summary>
/// Defines how one logical switch is activated.
/// </summary>
public sealed record HotkeyBinding(
    HotkeyActivationMode Mode,
    KeyIdentity? ToggleKey,
    KeyIdentity? EnableKey,
    KeyIdentity? DisableKey,
    KeyIdentity? HoldKey);

/// <summary>
/// Defines one script-declared binding slot and its user-selected physical key.
/// </summary>
public sealed record ScriptBindingSlot(
    string Id,
    string DisplayName,
    bool Pressed,
    bool Released,
    bool Enabled,
    KeyIdentity? Key);

/// <summary>
/// Defines one script-declared setting and its current user value.
/// </summary>
public sealed record ScriptSetting(
    string Id,
    string DisplayName,
    ScriptSettingType Type,
    string Value,
    string[] Options,
    double? Minimum,
    double? Maximum);

/// <summary>
/// Defines a simple equality condition controlling one script UI item's visibility.
/// </summary>
public sealed record ScriptUiVisibilityCondition(
    string SettingId,
    string EqualsValue);

/// <summary>
/// Defines trusted presentation metadata for one script setting.
/// </summary>
public sealed record ScriptUiItem(
    string SettingId,
    ScriptUiControlType Control,
    string Description,
    string Unit,
    double? Step,
    ScriptUiVisibilityCondition? VisibleWhen);

/// <summary>
/// Defines one ordered section in a script-provided settings layout.
/// </summary>
public sealed record ScriptUiSection(
    string Id,
    string DisplayName,
    string Description,
    bool Collapsible,
    bool DefaultExpanded,
    int Columns,
    ScriptUiItem[] Items);

/// <summary>
/// Defines the complete trusted settings layout declared by a script.
/// </summary>
public sealed record ScriptUiLayout(
    ScriptUiSection[] Sections);

/// <summary>
/// Stores a Lua source document together with the declarations and user values
/// produced by its last successful validation.
/// </summary>
public sealed record ScriptConfiguration(
    bool Enabled,
    string ApiVersion,
    string Source,
    string SourceHash,
    ScriptBindingSlot[] Bindings,
    ScriptSetting[] Settings,
    ScriptUiLayout? Ui = null,
    Guid Id = default,
    string Name = "");

/// <summary>
/// Defines both logical switches, their hotkeys, and their visibility rule.
/// </summary>
public sealed record SwitchConfiguration(
    VisibilityRule VisibilityRule,
    bool InitialStateA,
    bool InitialStateB,
    HotkeyBinding SwitchA,
    HotkeyBinding SwitchB);

/// <summary>
/// Defines the appearance and lifetime of status toasts.
/// </summary>
public sealed record ToastConfiguration(
    bool Enabled,
    ToastPosition Position,
    int DurationMs,
    string FontFamily,
    double FontSizePx,
    RgbaColor Foreground,
    RgbaColor Background);

/// <summary>
/// Defines one saved configuration.
/// </summary>
public sealed record Profile
{
    /// <summary>
    /// Initializes a profile without or with one compatibility script assignment.
    /// </summary>
    public Profile(
        Guid id,
        string name,
        OverlayMode activeMode,
        Crosshair crosshair,
        ImageOverlay image,
        SwitchConfiguration switches,
        DisplayControlMode controlMode = DisplayControlMode.Basic,
        ScriptConfiguration? script = null)
    {
        Id = id;
        Name = name;
        ActiveMode = activeMode;
        Crosshair = crosshair;
        Image = image;
        Switches = switches;
        ControlMode = controlMode;
        Scripts = script is null ? [] : [EnsureScriptIdentity(script, name)];
    }

    /// <summary>
    /// Initializes a profile with an ordered script stack.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public Profile(
        Guid id,
        string name,
        OverlayMode activeMode,
        Crosshair crosshair,
        ImageOverlay image,
        SwitchConfiguration switches,
        DisplayControlMode controlMode,
        ScriptConfiguration[] scripts)
    {
        Id = id;
        Name = name;
        ActiveMode = activeMode;
        Crosshair = crosshair;
        Image = image;
        Switches = switches;
        ControlMode = controlMode;
        Scripts = scripts;
    }

    /// <summary>
    /// Gets the stable profile identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the user-visible profile name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets the active overlay mode.
    /// </summary>
    public OverlayMode ActiveMode { get; init; }

    /// <summary>
    /// Gets the crosshair configuration.
    /// </summary>
    public Crosshair Crosshair { get; init; }

    /// <summary>
    /// Gets the image overlay configuration.
    /// </summary>
    public ImageOverlay Image { get; init; }

    /// <summary>
    /// Gets the logical switch configuration.
    /// </summary>
    public SwitchConfiguration Switches { get; init; }

    /// <summary>
    /// Gets the visibility control mode.
    /// </summary>
    public DisplayControlMode ControlMode { get; init; }

    /// <summary>
    /// Gets the ordered profile-level script stack.
    /// </summary>
    public ScriptConfiguration[] Scripts { get; init; } = [];

    /// <summary>
    /// Gets or replaces the first script for compatibility with single-script callers.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ScriptConfiguration? Script
    {
        get => Scripts?.FirstOrDefault();
        init => Scripts = value is null ? [] : [EnsureScriptIdentity(value, Name)];
    }

    private static ScriptConfiguration EnsureScriptIdentity(ScriptConfiguration script, string fallbackName) =>
        script with
        {
            Id = script.Id == Guid.Empty ? Guid.NewGuid() : script.Id,
            Name = string.IsNullOrWhiteSpace(script.Name) ? fallbackName : script.Name,
        };
}

/// <summary>
/// Defines an ordered group of profiles.
/// </summary>
public sealed record ProfileSet
{
    /// <summary>
    /// Initializes a profile set without or with one compatibility script assignment.
    /// </summary>
    public ProfileSet(
        Guid id,
        string name,
        Guid[] profileIds,
        Guid? selectedProfileId,
        ScriptConfiguration? script = null)
    {
        Id = id;
        Name = name;
        ProfileIds = profileIds;
        SelectedProfileId = selectedProfileId;
        Scripts = script is null ? [] : [EnsureScriptIdentity(script, name)];
    }

    /// <summary>
    /// Initializes a profile set with an ordered script stack.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public ProfileSet(
        Guid id,
        string name,
        Guid[] profileIds,
        Guid? selectedProfileId,
        ScriptConfiguration[] scripts)
    {
        Id = id;
        Name = name;
        ProfileIds = profileIds;
        SelectedProfileId = selectedProfileId;
        Scripts = scripts;
    }

    /// <summary>
    /// Gets the stable profile-set identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the user-visible profile-set name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets the ordered member profile identifiers.
    /// </summary>
    public Guid[] ProfileIds { get; init; }

    /// <summary>
    /// Gets the selected member profile identifier.
    /// </summary>
    public Guid? SelectedProfileId { get; init; }

    /// <summary>
    /// Gets the ordered profile-set-level script stack.
    /// </summary>
    public ScriptConfiguration[] Scripts { get; init; } = [];

    /// <summary>
    /// Gets or replaces the first script for compatibility with single-script callers.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ScriptConfiguration? Script
    {
        get => Scripts?.FirstOrDefault();
        init => Scripts = value is null ? [] : [EnsureScriptIdentity(value, Name)];
    }

    private static ScriptConfiguration EnsureScriptIdentity(ScriptConfiguration script, string fallbackName) =>
        script with
        {
            Id = script.Id == Guid.Empty ? Guid.NewGuid() : script.Id,
            Name = string.IsNullOrWhiteSpace(script.Name) ? fallbackName : script.Name,
        };
}

/// <summary>
/// Defines the versioned on-disk application state document.
/// </summary>
public sealed record ConfigurationDocument
{
    /// <summary>
    /// Gets the serialized schema version.
    /// </summary>
    public int SchemaVersion { get; init; } = ConfigurationJson.CurrentSchemaVersion;

    /// <summary>
    /// Gets the saved profiles.
    /// </summary>
    public Profile[] Profiles { get; init; } = [];

    /// <summary>
    /// Gets the saved profile sets.
    /// </summary>
    public ProfileSet[] ProfileSets { get; init; } = [];

    /// <summary>
    /// Gets the explicitly active profile set.
    /// </summary>
    public Guid ActiveProfileSetId { get; init; }

    /// <summary>
    /// Gets the image resource metadata referenced by profiles.
    /// </summary>
    public AssetReference[] Assets { get; init; } = [];

    /// <summary>
    /// Gets the monitor targeting policy.
    /// </summary>
    public MonitorSelection MonitorSelection { get; init; } =
        new(MonitorSelectionMode.Focus, [], FocusMonitorSource.ForegroundWindowThenMouse);

    /// <summary>
    /// Gets the Windows input API used to capture configured bindings.
    /// </summary>
    public InputCaptureBackend InputBackend { get; init; } = InputCaptureBackend.RawInput;

    /// <summary>
    /// Gets the toast configuration.
    /// </summary>
    public ToastConfiguration Toasts { get; init; } =
        new(true, ToastPosition.TopCenter, 1500, "Segoe UI", 18, RgbaColor.White, new RgbaColor(0, 0, 0, 180));

    /// <summary>
    /// Gets the ordered global Lua script stack.
    /// </summary>
    public ScriptConfiguration[] GlobalScripts { get; init; } = [];

    /// <summary>
    /// Gets or replaces the first global script for compatibility with single-script callers.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ScriptConfiguration? GlobalScript
    {
        get => GlobalScripts?.FirstOrDefault();
        init => GlobalScripts = value is null
            ? []
            :
            [
                value with
                {
                    Id = value.Id == Guid.Empty ? Guid.NewGuid() : value.Id,
                    Name = string.IsNullOrWhiteSpace(value.Name) ? "Global" : value.Name,
                },
            ];
    }
}

/// <summary>
/// Creates consistent defaults for new configuration documents.
/// </summary>
public static class ConfigurationDefaults
{
    /// <summary>
    /// Creates a new document containing one usable profile and profile set.
    /// </summary>
    /// <returns>A new default configuration document.</returns>
    public static ConfigurationDocument Create()
    {
        Guid profileId = Guid.NewGuid();
        Guid profileSetId = Guid.NewGuid();
        Arm[] arms =
        [
            new(0, 0, 6, 12, 2, RgbaColor.White, true),
            new(0, 0, 6, 12, 2, RgbaColor.White, true),
            new(0, 0, 6, 12, 2, RgbaColor.White, true),
            new(0, 0, 6, 12, 2, RgbaColor.White, true),
        ];

        return new ConfigurationDocument
        {
            Profiles =
            [
                new(
                    profileId,
                    "Default",
                    OverlayMode.Crosshair,
                    new Crosshair(
                        AnchorMode.ScreenCenter,
                        new PixelPoint(0, 0),
                        new CenterPoint(true, RgbaColor.White, 2),
                        arms,
                        true),
                    new ImageOverlay(null, AnchorMode.ScreenCenter, new PixelPoint(0, 0), 1, true),
                    CreateSwitches()),
            ],
            ProfileSets =
            [
                new(profileSetId, "Default", [profileId], profileId),
            ],
            ActiveProfileSetId = profileSetId,
        };
    }

    /// <summary>
    /// Creates the default logical switch and hotkey configuration.
    /// </summary>
    /// <returns>An unbound switch configuration with both switches disabled.</returns>
    public static SwitchConfiguration CreateSwitches() =>
        new(
            VisibilityRule.SwitchA,
            false,
            false,
            new HotkeyBinding(HotkeyActivationMode.Unbound, null, null, null, null),
            new HotkeyBinding(HotkeyActivationMode.Unbound, null, null, null, null));
}
