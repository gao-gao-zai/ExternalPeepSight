using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ExternalPeepSight.Core;

namespace ExternalPeepSight.UI.Controls;

public sealed class CrosshairPreview : Control
{
    public static readonly StyledProperty<Crosshair?> CrosshairProperty =
        AvaloniaProperty.Register<CrosshairPreview, Crosshair?>(nameof(Crosshair));

    static CrosshairPreview()
    {
        AffectsRender<CrosshairPreview>(CrosshairProperty);
    }

    public Crosshair? Crosshair
    {
        get => GetValue(CrosshairProperty);
        set => SetValue(CrosshairProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = Bounds;
        context.FillRectangle(
            new SolidColorBrush(Color.FromRgb(26, 29, 33)),
            new Rect(0, 0, bounds.Width, bounds.Height),
            4);
        if (Crosshair is not { } crosshair || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        const double previewSurfacePx = 512;
        CrosshairGeometryResult geometry = OverlayGeometry.CalculateCrosshair(
            crosshair,
            new ExternalPeepSight.Core.PixelSize((int)previewSurfacePx, (int)previewSurfacePx));
        double scale = Math.Min(bounds.Width, bounds.Height) / previewSurfacePx;
        double originX = (bounds.Width - previewSurfacePx * scale) / 2;
        double originY = (bounds.Height - previewSurfacePx * scale) / 2;

        foreach ((Arm arm, ArmGeometry geometryArm) in crosshair.Arms.Zip(geometry.Arms))
        {
            if (!geometryArm.Visible)
            {
                continue;
            }

            var pen = new Pen(
                new SolidColorBrush(ToColor(arm.Color)),
                Math.Max(1, geometryArm.WidthPx * scale),
                lineCap: PenLineCap.Square);
            context.DrawLine(
                pen,
                Transform(geometryArm.Start, scale, originX, originY),
                Transform(geometryArm.End, scale, originX, originY));
        }

        if (crosshair.Center.Visible)
        {
            double radius = Math.Max(0.75, crosshair.Center.RadiusPx * scale);
            context.DrawEllipse(
                new SolidColorBrush(ToColor(crosshair.Center.Color)),
                null,
                Transform(geometry.Center, scale, originX, originY),
                radius,
                radius);
        }
    }

    private static Point Transform(PixelPointD point, double scale, double originX, double originY) =>
        new(originX + point.X * scale, originY + point.Y * scale);

    private static Color ToColor(RgbaColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
