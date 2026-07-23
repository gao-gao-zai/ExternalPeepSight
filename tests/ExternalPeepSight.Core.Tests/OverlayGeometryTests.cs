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
                        arm.GetProperty("angleDeg").GetDouble(),
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

    private static void AssertPoint(JsonElement expected, PixelPointD actual)
    {
        Assert.Equal(expected.GetProperty("x").GetDouble(), actual.X, 8);
        Assert.Equal(expected.GetProperty("y").GetDouble(), actual.Y, 8);
    }
}
