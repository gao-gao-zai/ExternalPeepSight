#pragma once

namespace external_peepsight
{
/// Ordered lifecycle states for render-device recovery.
enum class DeviceRecoveryState
{
    cold,
    ready,
    releasing,
    recreating,
    failed,
};

/// Enforces release-before-recreate ordering after device loss.
class DeviceRecoveryStateMachine
{
  public:
    /// Marks initial device creation as complete.
    void mark_initialized();

    /// Starts recovery and requires render targets to be released next.
    void begin_recovery();

    /// Marks all device-dependent targets as released.
    void mark_targets_released();

    /// Marks device and target recreation as complete.
    void mark_recreated();

    /// Moves the state machine to a terminal failure state.
    void mark_failed() noexcept;

    /// Returns the current recovery state.
    [[nodiscard]] DeviceRecoveryState state() const noexcept;

  private:
    void require(DeviceRecoveryState expected, const char *operation) const;

    DeviceRecoveryState state_ = DeviceRecoveryState::cold;
};
} // namespace external_peepsight
