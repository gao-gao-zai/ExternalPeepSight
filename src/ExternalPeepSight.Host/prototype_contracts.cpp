#include "prototype_contracts.h"

namespace external_peepsight
{
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
