#include "monitor_descriptor.h"

#include "diagnostics.h"

#include <algorithm>
#include <array>
#include <cwctype>
#include <format>
#include <stdexcept>
#include <unordered_map>

namespace external_peepsight
{
namespace
{
[[nodiscard]] std::wstring normalize_identifier(std::wstring_view value)
{
    std::wstring normalized(value);
    std::ranges::transform(normalized, normalized.begin(),
                           [](const wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
    return normalized;
}

[[nodiscard]] std::uint64_t fnv_one_a(const std::wstring_view value) noexcept
{
    constexpr std::uint64_t offset = 14'695'981'039'346'656'037ULL;
    constexpr std::uint64_t prime = 1'099'511'628'211ULL;
    std::uint64_t hash = offset;
    for (const wchar_t character : value)
    {
        const auto code = static_cast<std::uint32_t>(character);
        for (unsigned int shift = 0U; shift < 32U; shift += 8U)
        {
            hash ^= (code >> shift) & 0xFFU;
            hash *= prime;
        }
    }
    return hash;
}

[[nodiscard]] std::unordered_map<std::wstring, std::wstring> query_target_paths()
{
    UINT32 path_count = 0U;
    UINT32 mode_count = 0U;
    const LONG size_result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &path_count, &mode_count);
    if (size_result != ERROR_SUCCESS)
    {
        return {};
    }

    std::vector<DISPLAYCONFIG_PATH_INFO> paths(path_count);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(mode_count);
    LONG query_result =
        QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &path_count, paths.data(), &mode_count, modes.data(), nullptr);
    if (query_result != ERROR_SUCCESS)
    {
        return {};
    }
    paths.resize(path_count);

    std::unordered_map<std::wstring, std::wstring> result;
    for (const DISPLAYCONFIG_PATH_INFO &path : paths)
    {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = sizeof(source);
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;

        DISPLAYCONFIG_TARGET_DEVICE_NAME target{};
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = sizeof(target);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;

        if (DisplayConfigGetDeviceInfo(&source.header) == ERROR_SUCCESS &&
            DisplayConfigGetDeviceInfo(&target.header) == ERROR_SUCCESS)
        {
            result.emplace(normalize_identifier(source.viewGdiDeviceName), target.monitorDevicePath);
        }
    }
    return result;
}

BOOL CALLBACK enumerate_monitor_callback(_In_ const HMONITOR monitor, _In_opt_ HDC device_context,
                                         _In_ LPRECT monitor_rect, _In_ const LPARAM context)
{
    UNREFERENCED_PARAMETER(device_context);
    UNREFERENCED_PARAMETER(monitor_rect);

    auto &monitors = *reinterpret_cast<std::vector<MonitorDescriptor> *>(context);
    MONITORINFOEXW information{};
    information.cbSize = sizeof(information);
    if (!GetMonitorInfoW(monitor, &information))
    {
        return FALSE;
    }

    monitors.push_back({monitor, information.rcMonitor, information.szDevice, {}, {}});
    return TRUE;
}
} // namespace

MonitorId make_monitor_id(const std::wstring_view device_path, const std::wstring_view device_name,
                          const RECT &bounds_px)
{
    std::wstring source;
    if (!device_path.empty())
    {
        source = L"path:";
        source += normalize_identifier(device_path);
    }
    else if (!device_name.empty())
    {
        source = L"gdi:";
        source += normalize_identifier(device_name);
    }
    else
    {
        source = std::format(L"bounds:{},{},{},{}", bounds_px.left, bounds_px.top, bounds_px.right, bounds_px.bottom);
    }

    return MonitorId{std::format(L"monitor-{:016x}", fnv_one_a(source))};
}

std::vector<MonitorDescriptor> enumerate_monitors()
{
    std::vector<MonitorDescriptor> monitors;
    if (!EnumDisplayMonitors(nullptr, nullptr, enumerate_monitor_callback, reinterpret_cast<LPARAM>(&monitors)))
    {
        throw_last_error("EnumDisplayMonitors");
    }

    const auto target_paths = query_target_paths();
    for (MonitorDescriptor &monitor : monitors)
    {
        const auto path = target_paths.find(normalize_identifier(monitor.device_name));
        if (path != target_paths.end())
        {
            monitor.device_path = path->second;
        }
        monitor.id = make_monitor_id(monitor.device_path, monitor.device_name, monitor.bounds_px);
    }

    std::ranges::sort(monitors,
                      [](const MonitorDescriptor &left, const MonitorDescriptor &right)
                      {
                          if (left.bounds_px.top != right.bounds_px.top)
                          {
                              return left.bounds_px.top < right.bounds_px.top;
                          }
                          return left.bounds_px.left < right.bounds_px.left;
                      });

    if (monitors.empty())
    {
        throw std::runtime_error("No display monitors were enumerated.");
    }
    return monitors;
}
} // namespace external_peepsight
