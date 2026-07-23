#pragma once

#include <windows.h>

#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>

namespace external_peepsight
{
/// Identifies the native subsystem that produced an error code.
enum class NativeErrorDomain
{
    win32,
    hresult,
};

/// Preserves a native error code together with its originating subsystem.
struct NativeErrorStatus
{
    /// Native subsystem that defines the code.
    NativeErrorDomain domain;
    /// Unmodified native error code.
    std::uint32_t code;
};

/// Exception that preserves the native error code for diagnostics and recovery decisions.
class NativeError final : public std::runtime_error
{
  public:
    /// Creates an error for a failed HRESULT operation.
    NativeError(HRESULT result, std::string_view operation);

    /// Creates an error for a failed Win32 operation.
    NativeError(DWORD result, std::string_view operation);

    /// Returns the preserved native status.
    [[nodiscard]] NativeErrorStatus status() const noexcept;

    /// Returns whether the error represents a lost D3D or DXGI device.
    [[nodiscard]] bool is_device_lost() const noexcept;

  private:
    NativeErrorStatus status_;
};

/// Severity assigned to a structured diagnostic event.
enum class DiagnosticLevel
{
    information,
    warning,
    error,
};

/// Formats one structured diagnostic record as a single JSON line.
[[nodiscard]] std::string format_diagnostic(DiagnosticLevel level, std::string_view event_name,
                                            std::string_view message,
                                            std::optional<NativeErrorStatus> status = std::nullopt);

/// Writes one structured diagnostic record to the debugger.
void log_diagnostic(DiagnosticLevel level, std::string_view event_name, std::string_view message,
                    std::optional<NativeErrorStatus> status = std::nullopt) noexcept;

/// Throws NativeError when result indicates a failed HRESULT operation.
void throw_if_failed(HRESULT result, std::string_view operation);

/// Captures GetLastError and throws NativeError for a failed Win32 operation.
[[noreturn]] void throw_last_error(std::string_view operation);
} // namespace external_peepsight
