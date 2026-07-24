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
/// Defines one crosshair arm in monitor-local physical pixels.
/// </summary>
public sealed record Arm(
    double AngleDeg,
    int GapPx,
    int LengthPx,
    int WidthPx,
    RgbaColor Color,
    bool Visible);

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
public sealed record Profile(
    Guid Id,
    string Name,
    OverlayMode ActiveMode,
    Crosshair Crosshair,
    ImageOverlay Image,
    SwitchConfiguration Switches);

/// <summary>
/// Defines an ordered group of profiles.
/// </summary>
public sealed record ProfileSet(
    Guid Id,
    string Name,
    Guid[] ProfileIds,
    Guid? SelectedProfileId);

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
    /// Gets the image resource metadata referenced by profiles.
    /// </summary>
    public AssetReference[] Assets { get; init; } = [];

    /// <summary>
    /// Gets the monitor targeting policy.
    /// </summary>
    public MonitorSelection MonitorSelection { get; init; } =
        new(MonitorSelectionMode.Focus, [], FocusMonitorSource.ForegroundWindowThenMouse);

    /// <summary>
    /// Gets the toast configuration.
    /// </summary>
    public ToastConfiguration Toasts { get; init; } =
        new(true, ToastPosition.TopCenter, 1500, "Segoe UI", 18, RgbaColor.White, new RgbaColor(0, 0, 0, 180));
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
            new(0, 6, 12, 2, RgbaColor.White, true),
            new(90, 6, 12, 2, RgbaColor.White, true),
            new(180, 6, 12, 2, RgbaColor.White, true),
            new(270, 6, 12, 2, RgbaColor.White, true),
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
