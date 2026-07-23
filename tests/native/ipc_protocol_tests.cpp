#include "ipc_protocol.h"

#include <winrt/base.h>

#include <gtest/gtest.h>

#include <string>

namespace
{
constexpr std::string_view kToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

[[nodiscard]] std::string envelope(const std::string_view request_id, const std::string_view type,
                                   const std::string_view payload, const int protocol_version = 1)
{
    return "{\"protocolVersion\":" + std::to_string(protocol_version) + ",\"requestId\":\"" + std::string(request_id) +
           "\",\"type\":\"" + std::string(type) + "\",\"payload\":" + std::string(payload) + "}";
}

void initialize_winrt()
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
}

TEST(IpcSession, RequiresHelloAsFirstMessage)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));

    const auto result = session.handle_message(envelope("11111111-1111-1111-1111-111111111111", "GetState", "null"));

    EXPECT_TRUE(result.disconnect);
    EXPECT_NE(std::string::npos, result.response_json.find("HandshakeRequired"));
    EXPECT_FALSE(session.authenticated());
}

TEST(IpcSession, RejectsInvalidHandshakeToken)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));

    const auto result = session.handle_message(
        envelope("22222222-2222-2222-2222-222222222222", "Hello", "{\"token\":\"invalid-token\"}"));

    EXPECT_TRUE(result.disconnect);
    EXPECT_NE(std::string::npos, result.response_json.find("AuthenticationFailed"));
    EXPECT_FALSE(session.authenticated());
}

TEST(IpcSession, AuthenticatesAndReturnsHostState)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));

    const auto hello = session.handle_message(
        envelope("33333333-3333-3333-3333-333333333333", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}"));
    const auto get_state = session.handle_message(envelope("44444444-4444-4444-4444-444444444444", "GetState", "null"));

    EXPECT_FALSE(hello.disconnect);
    EXPECT_TRUE(session.authenticated());
    EXPECT_NE(std::string::npos, hello.response_json.find("\"type\":\"Ack\""));
    EXPECT_NE(std::string::npos, get_state.response_json.find("\"configurationVersion\":0"));
    EXPECT_NE(std::string::npos, get_state.response_json.find("\"snapshot\":null"));
}

TEST(IpcSession, RejectsStaleAndConflictingConfigurationVersions)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));
    static_cast<void>(session.handle_message(
        envelope("55555555-5555-5555-5555-555555555555", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));

    const auto applied = session.handle_message(envelope("66666666-6666-6666-6666-666666666666", "ApplySnapshot",
                                                         "{\"configurationVersion\":2,\"snapshot\":{\"a\":1}}"));
    const auto idempotent = session.handle_message(envelope("77777777-7777-7777-7777-777777777777", "ApplySnapshot",
                                                            "{\"configurationVersion\":2,\"snapshot\":{\"a\":1}}"));
    const auto conflict = session.handle_message(envelope("88888888-8888-8888-8888-888888888888", "ApplySnapshot",
                                                          "{\"configurationVersion\":2,\"snapshot\":{\"a\":2}}"));
    const auto stale = session.handle_message(envelope("99999999-9999-9999-9999-999999999999", "ApplySnapshot",
                                                       "{\"configurationVersion\":1,\"snapshot\":{\"a\":1}}"));

    EXPECT_NE(std::string::npos, applied.response_json.find("\"alreadyApplied\":false"));
    EXPECT_NE(std::string::npos, idempotent.response_json.find("\"alreadyApplied\":true"));
    EXPECT_NE(std::string::npos, conflict.response_json.find("ConfigurationVersionConflict"));
    EXPECT_NE(std::string::npos, stale.response_json.find("StaleConfigurationVersion"));
    EXPECT_EQ(2U, state.snapshot().configuration_version);
}

TEST(IpcSession, ReplaysIdenticalRequestAndRejectsRequestIdReuse)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));
    static_cast<void>(session.handle_message(
        envelope("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));

    const std::string request = envelope("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "GetState", "null");
    const auto first = session.handle_message(request);
    const auto replay = session.handle_message(request);
    const auto reused = session.handle_message(envelope("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "GetState", "{}"));

    EXPECT_EQ(first.response_json, replay.response_json);
    EXPECT_NE(std::string::npos, reused.response_json.find("DuplicateRequestId"));
}

TEST(IpcSession, PreservesStateAcrossReconnect)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    {
        external_peepsight::IpcSession first(state, std::string(kToken));
        static_cast<void>(first.handle_message(
            envelope("cccccccc-cccc-cccc-cccc-cccccccccccc", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));
        static_cast<void>(first.handle_message(envelope("dddddddd-dddd-dddd-dddd-dddddddddddd", "ApplySnapshot",
                                                        "{\"configurationVersion\":7,\"snapshot\":{\"mode\":\"x\"}}")));
    }

    external_peepsight::IpcSession second(state, std::string(kToken));
    static_cast<void>(second.handle_message(
        envelope("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));
    const auto result = second.handle_message(envelope("ffffffff-ffff-ffff-ffff-ffffffffffff", "GetState", "null"));

    EXPECT_NE(std::string::npos, result.response_json.find("\"configurationVersion\":7"));
    EXPECT_NE(std::string::npos, result.response_json.find("\"mode\":\"x\""));
}

TEST(IpcSession, RejectsUnsupportedProtocolAndExcessiveDepth)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession session(state, std::string(kToken));

    const auto unsupported = session.handle_message(
        envelope("12345678-1234-1234-1234-123456789012", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}", 2));

    std::string deep_payload = "{\"token\":\"";
    deep_payload += kToken;
    deep_payload += "\",\"nested\":";
    deep_payload.append(65U, '[');
    deep_payload += "0";
    deep_payload.append(65U, ']');
    deep_payload += "}";
    const auto deep = session.handle_message(envelope("87654321-4321-4321-4321-210987654321", "Hello", deep_payload));

    EXPECT_TRUE(unsupported.disconnect);
    EXPECT_NE(std::string::npos, unsupported.response_json.find("UnsupportedProtocolVersion"));
    EXPECT_TRUE(deep.disconnect);
    EXPECT_NE(std::string::npos, deep.response_json.find("MalformedMessage"));
}

TEST(IpcSession, RejectsUnknownFieldsAndConfigurationVersionOutsideJsonIntegerRange)
{
    initialize_winrt();
    external_peepsight::IpcHostState state;
    external_peepsight::IpcSession unauthenticated(state, std::string(kToken));

    const auto unknown_field =
        unauthenticated.handle_message("{\"protocolVersion\":1,\"requestId\":\"12345678-1234-1234-1234-123456789012\","
                                       "\"type\":\"Hello\",\"payload\":{\"token\":\"" +
                                       std::string(kToken) + "\"},\"extra\":true}");

    external_peepsight::IpcSession authenticated(state, std::string(kToken));
    static_cast<void>(authenticated.handle_message(
        envelope("abcdefab-cdef-cdef-cdef-abcdefabcdef", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));
    const auto out_of_range =
        authenticated.handle_message(envelope("fedcbafe-dcba-dcba-dcba-fedcbafedcba", "ApplySnapshot",
                                              "{\"configurationVersion\":9007199254740992,\"snapshot\":{}}"));

    EXPECT_TRUE(unknown_field.disconnect);
    EXPECT_NE(std::string::npos, unknown_field.response_json.find("MalformedMessage"));
    EXPECT_FALSE(out_of_range.disconnect);
    EXPECT_NE(std::string::npos, out_of_range.response_json.find("InvalidPayload"));
    EXPECT_EQ(0U, state.snapshot().configuration_version);
}

TEST(IpcHostState, ValidatorRejectionDoesNotAdvanceVersionAndIdempotentReplaySkipsValidation)
{
    int validation_count = 0;
    external_peepsight::IpcHostState state(
        [&validation_count](const std::string_view snapshot)
        {
            ++validation_count;
            if (snapshot.find("\"reject\":true") != std::string_view::npos)
            {
                throw std::invalid_argument("Rejected by test validator.");
            }
        });

    EXPECT_THROW(static_cast<void>(state.apply(1U, "{\"reject\":true}")), std::invalid_argument);
    EXPECT_EQ(0U, state.snapshot().configuration_version);

    EXPECT_EQ(external_peepsight::IpcApplyResult::applied, state.apply(2U, "{\"accepted\":true}"));
    EXPECT_EQ(external_peepsight::IpcApplyResult::already_applied, state.apply(2U, "{\"accepted\":true}"));

    EXPECT_EQ(2, validation_count);
    EXPECT_EQ(2U, state.snapshot().configuration_version);
}

TEST(IpcSession, ReturnsStableClientErrorFromSnapshotValidator)
{
    initialize_winrt();
    external_peepsight::IpcHostState state(
        [](const std::string_view)
        {
            throw external_peepsight::IpcClientError(L"InputRegistrationFailed",
                                                     L"The configured hotkey is already registered.");
        });
    external_peepsight::IpcSession session(state, std::string(kToken));
    static_cast<void>(session.handle_message(
        envelope("10203040-5060-7080-90a0-b0c0d0e0f000", "Hello", "{\"token\":\"" + std::string(kToken) + "\"}")));

    const auto result = session.handle_message(envelope("01234567-89ab-cdef-0123-456789abcdef", "ApplySnapshot",
                                                        "{\"configurationVersion\":1,\"snapshot\":{\"switches\":{}}}"));

    EXPECT_FALSE(result.disconnect);
    EXPECT_NE(std::string::npos, result.response_json.find("\"code\":\"InputRegistrationFailed\""));
    EXPECT_NE(std::string::npos,
              result.response_json.find("\"message\":\"The configured hotkey is already registered.\""));
    EXPECT_EQ(0U, state.snapshot().configuration_version);
}
} // namespace
