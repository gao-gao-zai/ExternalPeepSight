namespace ExternalPeepSight.Core;

/// <summary>
/// Represents a two-dimensional physical-pixel point with subpixel precision.
/// </summary>
public readonly record struct PixelPointD(double X, double Y);

/// <summary>
/// Represents one crosshair arm segment in physical pixels.
/// </summary>
public readonly record struct ArmGeometry(PixelPointD Start, PixelPointD End, int WidthPx, bool Visible);

/// <summary>
/// Represents the calculated center and arm segments of a crosshair.
/// </summary>
public sealed record CrosshairGeometryResult(PixelPointD Center, ArmGeometry[] Arms);

/// <summary>
/// Calculates deterministic overlay geometry shared with the native renderer.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>
    /// Calculates crosshair geometry in surface-local physical pixels.
    /// </summary>
    /// <param name="crosshair">The crosshair configuration.</param>
    /// <param name="surfaceSizePx">The full monitor surface size in physical pixels.</param>
    /// <returns>The center and four configured arm segments.</returns>
    public static CrosshairGeometryResult CalculateCrosshair(Crosshair crosshair, PixelSize surfaceSizePx)
    {
        ArgumentNullException.ThrowIfNull(crosshair);
        ArgumentNullException.ThrowIfNull(crosshair.Arms);
        if (surfaceSizePx.Width <= 0 || surfaceSizePx.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceSizePx), "Surface dimensions must be positive.");
        }

        PixelPointD anchor = crosshair.Anchor switch
        {
            AnchorMode.ScreenCenter => new(surfaceSizePx.Width / 2.0, surfaceSizePx.Height / 2.0),
            AnchorMode.TopLeft => new(0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(crosshair), "Crosshair anchor is invalid."),
        };
        var center = new PixelPointD(
            anchor.X + crosshair.OffsetPx.X,
            anchor.Y + crosshair.OffsetPx.Y);

        ArmGeometry[] arms = crosshair.Arms.Select(
            arm =>
            {
                double radians = arm.AngleDeg * Math.PI / 180.0;
                double directionX = Math.Sin(radians);
                double directionY = -Math.Cos(radians);
                var start = new PixelPointD(
                    center.X + (directionX * arm.GapPx),
                    center.Y + (directionY * arm.GapPx));
                var end = new PixelPointD(
                    start.X + (directionX * arm.LengthPx),
                    start.Y + (directionY * arm.LengthPx));
                return new ArmGeometry(start, end, arm.WidthPx, arm.Visible);
            }).ToArray();

        return new CrosshairGeometryResult(center, arms);
    }
}
