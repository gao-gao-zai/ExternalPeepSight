#include "diagnostics.h"

#include <dxgi.h>

#include <filesystem>
#include <format>
#include <fstream>
#include <mutex>
#include <sstream>

namespace external_peepsight
{
namespace
{
constexpr std::uintmax_t kMaximumLogBytes = 2U * 1024U * 1024U;

[[nodiscard]] std::filesystem::path diagnostic_log_path()
{
    std::wstring local_app_data(32'768U, L'\0');
    const DWORD length =
        GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data.data(), static_cast<DWORD>(local_app_data.size()));
    if (length == 0U || length >= local_app_data.size())
    {
        return {};
    }

    local_app_data.resize(length);
    return std::filesystem::path(local_app_data) / L"ExternalPeepSight" / L"logs" / L"host.log";
}

void append_diagnostic_record(const std::string_view record)
{
    static std::mutex log_mutex;
    std::scoped_lock lock(log_mutex);

    const std::filesystem::path path = diagnostic_log_path();
    if (path.empty())
    {
        return;
    }

    std::filesystem::create_directories(path.parent_path());
    if (std::filesystem::exists(path) && std::filesystem::file_size(path) >= kMaximumLogBytes)
    {
        const std::filesystem::path backup = path.parent_path() / L"host.log.1";
        std::error_code ignored;
        std::filesystem::remove(backup, ignored);
        std::filesystem::rename(path, backup);
    }

    std::ofstream output(path, std::ios::binary | std::ios::app);
    output.write(record.data(), static_cast<std::streamsize>(record.size()));
}

[[nodiscard]] std::string escape_json(const std::string_view value)
{
    std::string result;
    result.reserve(value.size());
    for (const char character : value)
    {
        switch (character)
        {
        case '\\':
            result += "\\\\";
            break;
        case '"':
            result += "\\\"";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (static_cast<unsigned char>(character) < 0x20U)
            {
                result += std::format("\\u{:04x}", static_cast<unsigned int>(static_cast<unsigned char>(character)));
            }
            else
            {
                result.push_back(character);
            }
            break;
        }
    }
    return result;
}

[[nodiscard]] std::string_view level_name(const DiagnosticLevel level) noexcept
{
    switch (level)
    {
    case DiagnosticLevel::information:
        return "information";
    case DiagnosticLevel::warning:
        return "warning";
    case DiagnosticLevel::error:
        return "error";
    }
    return "unknown";
}

[[nodiscard]] std::string_view domain_name(const NativeErrorDomain domain) noexcept
{
    switch (domain)
    {
    case NativeErrorDomain::win32:
        return "win32";
    case NativeErrorDomain::hresult:
        return "hresult";
    }
    return "unknown";
}
} // namespace

NativeError::NativeError(const HRESULT result, const std::string_view operation)
    : std::runtime_error(
          std::format("{} failed with HRESULT 0x{:08X}.", operation, static_cast<std::uint32_t>(result))),
      status_{NativeErrorDomain::hresult, static_cast<std::uint32_t>(result)}
{
}

NativeError::NativeError(const DWORD result, const std::string_view operation)
    : std::runtime_error(std::format("{} failed with Win32 error {}.", operation, result)),
      status_{NativeErrorDomain::win32, result}
{
}

NativeErrorStatus NativeError::status() const noexcept
{
    return status_;
}

bool NativeError::is_device_lost() const noexcept
{
    if (status_.domain != NativeErrorDomain::hresult)
    {
        return false;
    }

    const HRESULT result = static_cast<HRESULT>(status_.code);
    return result == DXGI_ERROR_DEVICE_HUNG || result == DXGI_ERROR_DEVICE_REMOVED ||
           result == DXGI_ERROR_DEVICE_RESET || result == DXGI_ERROR_DRIVER_INTERNAL_ERROR;
}

std::string format_diagnostic(const DiagnosticLevel level, const std::string_view event_name,
                              const std::string_view message, const std::optional<NativeErrorStatus> status)
{
    SYSTEMTIME time{};
    GetSystemTime(&time);
    std::ostringstream output;
    output << "{\"timestampUtc\":\""
           << std::format("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z", time.wYear, time.wMonth, time.wDay, time.wHour,
                          time.wMinute, time.wSecond, time.wMilliseconds)
           << "\",\"processId\":" << GetCurrentProcessId() << ",\"component\":\"ExternalPeepSight.Host\",\"level\":\""
           << level_name(level) << "\",\"event\":\"" << escape_json(event_name) << "\",\"message\":\""
           << escape_json(message) << '"';
    if (status.has_value())
    {
        output << ",\"nativeDomain\":\"" << domain_name(status->domain) << "\",\"nativeCode\":\"0x" << std::hex
               << std::uppercase << status->code << '"';
    }
    output << "}\n";
    return output.str();
}

void log_diagnostic(const DiagnosticLevel level, const std::string_view event_name, const std::string_view message,
                    const std::optional<NativeErrorStatus> status) noexcept
{
    try
    {
        const std::string record = format_diagnostic(level, event_name, message, status);
        OutputDebugStringA(record.c_str());
        append_diagnostic_record(record);
    }
    catch (...)
    {
        OutputDebugStringA("{\"component\":\"ExternalPeepSight.Host\",\"level\":\"error\","
                           "\"event\":\"diagnostic_format_failure\"}\n");
    }
}

void throw_if_failed(const HRESULT result, const std::string_view operation)
{
    if (FAILED(result))
    {
        throw NativeError(result, operation);
    }
}

[[noreturn]] void throw_last_error(const std::string_view operation)
{
    const DWORD result = GetLastError();
    throw NativeError(result, operation);
}
} // namespace external_peepsight
