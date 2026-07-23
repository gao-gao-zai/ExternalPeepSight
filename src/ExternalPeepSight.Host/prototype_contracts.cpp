#include "prototype_contracts.h"

namespace external_peepsight
{
namespace
{
constexpr LONG kPrototypeMinimumWidthPx = 128;
constexpr LONG kPrototypeMinimumHeightPx = 64;

[[nodiscard]] LONG centered_extent(const LONG container_extent_px, const LONG minimum_extent_px) noexcept
{
    if (container_extent_px <= minimum_extent_px)
    {
        return container_extent_px;
    }

    LONG extent_px = minimum_extent_px;
    if ((extent_px & 1L) != (container_extent_px & 1L))
    {
        ++extent_px;
    }
    return extent_px;
}
} // namespace

RECT calculate_prototype_overlay_bounds(const RECT &monitor_bounds_px) noexcept
{
    const LONG monitor_width_px = monitor_bounds_px.right - monitor_bounds_px.left;
    const LONG monitor_height_px = monitor_bounds_px.bottom - monitor_bounds_px.top;
    if (monitor_width_px <= 0 || monitor_height_px <= 0)
    {
        return monitor_bounds_px;
    }

    const LONG width_px = centered_extent(monitor_width_px, kPrototypeMinimumWidthPx);
    const LONG height_px = centered_extent(monitor_height_px, kPrototypeMinimumHeightPx);
    const LONG left_px = monitor_bounds_px.left + ((monitor_width_px - width_px) / 2L);
    const LONG top_px = monitor_bounds_px.top + ((monitor_height_px - height_px) / 2L);
    return {left_px, top_px, left_px + width_px, top_px + height_px};
}

CrosshairGeometry calculate_crosshair_geometry(const float width_px, const float height_px, const float gap_px,
                                               const float arm_length_px) noexcept
{
    const PointPx center{width_px / 2.0F, height_px / 2.0F};

    return {
        center,
        {
            LineSegmentPx{{center.x, center.y - gap_px}, {center.x, center.y - gap_px - arm_length_px}},
            LineSegmentPx{{center.x + gap_px, center.y}, {center.x + gap_px + arm_length_px, center.y}},
            LineSegmentPx{{center.x, center.y + gap_px}, {center.x, center.y + gap_px + arm_length_px}},
            LineSegmentPx{{center.x - gap_px, center.y}, {center.x - gap_px - arm_length_px, center.y}},
        },
    };
}
} // namespace external_peepsight
