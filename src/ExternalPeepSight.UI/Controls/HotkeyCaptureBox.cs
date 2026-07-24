using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using ExternalPeepSight.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using ConfigurationKeyModifiers = ExternalPeepSight.Core.KeyModifiers;

namespace ExternalPeepSight.UI.Controls;

internal sealed class HotkeyCaptureBox : Button
{
    public static readonly StyledProperty<KeyIdentity?> ValueProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, KeyIdentity?>(
            nameof(Value),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<HotkeyCaptureBox, string?>(nameof(PlaceholderText));

    public HotkeyCaptureBox()
    {
        AddHandler(KeyDownEvent, OnCaptureKeyDown, handledEventsToo: true);
        AddHandler(
            PointerPressedEvent,
            OnCapturePointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        UpdateContent();
    }

    public KeyIdentity? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == PlaceholderTextProperty)
        {
            UpdateContent();
        }
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.PhysicalKey is PhysicalKey.Backspace or PhysicalKey.Delete)
        {
            Value = null;
            args.Handled = true;
            return;
        }

        if (PhysicalKeyMapper.TryCreate(args.PhysicalKey, args.KeyModifiers, out KeyIdentity identity))
        {
            Value = identity;
        }

        args.Handled = true;
    }

    private void OnCapturePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsKeyboardFocusWithin)
        {
            return;
        }

        PointerPointProperties properties = args.GetCurrentPoint(this).Properties;
        InputMouseButton? button = properties switch
        {
            { IsXButton2Pressed: true } => InputMouseButton.X2,
            { IsXButton1Pressed: true } => InputMouseButton.X1,
            { IsMiddleButtonPressed: true } => InputMouseButton.Middle,
            { IsRightButtonPressed: true } => InputMouseButton.Right,
            { IsLeftButtonPressed: true } => InputMouseButton.Left,
            _ => null,
        };
        if (button.HasValue)
        {
            Value = new KeyIdentity(
                InputDeviceKind.Mouse,
                (ushort)button.Value,
                false,
                PhysicalKeyMapper.MapModifiers(args.KeyModifiers));
            args.Handled = true;
        }
    }

    private void UpdateContent()
    {
        Content = Value is null
            ? PlaceholderText ?? string.Empty
            : HotkeyText.Format(Value);
    }
}

internal static class HotkeyText
{
    public static string Format(KeyIdentity? identity)
    {
        if (identity is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (identity.Value.Modifiers.HasFlag(ConfigurationKeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }
        if (identity.Value.Modifiers.HasFlag(ConfigurationKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (identity.Value.Modifiers.HasFlag(ConfigurationKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (identity.Value.Modifiers.HasFlag(ConfigurationKeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(PhysicalKeyMapper.GetDisplayName(identity.Value));
        return string.Join(" + ", parts);
    }
}

internal static class PhysicalKeyMapper
{
    public static bool TryCreate(
        PhysicalKey physicalKey,
        AvaloniaKeyModifiers modifiers,
        out KeyIdentity identity)
    {
        (ushort ScanCode, bool Extended)? mapped = Map(physicalKey);
        if (mapped is null)
        {
            identity = default;
            return false;
        }

        identity = new KeyIdentity(
            InputDeviceKind.Keyboard,
            mapped.Value.ScanCode,
            mapped.Value.Extended,
            MapModifiers(modifiers));
        return true;
    }

    public static string GetDisplayName(KeyIdentity identity)
    {
        if (identity.Device == InputDeviceKind.Mouse)
        {
            return ((InputMouseButton)identity.Code) switch
            {
                InputMouseButton.Left => "Mouse Left",
                InputMouseButton.Right => "Mouse Right",
                InputMouseButton.Middle => "Mouse Middle",
                InputMouseButton.X1 => "Mouse X1",
                InputMouseButton.X2 => "Mouse X2",
                _ => $"Mouse {identity.Code}",
            };
        }

        PhysicalKey key = AllMappings.FirstOrDefault(pair =>
            pair.Value.ScanCode == identity.Code &&
            pair.Value.Extended == identity.Extended).Key;
        return key == PhysicalKey.None
            ? $"SC {identity.Code:X2}"
            : key.ToString();
    }

    public static ConfigurationKeyModifiers MapModifiers(AvaloniaKeyModifiers modifiers)
    {
        ConfigurationKeyModifiers result = ConfigurationKeyModifiers.None;
        if (modifiers.HasFlag(AvaloniaKeyModifiers.Control))
        {
            result |= ConfigurationKeyModifiers.Ctrl;
        }
        if (modifiers.HasFlag(AvaloniaKeyModifiers.Alt))
        {
            result |= ConfigurationKeyModifiers.Alt;
        }
        if (modifiers.HasFlag(AvaloniaKeyModifiers.Shift))
        {
            result |= ConfigurationKeyModifiers.Shift;
        }
        if (modifiers.HasFlag(AvaloniaKeyModifiers.Meta))
        {
            result |= ConfigurationKeyModifiers.Win;
        }

        return result;
    }

    private static (ushort ScanCode, bool Extended)? Map(PhysicalKey key) =>
        AllMappings.TryGetValue(key, out (ushort ScanCode, bool Extended) value) ? value : null;

    private static readonly Dictionary<PhysicalKey, (ushort ScanCode, bool Extended)> AllMappings =
        new Dictionary<PhysicalKey, (ushort, bool)>
        {
            [PhysicalKey.Escape] = (0x01, false),
            [PhysicalKey.Digit1] = (0x02, false),
            [PhysicalKey.Digit2] = (0x03, false),
            [PhysicalKey.Digit3] = (0x04, false),
            [PhysicalKey.Digit4] = (0x05, false),
            [PhysicalKey.Digit5] = (0x06, false),
            [PhysicalKey.Digit6] = (0x07, false),
            [PhysicalKey.Digit7] = (0x08, false),
            [PhysicalKey.Digit8] = (0x09, false),
            [PhysicalKey.Digit9] = (0x0A, false),
            [PhysicalKey.Digit0] = (0x0B, false),
            [PhysicalKey.Minus] = (0x0C, false),
            [PhysicalKey.Equal] = (0x0D, false),
            [PhysicalKey.Backspace] = (0x0E, false),
            [PhysicalKey.Tab] = (0x0F, false),
            [PhysicalKey.Q] = (0x10, false),
            [PhysicalKey.W] = (0x11, false),
            [PhysicalKey.E] = (0x12, false),
            [PhysicalKey.R] = (0x13, false),
            [PhysicalKey.T] = (0x14, false),
            [PhysicalKey.Y] = (0x15, false),
            [PhysicalKey.U] = (0x16, false),
            [PhysicalKey.I] = (0x17, false),
            [PhysicalKey.O] = (0x18, false),
            [PhysicalKey.P] = (0x19, false),
            [PhysicalKey.BracketLeft] = (0x1A, false),
            [PhysicalKey.BracketRight] = (0x1B, false),
            [PhysicalKey.Enter] = (0x1C, false),
            [PhysicalKey.A] = (0x1E, false),
            [PhysicalKey.S] = (0x1F, false),
            [PhysicalKey.D] = (0x20, false),
            [PhysicalKey.F] = (0x21, false),
            [PhysicalKey.G] = (0x22, false),
            [PhysicalKey.H] = (0x23, false),
            [PhysicalKey.J] = (0x24, false),
            [PhysicalKey.K] = (0x25, false),
            [PhysicalKey.L] = (0x26, false),
            [PhysicalKey.Semicolon] = (0x27, false),
            [PhysicalKey.Quote] = (0x28, false),
            [PhysicalKey.Backquote] = (0x29, false),
            [PhysicalKey.Backslash] = (0x2B, false),
            [PhysicalKey.Z] = (0x2C, false),
            [PhysicalKey.X] = (0x2D, false),
            [PhysicalKey.C] = (0x2E, false),
            [PhysicalKey.V] = (0x2F, false),
            [PhysicalKey.B] = (0x30, false),
            [PhysicalKey.N] = (0x31, false),
            [PhysicalKey.M] = (0x32, false),
            [PhysicalKey.Comma] = (0x33, false),
            [PhysicalKey.Period] = (0x34, false),
            [PhysicalKey.Slash] = (0x35, false),
            [PhysicalKey.Space] = (0x39, false),
            [PhysicalKey.F1] = (0x3B, false),
            [PhysicalKey.F2] = (0x3C, false),
            [PhysicalKey.F3] = (0x3D, false),
            [PhysicalKey.F4] = (0x3E, false),
            [PhysicalKey.F5] = (0x3F, false),
            [PhysicalKey.F6] = (0x40, false),
            [PhysicalKey.F7] = (0x41, false),
            [PhysicalKey.F8] = (0x42, false),
            [PhysicalKey.F9] = (0x43, false),
            [PhysicalKey.F10] = (0x44, false),
            [PhysicalKey.F11] = (0x57, false),
            [PhysicalKey.F12] = (0x58, false),
            [PhysicalKey.NumPadEnter] = (0x1C, true),
            [PhysicalKey.NumPadDivide] = (0x35, true),
            [PhysicalKey.Home] = (0x47, true),
            [PhysicalKey.ArrowUp] = (0x48, true),
            [PhysicalKey.PageUp] = (0x49, true),
            [PhysicalKey.ArrowLeft] = (0x4B, true),
            [PhysicalKey.ArrowRight] = (0x4D, true),
            [PhysicalKey.End] = (0x4F, true),
            [PhysicalKey.ArrowDown] = (0x50, true),
            [PhysicalKey.PageDown] = (0x51, true),
            [PhysicalKey.Insert] = (0x52, true),
            [PhysicalKey.Delete] = (0x53, true),
        };
}
