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
