#include "current_user_security.h"
#include "ipc_endpoint.h"
#include "ipc_protocol.h"
#include "named_pipe_server.h"
#include "single_instance.h"

#include <gtest/gtest.h>

#include <sddl.h>
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace
{
using namespace std::chrono_literals;

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

[[nodiscard]] std::string envelope(const std::string_view request_id, const std::string_view type,
                                   const std::string_view payload)
{
    return "{\"protocolVersion\":1,\"requestId\":\"" + std::string(request_id) + "\",\"type\":\"" + std::string(type) +
           "\",\"payload\":" + std::string(payload) + "}";
}

[[nodiscard]] UniqueHandle connect_client(const std::wstring &pipe_name)
{
    for (int attempt = 0; attempt < 50; ++attempt)
    {
        HANDLE pipe =
            CreateFileW(pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0U, nullptr, OPEN_EXISTING, 0U, nullptr);
        if (pipe != INVALID_HANDLE_VALUE)
        {
            DWORD mode = PIPE_READMODE_MESSAGE;
            EXPECT_TRUE(SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr));
            return UniqueHandle(pipe);
        }
        EXPECT_TRUE(GetLastError() == ERROR_PIPE_BUSY || GetLastError() == ERROR_FILE_NOT_FOUND);
        std::this_thread::sleep_for(20ms);
    }
    return UniqueHandle();
}

void write_message(_In_ const HANDLE pipe, const std::string_view message)
{
    const std::uint32_t size = static_cast<std::uint32_t>(message.size());
    std::vector<std::byte> frame(sizeof(size) + message.size());
    std::memcpy(frame.data(), &size, sizeof(size));
    std::memcpy(frame.data() + sizeof(size), message.data(), message.size());

    DWORD written = 0U;
    ASSERT_TRUE(WriteFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &written, nullptr));
    ASSERT_EQ(frame.size(), written);
}

void write_frame(_In_ const HANDLE pipe, const std::uint32_t encoded_size, const std::string_view payload)
{
    std::vector<std::byte> frame(sizeof(encoded_size) + payload.size());
    std::memcpy(frame.data(), &encoded_size, sizeof(encoded_size));
    std::memcpy(frame.data() + sizeof(encoded_size), payload.data(), payload.size());

    DWORD written = 0U;
    ASSERT_TRUE(WriteFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &written, nullptr));
    ASSERT_EQ(frame.size(), written);
}

[[nodiscard]] std::string read_message(_In_ const HANDLE pipe)
{
    std::vector<std::byte> frame(external_peepsight::kMaximumIpcMessageBytes + sizeof(std::uint32_t));
    DWORD read = 0U;
    EXPECT_TRUE(ReadFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &read, nullptr));
    EXPECT_GE(read, sizeof(std::uint32_t));

    std::uint32_t size = 0U;
    std::memcpy(&size, frame.data(), sizeof(size));
    EXPECT_EQ(size + sizeof(size), read);
    return std::string(reinterpret_cast<const char *>(frame.data() + sizeof(size)), size);
}

[[nodiscard]] std::optional<std::string> read_message_with_timeout(_In_ const HANDLE pipe,
                                                                   const std::chrono::milliseconds timeout)
{
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline)
    {
        DWORD available = 0U;
        if (!PeekNamedPipe(pipe, nullptr, 0U, nullptr, &available, nullptr))
        {
            return std::nullopt;
        }
        if (available > 0U)
        {
            return read_message(pipe);
        }
        std::this_thread::sleep_for(10ms);
    }
    return std::nullopt;
}

void expect_server_disconnects(_In_ const HANDLE pipe)
{
    for (int attempt = 0; attempt < 50; ++attempt)
    {
        DWORD available = 0U;
        if (!PeekNamedPipe(pipe, nullptr, 0U, nullptr, &available, nullptr))
        {
            const DWORD error = GetLastError();
            EXPECT_TRUE(error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA || error == ERROR_PIPE_NOT_CONNECTED);
            return;
        }
        std::this_thread::sleep_for(20ms);
    }
    FAIL() << "Server did not disconnect the rejected client.";
}

class RunningServer
{
  public:
    RunningServer()
        : registration_(L"test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()),
                        false),
          server_(registration_.endpoint(), state_), thread_(
                                                         [this](const std::stop_token stop_token)
                                                         {
                                                             try
                                                             {
                                                                 server_.run(stop_token);
                                                             }
                                                             catch (...)
                                                             {
                                                                 std::scoped_lock lock(failure_mutex_);
                                                                 failure_ = std::current_exception();
                                                             }
                                                         })
    {
    }

    ~RunningServer()
    {
        thread_.request_stop();
        thread_.join();
    }

    [[nodiscard]] const external_peepsight::IpcEndpoint &endpoint() const noexcept
    {
        return registration_.endpoint();
    }

    void rethrow_if_failed() const
    {
        std::scoped_lock lock(failure_mutex_);
        if (failure_)
        {
            std::rethrow_exception(failure_);
        }
    }

    void publish_host_snapshot(std::string snapshot_json)
    {
        state_.publish_host_snapshot(std::move(snapshot_json));
    }

  private:
    external_peepsight::IpcEndpointRegistration registration_;
    external_peepsight::IpcHostState state_;
    external_peepsight::NamedPipeServer server_;
    std::jthread thread_;
    mutable std::mutex failure_mutex_;
    std::exception_ptr failure_;
};

void authenticate(_In_ const HANDLE pipe, const external_peepsight::IpcEndpoint &endpoint)
{
    write_message(pipe, envelope("11111111-1111-1111-1111-111111111111", "Hello",
                                 "{\"token\":\"" + endpoint.handshake_token + "\"}"));
    EXPECT_NE(std::string::npos, read_message(pipe).find("\"type\":\"Ack\""));
}

TEST(NamedPipeContract, RejectsRemoteClientsAndUsesMessageMode)
{
    const DWORD mode = external_peepsight::ipc_pipe_mode();

    EXPECT_NE(0U, mode & PIPE_REJECT_REMOTE_CLIENTS);
    EXPECT_NE(0U, mode & PIPE_TYPE_MESSAGE);
    EXPECT_NE(0U, mode & PIPE_READMODE_MESSAGE);
}

TEST(CurrentUserSecurity, GrantsAccessOnlyToSystemAndCurrentUser)
{
    external_peepsight::CurrentUserSecurity security;
    BOOL dacl_present = FALSE;
    BOOL dacl_defaulted = FALSE;
    PACL dacl = nullptr;
    ASSERT_TRUE(
        GetSecurityDescriptorDacl(security.attributes()->lpSecurityDescriptor, &dacl_present, &dacl, &dacl_defaulted));
    ASSERT_TRUE(dacl_present);
    ASSERT_NE(nullptr, dacl);
    EXPECT_FALSE(dacl_defaulted);
    if (dacl == nullptr)
    {
        return;
    }
    ASSERT_EQ(2U, dacl->AceCount);

    std::set<std::wstring> allowed_sids;
    for (DWORD index = 0U; index < dacl->AceCount; ++index)
    {
        void *ace_value = nullptr;
        ASSERT_TRUE(GetAce(dacl, index, &ace_value));
        if (ace_value == nullptr)
        {
            FAIL() << "DACL returned a null ACE.";
            return;
        }
        const auto *ace = static_cast<const ACCESS_ALLOWED_ACE *>(ace_value);
        ASSERT_EQ(ACCESS_ALLOWED_ACE_TYPE, ace->Header.AceType);
        EXPECT_EQ(GENERIC_ALL, ace->Mask);

        LPWSTR sid_text = nullptr;
        ASSERT_TRUE(ConvertSidToStringSidW(const_cast<DWORD *>(&ace->SidStart), &sid_text));
        if (sid_text == nullptr)
        {
            FAIL() << "ACE SID conversion returned a null string.";
            return;
        }
        allowed_sids.emplace(sid_text);
        LocalFree(sid_text);
    }

    EXPECT_EQ((std::set<std::wstring>{L"S-1-5-18", security.sid_string()}), allowed_sids);
}

TEST(IpcEndpoint, UsesLowercaseHexTokenAndRejectsNonAsciiInstanceIdentifier)
{
    external_peepsight::IpcEndpointRegistration registration(
        L"test-" + std::to_wstring(GetCurrentProcessId()) + L"-token", false);
    const std::string &token = registration.endpoint().handshake_token;

    EXPECT_EQ(64U, token.size());
    EXPECT_TRUE(
        std::all_of(token.begin(), token.end(), [](const char character)
                    { return (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'); }));
    EXPECT_THROW({ external_peepsight::IpcEndpointRegistration invalid(L"\x4E2D", false); }, std::invalid_argument);
}

TEST(IpcEndpoint, PublishesGracefulShutdownMarkerForCurrentProcess)
{
    const std::wstring instance_id =
        L"test-" + std::to_wstring(GetCurrentProcessId()) + L"-graceful-" + std::to_wstring(GetTickCount64());
    std::filesystem::path marker;
    {
        external_peepsight::IpcEndpointRegistration registration(instance_id, true);
        marker = registration.endpoint().graceful_shutdown_file;
        EXPECT_FALSE(std::filesystem::exists(marker));

        registration.mark_graceful_shutdown();

        std::ifstream input(marker, std::ios::binary);
        ASSERT_TRUE(input);
        std::string content;
        std::getline(input, content);
        EXPECT_EQ("processId=" + std::to_string(GetCurrentProcessId()), content);
    }

    EXPECT_TRUE(std::filesystem::exists(marker));
    std::error_code error;
    std::filesystem::remove(marker, error);
    EXPECT_FALSE(error);
}

TEST(NamedPipeServer, AuthenticatesAppliesAndRestoresStateAfterReconnect)
{
    RunningServer server;
    {
        const UniqueHandle first = connect_client(server.endpoint().pipe_name);
        ASSERT_TRUE(first);
        authenticate(first.get(), server.endpoint());
        write_message(first.get(), envelope("22222222-2222-2222-2222-222222222222", "ApplySnapshot",
                                            "{\"configurationVersion\":5,\"snapshot\":{\"profile\":\"alpha\"}}"));
        EXPECT_NE(std::string::npos, read_message(first.get()).find("\"configurationVersion\":5"));
    }

    const UniqueHandle second = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(second);
    authenticate(second.get(), server.endpoint());
    write_message(second.get(), envelope("33333333-3333-3333-3333-333333333333", "GetState", "null"));
    const std::string state = read_message(second.get());

    EXPECT_NE(std::string::npos, state.find("\"configurationVersion\":5"));
    EXPECT_NE(std::string::npos, state.find("\"profile\":\"alpha\""));
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, PushesHostStateChangeWithoutAnotherClientRequest)
{
    RunningServer server;
    const UniqueHandle client = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(client);
    authenticate(client.get(), server.endpoint());

    server.publish_host_snapshot(R"({"profile":"script-selected"})");

    const std::optional<std::string> notification = read_message_with_timeout(client.get(), 1s);
    ASSERT_TRUE(notification);
    EXPECT_NE(std::string::npos, notification->find(R"("type":"HostStateChanged")"));
    EXPECT_NE(std::string::npos, notification->find(R"("configurationVersion":1)"));
    EXPECT_NE(std::string::npos, notification->find(R"("snapshot":{"profile":"script-selected"})"));
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, DisconnectsClientWithInvalidTokenThenAcceptsReconnect)
{
    RunningServer server;
    {
        const UniqueHandle invalid = connect_client(server.endpoint().pipe_name);
        ASSERT_TRUE(invalid);
        write_message(invalid.get(),
                      envelope("44444444-4444-4444-4444-444444444444", "Hello", "{\"token\":\"not-the-token\"}"));
        EXPECT_NE(std::string::npos, read_message(invalid.get()).find("AuthenticationFailed"));
    }

    const UniqueHandle valid = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(valid);
    authenticate(valid.get(), server.endpoint());
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, RejectsConcurrentSecondClientThenAcceptsItAfterDisconnect)
{
    RunningServer server;
    {
        const UniqueHandle first = connect_client(server.endpoint().pipe_name);
        ASSERT_TRUE(first);
        authenticate(first.get(), server.endpoint());

        HANDLE duplicate = CreateFileW(server.endpoint().pipe_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0U, nullptr,
                                       OPEN_EXISTING, 0U, nullptr);
        EXPECT_EQ(INVALID_HANDLE_VALUE, duplicate);
        EXPECT_EQ(ERROR_PIPE_BUSY, GetLastError());
    }

    const UniqueHandle reconnected = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(reconnected);
    authenticate(reconnected.get(), server.endpoint());
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, RejectsMalformedFrameThenAcceptsReconnect)
{
    RunningServer server;
    {
        const UniqueHandle malformed = connect_client(server.endpoint().pipe_name);
        ASSERT_TRUE(malformed);
        write_frame(malformed.get(), 10U, "{}");
        expect_server_disconnects(malformed.get());
    }

    const UniqueHandle reconnected = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(reconnected);
    authenticate(reconnected.get(), server.endpoint());
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, RejectsOversizedLengthThenAcceptsReconnect)
{
    RunningServer server;
    {
        const UniqueHandle oversized = connect_client(server.endpoint().pipe_name);
        ASSERT_TRUE(oversized);
        write_frame(oversized.get(), static_cast<std::uint32_t>(external_peepsight::kMaximumIpcMessageBytes + 1U), "");
        expect_server_disconnects(oversized.get());
    }

    const UniqueHandle reconnected = connect_client(server.endpoint().pipe_name);
    ASSERT_TRUE(reconnected);
    authenticate(reconnected.get(), server.endpoint());
    server.rethrow_if_failed();
}

TEST(NamedPipeServer, StopInterruptsPendingConnection)
{
    const auto started = std::chrono::steady_clock::now();
    {
        RunningServer server;
    }

    EXPECT_LT(std::chrono::steady_clock::now() - started, 1s);
}

TEST(SingleInstanceGuard, DetectsSecondOwnerInSameNamespace)
{
    const std::wstring instance_id =
        L"test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());

    external_peepsight::SingleInstanceGuard first(instance_id);
    external_peepsight::SingleInstanceGuard second(instance_id);

    EXPECT_FALSE(first.already_running());
    EXPECT_TRUE(second.already_running());
    EXPECT_THROW({ external_peepsight::SingleInstanceGuard invalid(L"\x4E2D"); }, std::invalid_argument);
}
} // namespace
