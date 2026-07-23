#pragma once

#include <windows.h>

#include <string>

namespace external_peepsight
{
/// Owns a security descriptor that grants full access only to SYSTEM and the current user.
class CurrentUserSecurity
{
  public:
    /// Builds the current-user security descriptor.
    CurrentUserSecurity();

    CurrentUserSecurity(const CurrentUserSecurity &) = delete;
    CurrentUserSecurity &operator=(const CurrentUserSecurity &) = delete;

    /// Releases the owned security descriptor.
    ~CurrentUserSecurity();

    /// Returns SECURITY_ATTRIBUTES backed by this object.
    [[nodiscard]] SECURITY_ATTRIBUTES *attributes() noexcept;

    /// Returns the current user's string SID.
    [[nodiscard]] const std::wstring &sid_string() const noexcept;

  private:
    std::wstring sid_string_;
    PSECURITY_DESCRIPTOR descriptor_ = nullptr;
    SECURITY_ATTRIBUTES attributes_{};
};
} // namespace external_peepsight
