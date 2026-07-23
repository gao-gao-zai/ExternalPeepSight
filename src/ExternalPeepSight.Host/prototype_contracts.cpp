#include "prototype_contracts.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace external_peepsight
{
namespace
{
constexpr LONG kPrototypeMinimumWidthPx = 128;
constexpr LONG kPrototypeMinimumHeightPx = 64;
constexpr float kBoundsPaddingPx = 2.0F;

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

[[nodiscard]] PointPx anchor_point(const float width_px, const float height_px, const bool anchor_at_center,
                                   const PointPx offset_px) noexcept
{
    const PointPx anchor = anchor_at_center ? PointPx{width_px / 2.0F, height_px / 2.0F} : PointPx{0.0F, 0.0F};
    return {anchor.x + offset_px.x, anchor.y + offset_px.y};
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
    const std::array<CrosshairArmDefinition, 4> arms{
        CrosshairArmDefinition{0.0F, gap_px, arm_length_px, 1.0F, true},
        CrosshairArmDefinition{90.0F, gap_px, arm_length_px, 1.0F, true},
        CrosshairArmDefinition{180.0F, gap_px, arm_length_px, 1.0F, true},
        CrosshairArmDefinition{270.0F, gap_px, arm_length_px, 1.0F, true},
    };
    return calculate_crosshair_geometry(width_px, height_px, true, {0.0F, 0.0F}, arms);
}

CrosshairGeometry calculate_crosshair_geometry(const float width_px, const float height_px, const bool anchor_at_center,
                                               const PointPx offset_px,
                                               const std::array<CrosshairArmDefinition, 4> &arms) noexcept
{
    const PointPx center = anchor_point(width_px, height_px, anchor_at_center, offset_px);
    CrosshairGeometry geometry{center, {}};
    constexpr float radians_per_degree = 3.14159265358979323846F / 180.0F;
    for (std::size_t index = 0U; index < arms.size(); ++index)
    {
        const float radians = arms[index].angle_deg * radians_per_degree;
        const PointPx direction{std::sin(radians), -std::cos(radians)};
        const PointPx start{center.x + direction.x * arms[index].gap_px, center.y + direction.y * arms[index].gap_px};
        geometry.arms[index] = {
            start,
            {start.x + direction.x * arms[index].length_px, start.y + direction.y * arms[index].length_px},
        };
    }
    return geometry;
}

RECT calculate_crosshair_visual_bounds(const RECT &monitor_bounds_px, const bool anchor_at_center,
                                       const PointPx offset_px, const bool center_visible, const float center_radius_px,
                                       const std::array<CrosshairArmDefinition, 4> &arms) noexcept
{
    const LONG monitor_width_px = monitor_bounds_px.right - monitor_bounds_px.left;
    const LONG monitor_height_px = monitor_bounds_px.bottom - monitor_bounds_px.top;
    if (monitor_width_px <= 0 || monitor_height_px <= 0)
    {
        return monitor_bounds_px;
    }

    const CrosshairGeometry geometry = calculate_crosshair_geometry(
        static_cast<float>(monitor_width_px), static_cast<float>(monitor_height_px), anchor_at_center, offset_px, arms);
    float left = std::numeric_limits<float>::infinity();
    float top = std::numeric_limits<float>::infinity();
    float right = -std::numeric_limits<float>::infinity();
    float bottom = -std::numeric_limits<float>::infinity();
    const auto include = [&left, &top, &right, &bottom](const PointPx point, const float radius_px)
    {
        left = (std::min)(left, point.x - radius_px);
        top = (std::min)(top, point.y - radius_px);
        right = (std::max)(right, point.x + radius_px);
        bottom = (std::max)(bottom, point.y + radius_px);
    };

    if (center_visible)
    {
        include(geometry.center, center_radius_px + kBoundsPaddingPx);
    }
    for (std::size_t index = 0U; index < arms.size(); ++index)
    {
        if (!arms[index].visible)
        {
            continue;
        }
        const float radius_px = arms[index].width_px / 2.0F + kBoundsPaddingPx;
        include(geometry.arms[index].start, radius_px);
        include(geometry.arms[index].end, radius_px);
    }

    if (!std::isfinite(left))
    {
        const LONG x = (std::clamp)(monitor_bounds_px.left + static_cast<LONG>(geometry.center.x),
                                    monitor_bounds_px.left, monitor_bounds_px.right - 1L);
        const LONG y = (std::clamp)(monitor_bounds_px.top + static_cast<LONG>(geometry.center.y), monitor_bounds_px.top,
                                    monitor_bounds_px.bottom - 1L);
        return {x, y, x + 1L, y + 1L};
    }

    const LONG global_left = monitor_bounds_px.left + static_cast<LONG>(std::floor(left));
    const LONG global_top = monitor_bounds_px.top + static_cast<LONG>(std::floor(top));
    const LONG global_right = monitor_bounds_px.left + static_cast<LONG>(std::ceil(right));
    const LONG global_bottom = monitor_bounds_px.top + static_cast<LONG>(std::ceil(bottom));
    RECT result{
        (std::clamp)(global_left, monitor_bounds_px.left, monitor_bounds_px.right - 1L),
        (std::clamp)(global_top, monitor_bounds_px.top, monitor_bounds_px.bottom - 1L),
        (std::clamp)(global_right, monitor_bounds_px.left + 1L, monitor_bounds_px.right),
        (std::clamp)(global_bottom, monitor_bounds_px.top + 1L, monitor_bounds_px.bottom),
    };
    if (result.right <= result.left)
    {
        result.right = result.left + 1L;
    }
    if (result.bottom <= result.top)
    {
        result.bottom = result.top + 1L;
    }
    return result;
}
} // namespace external_peepsight
