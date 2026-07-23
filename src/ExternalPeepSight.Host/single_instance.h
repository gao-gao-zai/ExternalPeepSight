#pragma once

#include <windows.h>

#include <string_view>

namespace external_peepsight
{
/// Owns the per-user Host single-instance mutex.
class SingleInstanceGuard
{
  public:
    /// Creates or opens the mutex for one instance namespace.
    explicit SingleInstanceGuard(std::wstring_view instance_id);

    SingleInstanceGuard(const SingleInstanceGuard &) = delete;
    SingleInstanceGuard &operator=(const SingleInstanceGuard &) = delete;

    /// Releases mutex ownership and closes the handle.
    ~SingleInstanceGuard();

    /// Returns whether another Host already owns the mutex.
    [[nodiscard]] bool already_running() const noexcept;

  private:
    HANDLE mutex_ = nullptr;
    bool owns_mutex_ = false;
    bool already_running_ = false;
};
} // namespace external_peepsight
