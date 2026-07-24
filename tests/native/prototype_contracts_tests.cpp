#include "prototype_contracts.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <gtest/gtest.h>

#include <array>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>

namespace
{
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;

[[nodiscard]] std::string read_fixture()
{
    std::ifstream input(std::filesystem::path(EXTERNAL_PEEPSIGHT_RENDER_GEOMETRY_FIXTURE), std::ios::binary);
    if (!input)
    {
        throw std::runtime_error("Unable to open render geometry fixture.");
    }
    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

[[nodiscard]] external_peepsight::PointPx parse_point(const JsonObject &object)
{
    return {static_cast<float>(object.GetNamedNumber(L"x")), static_cast<float>(object.GetNamedNumber(L"y"))};
}

void expect_point_near(const JsonObject &expected, const external_peepsight::PointPx actual)
{
    EXPECT_NEAR(expected.GetNamedNumber(L"x"), actual.x, 0.0001);
    EXPECT_NEAR(expected.GetNamedNumber(L"y"), actual.y, 0.0001);
}

TEST(OverlayWindowContract, UsesRequiredNonActivatingTransparentStyles)
{
    const DWORD style = external_peepsight::overlay_extended_style();

    EXPECT_NE(0U, style & WS_EX_TOPMOST);
    EXPECT_NE(0U, style & WS_EX_TOOLWINDOW);
    EXPECT_NE(0U, style & WS_EX_NOACTIVATE);
    EXPECT_NE(0U, style & WS_EX_TRANSPARENT);
    EXPECT_NE(0U, style & WS_EX_LAYERED);
    EXPECT_NE(0U, style & WS_EX_NOREDIRECTIONBITMAP);
    EXPECT_EQ(HTTRANSPARENT, external_peepsight::overlay_hit_test_result());
}

TEST(OverlayVisibility, KeepsProfileHiddenUntilConfiguredStateIsAvailable)
{
    EXPECT_FALSE(external_peepsight::evaluate_overlay_visibility(true, false, false, true));
    EXPECT_FALSE(external_peepsight::evaluate_overlay_visibility(true, false, true, false));
    EXPECT_TRUE(external_peepsight::evaluate_overlay_visibility(true, false, true, true));
}

TEST(OverlayVisibility, ShowsToastOnlyOnSelectedMonitor)
{
    EXPECT_TRUE(external_peepsight::evaluate_overlay_visibility(true, true, false, false));
    EXPECT_FALSE(external_peepsight::evaluate_overlay_visibility(false, true, true, true));
    EXPECT_FALSE(external_peepsight::evaluate_overlay_visibility(false, false, true, true));
}

TEST(OverlayWindowContract, CentersPrototypeBoundsWithoutCoveringTheMonitor)
{
    const RECT monitor{0, 0, 1920, 1080};

    const RECT bounds = external_peepsight::calculate_prototype_overlay_bounds(monitor);

    EXPECT_EQ(896, bounds.left);
    EXPECT_EQ(508, bounds.top);
    EXPECT_EQ(1024, bounds.right);
    EXPECT_EQ(572, bounds.bottom);
}

TEST(OverlayWindowContract, PreservesHalfPixelMonitorCenterForOddDimensions)
{
    const RECT monitor{100, 200, 2019, 1279};

    const RECT bounds = external_peepsight::calculate_prototype_overlay_bounds(monitor);

    EXPECT_EQ(995, bounds.left);
    EXPECT_EQ(707, bounds.top);
    EXPECT_EQ(1124, bounds.right);
    EXPECT_EQ(772, bounds.bottom);
    EXPECT_FLOAT_EQ(1059.5F, static_cast<float>(bounds.left + bounds.right) / 2.0F);
    EXPECT_FLOAT_EQ(739.5F, static_cast<float>(bounds.top + bounds.bottom) / 2.0F);
}

TEST(OverlayWindowContract, UsesEntireMonitorWhenItIsSmallerThanPrototypeBounds)
{
    const RECT monitor{-50, -25, 50, 25};

    const RECT bounds = external_peepsight::calculate_prototype_overlay_bounds(monitor);

    EXPECT_EQ(monitor.left, bounds.left);
    EXPECT_EQ(monitor.top, bounds.top);
    EXPECT_EQ(monitor.right, bounds.right);
    EXPECT_EQ(monitor.bottom, bounds.bottom);
}

TEST(CrosshairGeometry, CentersCardinalArmsInPhysicalPixels)
{
    const external_peepsight::CrosshairGeometry geometry =
        external_peepsight::calculate_crosshair_geometry(1920.0F, 1080.0F, 6.0F, 12.0F);

    EXPECT_FLOAT_EQ(960.0F, geometry.center.x);
    EXPECT_FLOAT_EQ(540.0F, geometry.center.y);

    EXPECT_FLOAT_EQ(534.0F, geometry.arms[0].start.y);
    EXPECT_FLOAT_EQ(522.0F, geometry.arms[0].end.y);
    EXPECT_FLOAT_EQ(966.0F, geometry.arms[1].start.x);
    EXPECT_FLOAT_EQ(978.0F, geometry.arms[1].end.x);
    EXPECT_FLOAT_EQ(546.0F, geometry.arms[2].start.y);
    EXPECT_FLOAT_EQ(558.0F, geometry.arms[2].end.y);
    EXPECT_FLOAT_EQ(954.0F, geometry.arms[3].start.x);
    EXPECT_FLOAT_EQ(942.0F, geometry.arms[3].end.x);
}

TEST(CrosshairGeometry, PreservesHalfPixelCenterForOddSurfaceDimensions)
{
    const external_peepsight::CrosshairGeometry geometry =
        external_peepsight::calculate_crosshair_geometry(1919.0F, 1079.0F, 5.0F, 11.0F);

    EXPECT_FLOAT_EQ(959.5F, geometry.center.x);
    EXPECT_FLOAT_EQ(539.5F, geometry.center.y);
}

TEST(CrosshairGeometry, MatchesSharedGoldenFixtures)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    const JsonArray cases = JsonObject::Parse(winrt::to_hstring(read_fixture())).GetNamedArray(L"cases");

    for (const auto &item : cases)
    {
        const JsonObject fixture = item.GetObject();
        const JsonObject surface = fixture.GetNamedObject(L"surface");
        const JsonObject offset = fixture.GetNamedObject(L"offsetPx");
        const JsonArray arm_values = fixture.GetNamedArray(L"arms");
        ASSERT_EQ(4U, arm_values.Size());

        std::array<external_peepsight::CrosshairArmDefinition, 4> arms{};
        for (std::uint32_t index = 0U; index < arm_values.Size(); ++index)
        {
            const JsonObject arm = arm_values.GetObjectAt(index);
            arms[index] = {
                static_cast<float>(arm.GetNamedNumber(L"angleDeg")),
                static_cast<float>(arm.GetNamedNumber(L"gapPx")),
                static_cast<float>(arm.GetNamedNumber(L"lengthPx")),
                static_cast<float>(arm.GetNamedNumber(L"widthPx")),
                arm.GetNamedBoolean(L"visible"),
            };
        }

        const bool anchor_at_center = fixture.GetNamedString(L"anchor") == L"screenCenter";
        const external_peepsight::CrosshairGeometry actual = external_peepsight::calculate_crosshair_geometry(
            static_cast<float>(surface.GetNamedNumber(L"width")), static_cast<float>(surface.GetNamedNumber(L"height")),
            anchor_at_center, parse_point(offset), arms);
        const JsonObject expected = fixture.GetNamedObject(L"expected");
        const JsonArray expected_arms = expected.GetNamedArray(L"arms");

        expect_point_near(expected.GetNamedObject(L"center"), actual.center);
        for (std::uint32_t index = 0U; index < expected_arms.Size(); ++index)
        {
            const JsonObject expected_arm = expected_arms.GetObjectAt(index);
            expect_point_near(expected_arm.GetNamedObject(L"start"), actual.arms[index].start);
            expect_point_near(expected_arm.GetNamedObject(L"end"), actual.arms[index].end);
        }
    }
}

TEST(CrosshairGeometry, BoundsIncludeVisibleStrokesAndExcludeHiddenArms)
{
    const RECT monitor{-1920, 0, 0, 1080};
    const std::array<external_peepsight::CrosshairArmDefinition, 4> arms{
        external_peepsight::CrosshairArmDefinition{0.0F, 6.0F, 12.0F, 2.0F, true},
        external_peepsight::CrosshairArmDefinition{90.0F, 6.0F, 200.0F, 20.0F, false},
        external_peepsight::CrosshairArmDefinition{180.0F, 6.0F, 12.0F, 2.0F, true},
        external_peepsight::CrosshairArmDefinition{270.0F, 6.0F, 12.0F, 2.0F, true},
    };

    const RECT bounds =
        external_peepsight::calculate_crosshair_visual_bounds(monitor, true, {0.0F, 0.0F}, true, 2.0F, arms);

    EXPECT_GT(bounds.left, monitor.left);
    EXPECT_LT(bounds.right, monitor.right);
    EXPECT_EQ(519, bounds.top);
    EXPECT_EQ(561, bounds.bottom);
}
} // namespace
