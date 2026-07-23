#include "single_instance.h"

#include "current_user_security.h"
#include "diagnostics.h"

#include <stdexcept>
#include <string>

namespace external_peepsight
{
namespace
{
[[nodiscard]] std::wstring validate_instance_id(const std::wstring_view instance_id)
{
    if (instance_id.empty() || instance_id.size() > 64U)
    {
        throw std::invalid_argument("Host instance identifier length is invalid.");
    }
    for (const wchar_t character : instance_id)
    {
        const bool ascii_alphanumeric = (character >= L'0' && character <= L'9') ||
                                        (character >= L'A' && character <= L'Z') ||
                                        (character >= L'a' && character <= L'z');
        if (!ascii_alphanumeric && character != L'-' && character != L'_')
        {
            throw std::invalid_argument("Host instance identifier contains an invalid character.");
        }
    }
    return std::wstring(instance_id);
}
} // namespace

SingleInstanceGuard::SingleInstanceGuard(const std::wstring_view instance_id)
{
    CurrentUserSecurity security;
    const std::wstring name = L"Local\\ExternalPeepSight.Host." + validate_instance_id(instance_id);
    mutex_ = CreateMutexW(security.attributes(), TRUE, name.c_str());
    if (mutex_ == nullptr)
    {
        throw_last_error("CreateMutexW Host single instance");
    }

    already_running_ = GetLastError() == ERROR_ALREADY_EXISTS;
    owns_mutex_ = !already_running_;
}

SingleInstanceGuard::~SingleInstanceGuard()
{
    if (owns_mutex_)
    {
        ReleaseMutex(mutex_);
    }
    if (mutex_ != nullptr)
    {
        CloseHandle(mutex_);
    }
}

bool SingleInstanceGuard::already_running() const noexcept
{
    return already_running_;
}
} // namespace external_peepsight
