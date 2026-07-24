#pragma once

#include <array>
#include <cstdint>
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

/// Defines one independently configurable crosshair arm.
struct CrosshairArmDefinition
{
    /// Clockwise orbit angle locating the arm around the center.
    float orbit_angle_deg;
    /// Clockwise rotation offset from the arm's outward radial direction.
    float rotation_angle_offset_deg;
    /// Distance from the center to the segment start in physical pixels.
    float gap_px;
    /// Segment length in physical pixels.
    float length_px;
    /// Stroke width in physical pixels.
    float width_px;
    /// Whether the arm contributes to rendering and visual bounds.
    bool visible;
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

/// Evaluates whether an overlay should be visible on its monitor.
///
/// Toast overlays ignore input switches. Profile overlays remain hidden until
/// the Host has received a configured switch visibility state.
[[nodiscard]] constexpr bool evaluate_overlay_visibility(bool monitor_selected, bool is_toast,
                                                         bool switch_visibility_configured,
                                                         bool switch_visibility) noexcept
{
    return monitor_selected && (is_toast || (switch_visibility_configured && switch_visibility));
}

/// Calculates the monitor-centered HWND bounds required by the prototype content.
///
/// The returned dimensions preserve the monitor's width and height parity so
/// the Direct2D surface center maps exactly to the full monitor center.
[[nodiscard]] RECT calculate_prototype_overlay_bounds(const RECT &monitor_bounds_px) noexcept;

/// Calculates centered crosshair geometry in physical pixels.
///
/// Width and height are the target surface dimensions. Gap and arm length are
/// measured from the center towards each cardinal direction.
[[nodiscard]] CrosshairGeometry calculate_crosshair_geometry(float width_px, float height_px, float gap_px,
                                                             float arm_length_px) noexcept;

/// Calculates independently configured crosshair geometry in physical pixels.
///
/// Angles increase clockwise from the upward axis. Offset values and the
/// returned coordinates use surface-local physical pixels.
[[nodiscard]] CrosshairGeometry calculate_crosshair_geometry(
    float width_px, float height_px, bool anchor_at_center, PointPx offset_px,
    const std::array<CrosshairArmDefinition, 4> &arms) noexcept;

/// Calculates a clipped virtual-desktop HWND bounds for visible crosshair content.
///
/// The monitor rectangle and returned bounds use virtual-desktop physical pixels.
/// Anti-aliasing padding is included so a tightly bounded window does not clip strokes.
[[nodiscard]] RECT calculate_crosshair_visual_bounds(const RECT &monitor_bounds_px, bool anchor_at_center,
                                                     PointPx offset_px, bool center_visible, float center_radius_px,
                                                     const std::array<CrosshairArmDefinition, 4> &arms) noexcept;
} // namespace external_peepsight
