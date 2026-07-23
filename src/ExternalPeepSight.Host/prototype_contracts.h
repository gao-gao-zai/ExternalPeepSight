#pragma once

#include <array>
#include <windows.h>

namespace external_peepsight
{
/// A two-dimensional position expressed in monitor-local physical pixels.
struct PointPx
{
    /// Horizontal monitor-local coordinate.
    float x;
    /// Vertical monitor-local coordinate.
    float y;
};

/// A line segment expressed in monitor-local physical pixels.
struct LineSegmentPx
{
    /// Segment start point.
    PointPx start;
    /// Segment end point.
    PointPx end;
};

/// Geometry for the center point and four crosshair arms.
struct CrosshairGeometry
{
    /// Center point of the target surface.
    PointPx center;
    /// Arms ordered as up, right, down, and left.
    std::array<LineSegmentPx, 4> arms;
};

/// Returns the extended styles required by a prototype overlay window.
[[nodiscard]] constexpr DWORD overlay_extended_style() noexcept
{
    return WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED |
           WS_EX_NOREDIRECTIONBITMAP;
}

/// Returns the base style required by a prototype overlay window.
[[nodiscard]] constexpr DWORD overlay_window_style() noexcept
{
    return WS_POPUP;
}

/// Returns the non-client hit-test result used to keep the overlay mouse transparent.
[[nodiscard]] constexpr LRESULT overlay_hit_test_result() noexcept
{
    return HTTRANSPARENT;
}

/// Calculates centered crosshair geometry in physical pixels.
///
/// Width and height are the target surface dimensions. Gap and arm length are
/// measured from the center towards each cardinal direction.
[[nodiscard]] CrosshairGeometry calculate_crosshair_geometry(float width_px, float height_px, float gap_px,
                                                             float arm_length_px) noexcept;
} // namespace external_peepsight
