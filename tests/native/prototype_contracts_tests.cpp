#include "prototype_contracts.h"

#include <gtest/gtest.h>

namespace
{
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
} // namespace
