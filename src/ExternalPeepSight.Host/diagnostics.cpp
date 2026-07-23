#include "diagnostics.h"

#include <dxgi.h>

#include <format>
#include <sstream>

namespace external_peepsight
{
namespace
{
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
    std::ostringstream output;
    output << "{\"component\":\"ExternalPeepSight.Host\",\"level\":\"" << level_name(level) << "\",\"event\":\""
           << escape_json(event_name) << "\",\"message\":\"" << escape_json(message) << '"';
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
