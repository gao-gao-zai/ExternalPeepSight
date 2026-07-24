using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace ExternalPeepSight.UI.Controls;

internal sealed partial class ColorField : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorField, Color>(
            nameof(Color),
            Colors.White,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> HexProperty =
        AvaloniaProperty.Register<ColorField, string>(
            nameof(Hex),
            "#FFFFFFFF",
            defaultBindingMode: BindingMode.TwoWay);

    public ColorField()
    {
        InitializeComponent();
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string Hex
    {
        get => GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }
}
