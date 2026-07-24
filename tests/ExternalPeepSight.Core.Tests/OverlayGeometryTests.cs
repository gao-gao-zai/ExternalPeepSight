using System.Text.Json;

namespace ExternalPeepSight.Core.Tests;

public sealed class OverlayGeometryTests
{
    [Fact]
    public void GoldenFixturesMatchManagedPreviewGeometry()
    {
        using JsonDocument fixture = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "render-geometry-v1.json")));

        foreach (JsonElement item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement surface = item.GetProperty("surface");
            JsonElement offset = item.GetProperty("offsetPx");
            JsonElement center = item.GetProperty("center");
            Arm[] arms = item.GetProperty("arms").EnumerateArray()
                .Select(
                    arm => new Arm(
                        arm.GetProperty("orbitAngleOffsetDeg").GetDouble(),
                        arm.GetProperty("rotationAngleOffsetDeg").GetDouble(),
                        arm.GetProperty("gapPx").GetInt32(),
                        arm.GetProperty("lengthPx").GetInt32(),
                        arm.GetProperty("widthPx").GetInt32(),
                        RgbaColor.White,
                        arm.GetProperty("visible").GetBoolean()))
                .ToArray();
            var crosshair = new Crosshair(
                Enum.Parse<AnchorMode>(item.GetProperty("anchor").GetString()!, true),
                new PixelPoint(offset.GetProperty("x").GetInt32(), offset.GetProperty("y").GetInt32()),
                new CenterPoint(
                    center.GetProperty("visible").GetBoolean(),
                    RgbaColor.White,
                    center.GetProperty("radiusPx").GetInt32()),
                arms,
                false);

            CrosshairGeometryResult actual = OverlayGeometry.CalculateCrosshair(
                crosshair,
                new PixelSize(surface.GetProperty("width").GetInt32(), surface.GetProperty("height").GetInt32()));
            JsonElement expected = item.GetProperty("expected");

            AssertPoint(expected.GetProperty("center"), actual.Center);
            JsonElement.ArrayEnumerator expectedArms = expected.GetProperty("arms").EnumerateArray();
            int index = 0;
            foreach (JsonElement expectedArm in expectedArms)
            {
                AssertPoint(expectedArm.GetProperty("start"), actual.Arms[index].Start);
                AssertPoint(expectedArm.GetProperty("end"), actual.Arms[index].End);
                index++;
            }
        }
    }

    [Fact]
    public void InvalidSurfaceIsRejected()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayGeometry.CalculateCrosshair(defaults.Profiles[0].Crosshair, new PixelSize(0, 1080)));
    }

    [Fact]
    public void OrbitAndRotationOffsetsControlDifferentPartsOfArmGeometry()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        Crosshair crosshair = defaults.Profiles[0].Crosshair with
        {
            Arms =
            [
                defaults.Profiles[0].Crosshair.Arms[0] with
                {
                    OrbitAngleOffsetDeg = 30,
                    RotationAngleOffsetDeg = 90,
                },
                .. defaults.Profiles[0].Crosshair.Arms[1..],
            ],
        };

        CrosshairGeometryResult actual = OverlayGeometry.CalculateCrosshair(crosshair, new PixelSize(1000, 1000));

        Assert.Equal(500 + (12 * 0.5) - (6 * Math.Sqrt(3) / 2), actual.Arms[0].Start.X, 8);
        Assert.Equal(500 - (12 * Math.Sqrt(3) / 2) - 3, actual.Arms[0].Start.Y, 8);
        Assert.Equal(actual.Arms[0].Start.X + (12 * Math.Sqrt(3) / 2), actual.Arms[0].End.X, 8);
        Assert.Equal(actual.Arms[0].Start.Y + 6, actual.Arms[0].End.Y, 8);
    }

    [Fact]
    public void RotationOffsetKeepsArmMidpointFixedAndFollowsOrbitAngle()
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        Crosshair baseCrosshair = defaults.Profiles[0].Crosshair with
        {
            Arms =
            [
                defaults.Profiles[0].Crosshair.Arms[0] with { OrbitAngleOffsetDeg = 45 },
                .. defaults.Profiles[0].Crosshair.Arms[1..],
            ],
        };
        Crosshair rotatedCrosshair = baseCrosshair with
        {
            Arms =
            [
                baseCrosshair.Arms[0] with { RotationAngleOffsetDeg = 90 },
                .. baseCrosshair.Arms[1..],
            ],
        };

        CrosshairGeometryResult baseline = OverlayGeometry.CalculateCrosshair(
            baseCrosshair,
            new PixelSize(1000, 1000));
        CrosshairGeometryResult rotated = OverlayGeometry.CalculateCrosshair(
            rotatedCrosshair,
            new PixelSize(1000, 1000));
        PixelPointD baselineMidpoint = Midpoint(baseline.Arms[0]);
        PixelPointD rotatedMidpoint = Midpoint(rotated.Arms[0]);

        Assert.Equal(baselineMidpoint.X, rotatedMidpoint.X, 8);
        Assert.Equal(baselineMidpoint.Y, rotatedMidpoint.Y, 8);
        Assert.Equal(
            -45,
            Math.Atan2(
                    -(rotated.Arms[0].End.Y - rotated.Arms[0].Start.Y),
                    rotated.Arms[0].End.X - rotated.Arms[0].Start.X) *
                180 /
                Math.PI,
            8);
    }

    private static void AssertPoint(JsonElement expected, PixelPointD actual)
    {
        Assert.Equal(expected.GetProperty("x").GetDouble(), actual.X, 8);
        Assert.Equal(expected.GetProperty("y").GetDouble(), actual.Y, 8);
    }

    private static PixelPointD Midpoint(ArmGeometry arm) =>
        new((arm.Start.X + arm.End.X) / 2, (arm.Start.Y + arm.End.Y) / 2);
}
