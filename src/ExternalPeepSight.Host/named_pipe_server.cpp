#include "named_pipe_server.h"

#include "current_user_security.h"
#include "diagnostics.h"

#include <winrt/base.h>

#include <array>
#include <cstring>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace external_peepsight
{
namespace
{
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

using UniqueHandle = std::unique_ptr<void, HandleCloser>;

[[nodiscard]] UniqueHandle create_event()
{
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (event == nullptr)
    {
        throw_last_error("CreateEventW Named Pipe");
    }
    return UniqueHandle(event);
}

[[nodiscard]] bool complete_overlapped(_In_ const HANDLE pipe, _Inout_ OVERLAPPED &overlapped,
                                       const std::stop_token stop_token, _Out_ DWORD &transferred)
{
    std::stop_callback cancel(stop_token, [pipe, &overlapped] { CancelIoEx(pipe, &overlapped); });
    if (!GetOverlappedResult(pipe, &overlapped, &transferred, TRUE))
    {
        const DWORD error = GetLastError();
        if ((error == ERROR_OPERATION_ABORTED && stop_token.stop_requested()) || error == ERROR_BROKEN_PIPE ||
            error == ERROR_NO_DATA)
        {
            return false;
        }
        throw NativeError(error, "GetOverlappedResult Named Pipe");
    }
    return true;
}

[[nodiscard]] bool connect_client(_In_ const HANDLE pipe, const std::stop_token stop_token)
{
    const UniqueHandle event = create_event();
    OVERLAPPED overlapped{};
    overlapped.hEvent = event.get();
    if (ConnectNamedPipe(pipe, &overlapped))
    {
        return true;
    }

    const DWORD error = GetLastError();
    if (error == ERROR_PIPE_CONNECTED)
    {
        return true;
    }
    if (error != ERROR_IO_PENDING)
    {
        throw NativeError(error, "ConnectNamedPipe");
    }

    DWORD transferred = 0U;
    return complete_overlapped(pipe, overlapped, stop_token, transferred);
}

[[nodiscard]] std::optional<std::string> read_message(_In_ const HANDLE pipe, const std::stop_token stop_token)
{
    std::vector<std::byte> buffer(kMaximumIpcMessageBytes + sizeof(std::uint32_t));
    const UniqueHandle event = create_event();
    OVERLAPPED overlapped{};
    overlapped.hEvent = event.get();

    if (!ReadFile(pipe, buffer.data(), static_cast<DWORD>(buffer.size()), nullptr, &overlapped))
    {
        const DWORD error = GetLastError();
        if (error != ERROR_IO_PENDING)
        {
            if (error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA)
            {
                return std::nullopt;
            }
            if (error == ERROR_MORE_DATA)
            {
                throw std::length_error("IPC message exceeds the maximum size.");
            }
            throw NativeError(error, "ReadFile Named Pipe");
        }
    }

    DWORD transferred = 0U;
    if (!complete_overlapped(pipe, overlapped, stop_token, transferred))
    {
        return std::nullopt;
    }
    if (transferred < sizeof(std::uint32_t))
    {
        throw std::invalid_argument("IPC frame is shorter than its length prefix.");
    }

    std::uint32_t encoded_size = 0U;
    std::memcpy(&encoded_size, buffer.data(), sizeof(encoded_size));
    if (encoded_size == 0U || encoded_size > kMaximumIpcMessageBytes ||
        transferred != encoded_size + sizeof(encoded_size))
    {
        throw std::invalid_argument("IPC frame length does not match its payload.");
    }

    return std::string(reinterpret_cast<const char *>(buffer.data() + sizeof(encoded_size)), encoded_size);
}

[[nodiscard]] bool write_message(_In_ const HANDLE pipe, const std::string_view message,
                                 const std::stop_token stop_token)
{
    if (message.empty() || message.size() > kMaximumIpcMessageBytes)
    {
        throw std::length_error("IPC response exceeds the maximum size.");
    }

    const std::uint32_t encoded_size = static_cast<std::uint32_t>(message.size());
    std::vector<std::byte> buffer(sizeof(encoded_size) + message.size());
    std::memcpy(buffer.data(), &encoded_size, sizeof(encoded_size));
    std::memcpy(buffer.data() + sizeof(encoded_size), message.data(), message.size());

    const UniqueHandle event = create_event();
    OVERLAPPED overlapped{};
    overlapped.hEvent = event.get();
    if (!WriteFile(pipe, buffer.data(), static_cast<DWORD>(buffer.size()), nullptr, &overlapped))
    {
        const DWORD error = GetLastError();
        if (error != ERROR_IO_PENDING)
        {
            if (error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA)
            {
                return false;
            }
            throw NativeError(error, "WriteFile Named Pipe");
        }
    }

    DWORD transferred = 0U;
    return complete_overlapped(pipe, overlapped, stop_token, transferred) &&
           transferred == static_cast<DWORD>(buffer.size());
}

void serve_client(_In_ const HANDLE pipe, const IpcEndpoint &endpoint, IpcHostState &host_state,
                  const std::stop_token stop_token)
{
    IpcSession session(host_state, endpoint.handshake_token);
    while (!stop_token.stop_requested())
    {
        std::optional<std::string> message;
        try
        {
            message = read_message(pipe, stop_token);
        }
        catch (const std::exception &error)
        {
            log_diagnostic(DiagnosticLevel::warning, "ipc.frame_rejected", error.what());
            return;
        }
        if (!message)
        {
            return;
        }

        const IpcSessionResult result = session.handle_message(*message);
        if (!write_message(pipe, result.response_json, stop_token) || result.disconnect)
        {
            return;
        }
    }
}
} // namespace

NamedPipeServer::NamedPipeServer(const IpcEndpoint &endpoint, IpcHostState &host_state)
    : endpoint_(endpoint), host_state_(host_state)
{
}

void NamedPipeServer::run(const std::stop_token stop_token)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    CurrentUserSecurity security;
    log_diagnostic(DiagnosticLevel::information, "ipc.server_started", "Named Pipe server is accepting clients.");

    while (!stop_token.stop_requested())
    {
        const DWORD buffer_bytes = static_cast<DWORD>(kMaximumIpcMessageBytes + sizeof(std::uint32_t));
        const UniqueHandle pipe(CreateNamedPipeW(endpoint_.pipe_name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                                                 ipc_pipe_mode(), 1U, buffer_bytes, buffer_bytes, 0U,
                                                 security.attributes()));
        if (pipe.get() == INVALID_HANDLE_VALUE)
        {
            throw_last_error("CreateNamedPipeW");
        }

        if (!connect_client(pipe.get(), stop_token))
        {
            break;
        }

        log_diagnostic(DiagnosticLevel::information, "ipc.client_connected", "IPC client connected.");
        serve_client(pipe.get(), endpoint_, host_state_, stop_token);
        DisconnectNamedPipe(pipe.get());
        log_diagnostic(DiagnosticLevel::information, "ipc.client_disconnected", "IPC client disconnected.");
    }

    log_diagnostic(DiagnosticLevel::information, "ipc.server_stopped", "Named Pipe server stopped.");
}
} // namespace external_peepsight
