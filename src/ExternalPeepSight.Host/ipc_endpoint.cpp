#include "ipc_endpoint.h"

#include "diagnostics.h"
#include "ipc_protocol.h"

#include <bcrypt.h>
#include <objbase.h>
#include <shlobj.h>

#include <algorithm>
#include <array>
#include <format>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace external_peepsight
{
namespace
{
[[nodiscard]] std::wstring validate_instance_id(const std::wstring_view instance_id)
{
    if (instance_id.empty() || instance_id.size() > 64U)
    {
        throw std::invalid_argument("IPC instance identifier length is invalid.");
    }
    for (const wchar_t character : instance_id)
    {
        const bool ascii_alphanumeric = (character >= L'0' && character <= L'9') ||
                                        (character >= L'A' && character <= L'Z') ||
                                        (character >= L'a' && character <= L'z');
        if (!ascii_alphanumeric && character != L'-' && character != L'_')
        {
            throw std::invalid_argument("IPC instance identifier contains an invalid character.");
        }
    }
    return std::wstring(instance_id);
}

[[nodiscard]] std::string random_token()
{
    std::array<unsigned char, 32> bytes{};
    const NTSTATUS result =
        BCryptGenRandom(nullptr, bytes.data(), static_cast<ULONG>(bytes.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (result < 0)
    {
        throw NativeError(static_cast<HRESULT>(result), "BCryptGenRandom");
    }

    std::string token;
    token.reserve(bytes.size() * 2U);
    for (const unsigned char value : bytes)
    {
        token += std::format("{:02x}", value);
    }
    return token;
}

[[nodiscard]] std::wstring random_pipe_name(const std::wstring_view instance_id)
{
    GUID identifier{};
    throw_if_failed(CoCreateGuid(&identifier), "CoCreateGuid");

    std::array<wchar_t, 39> guid_text{};
    if (StringFromGUID2(identifier, guid_text.data(), static_cast<int>(guid_text.size())) == 0)
    {
        throw std::runtime_error("StringFromGUID2 failed.");
    }

    std::wstring normalized(guid_text.data());
    normalized.erase(std::remove(normalized.begin(), normalized.end(), L'{'), normalized.end());
    normalized.erase(std::remove(normalized.begin(), normalized.end(), L'}'), normalized.end());
    return L"\\\\.\\pipe\\ExternalPeepSight." + std::wstring(instance_id) + L"." + normalized;
}

[[nodiscard]] std::filesystem::path discovery_path(const std::wstring_view instance_id)
{
    PWSTR local_app_data = nullptr;
    throw_if_failed(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &local_app_data),
                    "SHGetKnownFolderPath LocalAppData");
    const std::filesystem::path root(local_app_data);
    CoTaskMemFree(local_app_data);
    return root / L"ExternalPeepSight" / (L"host-" + std::wstring(instance_id) + L".endpoint");
}

[[nodiscard]] std::filesystem::path graceful_shutdown_path(const std::filesystem::path &discovery_file)
{
    std::filesystem::path result = discovery_file;
    result.replace_extension(L".shutdown");
    return result;
}

[[nodiscard]] std::string narrow_ascii(const std::wstring_view value)
{
    std::string result;
    result.reserve(value.size());
    for (const wchar_t character : value)
    {
        if (character > 0x7F)
        {
            throw std::runtime_error("IPC endpoint contains a non-ASCII character.");
        }
        result.push_back(static_cast<char>(character));
    }
    return result;
}
} // namespace

IpcEndpointRegistration::IpcEndpointRegistration(const std::wstring_view instance_id, const bool publish_discovery)
{
    const std::wstring validated = validate_instance_id(instance_id);
    endpoint_.pipe_name = random_pipe_name(validated);
    endpoint_.handshake_token = random_token();
    endpoint_.discovery_file = discovery_path(validated);
    endpoint_.graceful_shutdown_file = graceful_shutdown_path(endpoint_.discovery_file);
    std::error_code removal_error;
    std::filesystem::remove(endpoint_.graceful_shutdown_file, removal_error);
    if (removal_error)
    {
        throw std::filesystem::filesystem_error("Unable to remove stale graceful shutdown marker.",
                                                endpoint_.graceful_shutdown_file, removal_error);
    }
    if (publish_discovery)
    {
        publish();
    }
}

IpcEndpointRegistration::~IpcEndpointRegistration()
{
    if (!published_)
    {
        return;
    }

    std::error_code error;
    std::filesystem::remove(endpoint_.discovery_file, error);
    if (error)
    {
        log_diagnostic(DiagnosticLevel::warning, "ipc.discovery_remove_failed", error.message());
    }
}

const IpcEndpoint &IpcEndpointRegistration::endpoint() const noexcept
{
    return endpoint_;
}

void IpcEndpointRegistration::mark_graceful_shutdown()
{
    if (!published_)
    {
        return;
    }

    const std::filesystem::path parent = endpoint_.graceful_shutdown_file.parent_path();
    std::filesystem::create_directories(parent);
    const std::filesystem::path temporary = endpoint_.graceful_shutdown_file.native() + L".tmp";

    {
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        if (!output)
        {
            throw std::runtime_error("Unable to create the graceful shutdown marker.");
        }
        output << "processId=" << GetCurrentProcessId() << '\n';
        output.flush();
        if (!output)
        {
            throw std::runtime_error("Unable to write the graceful shutdown marker.");
        }
    }

    if (!MoveFileExW(temporary.c_str(), endpoint_.graceful_shutdown_file.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        const DWORD error = GetLastError();
        std::filesystem::remove(temporary);
        throw NativeError(error, "MoveFileExW graceful shutdown marker");
    }
}

void IpcEndpointRegistration::publish()
{
    const std::filesystem::path parent = endpoint_.discovery_file.parent_path();
    std::filesystem::create_directories(parent);
    const std::filesystem::path temporary = endpoint_.discovery_file.native() + L".tmp";

    {
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        if (!output)
        {
            throw std::runtime_error("Unable to create the IPC discovery file.");
        }
        output << "protocolVersion=" << kIpcProtocolVersion << '\n';
        output << "pipeName=" << narrow_ascii(endpoint_.pipe_name) << '\n';
        output << "token=" << endpoint_.handshake_token << '\n';
        output << "processId=" << GetCurrentProcessId() << '\n';
        output.flush();
        if (!output)
        {
            throw std::runtime_error("Unable to write the IPC discovery file.");
        }
    }

    if (!MoveFileExW(temporary.c_str(), endpoint_.discovery_file.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        const DWORD error = GetLastError();
        std::filesystem::remove(temporary);
        throw NativeError(error, "MoveFileExW IPC discovery");
    }
    published_ = true;
}
} // namespace external_peepsight
