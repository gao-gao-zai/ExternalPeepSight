#pragma once

#include "monitor_descriptor.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <vector>

namespace external_peepsight
{
/// Immutable monitor state consumed by Host worker threads.
struct SnapshotMonitor
{
    /// Persistent monitor identity.
    MonitorId id;
    /// Virtual-desktop bounds in physical pixels.
    RECT bounds_px;
};

/// Immutable configuration and display state published to Host worker threads.
struct HostSnapshot
{
    /// Strictly increasing snapshot version.
    std::uint64_t version;
    /// Current active monitor topology.
    std::vector<SnapshotMonitor> monitors;
    /// Whether the overlay is visible on every monitor.
    bool all_monitors;
};

/// Result of attempting to publish an immutable Host snapshot.
enum class SnapshotPublishResult
{
    published,
    stale_version,
    invalid_snapshot,
};

/// Atomically publishes complete immutable Host snapshots.
class AtomicHostSnapshot
{
  public:
    /// Publishes a snapshot only when it is valid and newer than the current value.
    [[nodiscard]] SnapshotPublishResult publish(std::shared_ptr<const HostSnapshot> snapshot) noexcept;

    /// Loads one internally consistent snapshot.
    [[nodiscard]] std::shared_ptr<const HostSnapshot> load() const noexcept;

  private:
    std::atomic<std::shared_ptr<const HostSnapshot>> current_;
};
} // namespace external_peepsight
