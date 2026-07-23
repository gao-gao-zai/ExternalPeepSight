#pragma once

#include <windows.h>

#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Persistent identifier for one physical display target.
struct MonitorId
{
    /// Stable normalized identifier value.
    std::wstring value;

    /// Compares persistent monitor identifiers.
    [[nodiscard]] bool operator==(const MonitorId &) const noexcept = default;
};

/// Describes one active monitor in physical virtual-desktop coordinates.
struct MonitorDescriptor
{
    /// Process-local monitor handle. Never persist this value.
    HMONITOR handle;
    /// Virtual-desktop bounds in physical pixels.
    RECT bounds_px;
    /// Taskbar-excluding work area used only for edge-aligned auxiliary content.
    RECT work_area_px;
    /// GDI display source name.
    std::wstring device_name;
    /// DisplayConfig target path when available.
    std::wstring device_path;
    /// Persistent identity derived from the target path or a documented fallback.
    MonitorId id;
};

/// Creates a normalized persistent monitor identifier.
///
/// The DisplayConfig target path is preferred. The GDI source name is used
/// only when Windows does not expose a target path.
[[nodiscard]] MonitorId make_monitor_id(std::wstring_view device_path, std::wstring_view device_name,
                                        const RECT &bounds_px);

/// Enumerates active monitors in deterministic virtual-desktop order.
[[nodiscard]] std::vector<MonitorDescriptor> enumerate_monitors();
} // namespace external_peepsight
