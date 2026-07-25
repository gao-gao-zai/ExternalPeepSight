#include "current_user_security.h"

#include "diagnostics.h"

#include <sddl.h>

#include <cstddef>
#include <memory>
#include <vector>

namespace external_peepsight
{
namespace
{
struct LocalFreeDeleter
{
    void operator()(void *value) const noexcept
    {
        if (value != nullptr)
        {
            LocalFree(value);
        }
    }
};

struct HandleCloser
{
    void operator()(void *value) const noexcept
    {
        if (value != nullptr && value != INVALID_HANDLE_VALUE)
        {
            CloseHandle(value);
        }
    }
};

using LocalPointer = std::unique_ptr<void, LocalFreeDeleter>;
using UniqueHandle = std::unique_ptr<void, HandleCloser>;

[[nodiscard]] std::wstring current_user_sid()
{
    HANDLE token_handle = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token_handle))
    {
        throw_last_error("OpenProcessToken");
    }
    UniqueHandle token(token_handle);

    DWORD required_bytes = 0U;
    GetTokenInformation(token.get(), TokenUser, nullptr, 0U, &required_bytes);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || required_bytes == 0U)
    {
        throw_last_error("GetTokenInformation size");
    }

    std::vector<std::byte> buffer(required_bytes);
    if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), required_bytes, &required_bytes))
    {
        throw_last_error("GetTokenInformation");
    }

    const auto *token_user = reinterpret_cast<const TOKEN_USER *>(buffer.data());
    LPWSTR sid_text = nullptr;
    if (!ConvertSidToStringSidW(token_user->User.Sid, &sid_text))
    {
        throw_last_error("ConvertSidToStringSidW");
    }
    LocalPointer sid(sid_text);
    return static_cast<const wchar_t *>(sid.get());
}
} // namespace

bool is_current_process_elevated()
{
    HANDLE token_handle = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token_handle))
    {
        throw_last_error("OpenProcessToken elevation");
    }
    UniqueHandle token(token_handle);

    TOKEN_ELEVATION elevation{};
    DWORD returned_bytes = 0U;
    if (!GetTokenInformation(token.get(), TokenElevation, &elevation, sizeof(elevation), &returned_bytes))
    {
        throw_last_error("GetTokenInformation elevation");
    }
    if (returned_bytes != sizeof(elevation))
    {
        throw NativeError(ERROR_INVALID_DATA, "GetTokenInformation elevation size");
    }
    return elevation.TokenIsElevated != 0U;
}

CurrentUserSecurity::CurrentUserSecurity() : sid_string_(current_user_sid())
{
    const std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;" + sid_string_ + L")";
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor_, nullptr))
    {
        throw_last_error("ConvertStringSecurityDescriptorToSecurityDescriptorW");
    }

    attributes_.nLength = sizeof(attributes_);
    attributes_.lpSecurityDescriptor = descriptor_;
    attributes_.bInheritHandle = FALSE;
}

CurrentUserSecurity::~CurrentUserSecurity()
{
    if (descriptor_ != nullptr)
    {
        LocalFree(descriptor_);
    }
}

SECURITY_ATTRIBUTES *CurrentUserSecurity::attributes() noexcept
{
    return &attributes_;
}

const std::wstring &CurrentUserSecurity::sid_string() const noexcept
{
    return sid_string_;
}
} // namespace external_peepsight
