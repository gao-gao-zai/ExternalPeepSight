#include "render_recovery.h"

#include <stdexcept>
#include <string>

namespace external_peepsight
{
void DeviceRecoveryStateMachine::mark_initialized()
{
    require(DeviceRecoveryState::cold, "Initial device creation");
    state_ = DeviceRecoveryState::ready;
}

void DeviceRecoveryStateMachine::begin_recovery()
{
    require(DeviceRecoveryState::ready, "Device recovery");
    state_ = DeviceRecoveryState::releasing;
}

void DeviceRecoveryStateMachine::mark_targets_released()
{
    require(DeviceRecoveryState::releasing, "Render target release");
    state_ = DeviceRecoveryState::recreating;
}

void DeviceRecoveryStateMachine::mark_recreated()
{
    require(DeviceRecoveryState::recreating, "Device recreation");
    state_ = DeviceRecoveryState::ready;
}

void DeviceRecoveryStateMachine::mark_failed() noexcept
{
    state_ = DeviceRecoveryState::failed;
}

DeviceRecoveryState DeviceRecoveryStateMachine::state() const noexcept
{
    return state_;
}

void DeviceRecoveryStateMachine::require(const DeviceRecoveryState expected, const char *operation) const
{
    if (state_ != expected)
    {
        throw std::logic_error(std::string(operation) + " occurred out of order.");
    }
}
} // namespace external_peepsight
