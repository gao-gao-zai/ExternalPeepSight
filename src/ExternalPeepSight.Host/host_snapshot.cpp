#include "host_snapshot.h"

#include <unordered_set>

namespace external_peepsight
{
namespace
{
[[nodiscard]] bool is_valid(const HostSnapshot &snapshot)
{
    if (snapshot.version == 0U || snapshot.monitors.empty() || !snapshot.render_configuration)
    {
        return false;
    }

    std::unordered_set<std::wstring> ids;
    for (const SnapshotMonitor &monitor : snapshot.monitors)
    {
        if (monitor.id.value.empty() || monitor.bounds_px.right <= monitor.bounds_px.left ||
            monitor.bounds_px.bottom <= monitor.bounds_px.top || !ids.insert(monitor.id.value).second)
        {
            return false;
        }
    }
    return true;
}
} // namespace

SnapshotPublishResult AtomicHostSnapshot::publish(std::shared_ptr<const HostSnapshot> snapshot) noexcept
{
    if (!snapshot || !is_valid(*snapshot))
    {
        return SnapshotPublishResult::invalid_snapshot;
    }

    std::shared_ptr<const HostSnapshot> observed = current_.load(std::memory_order_acquire);
    while (!observed || snapshot->version > observed->version)
    {
        if (current_.compare_exchange_weak(observed, snapshot, std::memory_order_release, std::memory_order_acquire))
        {
            return SnapshotPublishResult::published;
        }
    }
    return SnapshotPublishResult::stale_version;
}

std::shared_ptr<const HostSnapshot> AtomicHostSnapshot::load() const noexcept
{
    return current_.load(std::memory_order_acquire);
}
} // namespace external_peepsight
