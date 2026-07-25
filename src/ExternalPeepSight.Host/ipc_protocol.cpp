#include "ipc_protocol.h"

#include <objbase.h>
#include <windows.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <utility>

namespace external_peepsight
{
namespace
{
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValue;
using winrt::Windows::Data::Json::JsonValueType;

constexpr std::size_t kMaximumCachedRequests = 128U;
constexpr std::uint64_t kMaximumJsonInteger = 9'007'199'254'740'991ULL;
constexpr std::string_view kUnknownRequestId = "00000000-0000-0000-0000-000000000000";

enum class MessageType
{
    hello,
    get_state,
    apply_snapshot,
    set_active_profile,
    set_switch_state,
    set_monitor_selection,
    show_toast,
    validate_script,
    ack,
    error,
    host_state_changed,
};

struct ParsedEnvelope
{
    std::string request_id;
    MessageType type;
    IJsonValue payload;
};

[[nodiscard]] bool has_valid_depth(const std::string_view json) noexcept
{
    std::size_t depth = 0U;
    bool in_string = false;
    bool escaped = false;
    for (const char character : json)
    {
        if (in_string)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                in_string = false;
            }
            continue;
        }

        if (character == '"')
        {
            in_string = true;
        }
        else if (character == '{' || character == '[')
        {
            ++depth;
            if (depth > kMaximumIpcJsonDepth)
            {
                return false;
            }
        }
        else if (character == '}' || character == ']')
        {
            if (depth == 0U)
            {
                return false;
            }
            --depth;
        }
    }
    return depth == 0U && !in_string && !escaped;
}

void require_allowed_properties(const JsonObject &object, const std::initializer_list<std::wstring_view> allowed)
{
    std::unordered_set<std::wstring> names;
    names.reserve(allowed.size());
    for (const std::wstring_view name : allowed)
    {
        names.emplace(name);
    }

    for (const auto &entry : object)
    {
        if (!names.contains(std::wstring(entry.Key())))
        {
            throw std::invalid_argument("IPC JSON contains an unknown property.");
        }
    }
}

[[nodiscard]] bool is_valid_request_id(const std::string_view value)
{
    if (value.size() != 36U)
    {
        return false;
    }

    const std::wstring wide = L"{" + std::wstring(winrt::to_hstring(value).c_str()) + L"}";
    GUID identifier{};
    return SUCCEEDED(CLSIDFromString(wide.c_str(), &identifier));
}

[[nodiscard]] std::uint64_t get_json_integer(const JsonObject &object, const std::wstring_view name)
{
    const double value = object.GetNamedNumber(name);
    if (!std::isfinite(value) || value < 0.0 || value > static_cast<double>(kMaximumJsonInteger) ||
        std::floor(value) != value)
    {
        throw std::invalid_argument("IPC integer is outside the supported range.");
    }
    return static_cast<std::uint64_t>(value);
}

[[nodiscard]] MessageType parse_message_type(const std::string_view value)
{
    if (value == "Hello")
    {
        return MessageType::hello;
    }
    if (value == "GetState")
    {
        return MessageType::get_state;
    }
    if (value == "ApplySnapshot")
    {
        return MessageType::apply_snapshot;
    }
    if (value == "SetActiveProfile")
    {
        return MessageType::set_active_profile;
    }
    if (value == "SetSwitchState")
    {
        return MessageType::set_switch_state;
    }
    if (value == "SetMonitorSelection")
    {
        return MessageType::set_monitor_selection;
    }
    if (value == "ShowToast")
    {
        return MessageType::show_toast;
    }
    if (value == "ValidateScript")
    {
        return MessageType::validate_script;
    }
    if (value == "Ack")
    {
        return MessageType::ack;
    }
    if (value == "Error")
    {
        return MessageType::error;
    }
    if (value == "HostStateChanged")
    {
        return MessageType::host_state_changed;
    }
    throw std::invalid_argument("IPC message type is unknown.");
}

[[nodiscard]] ParsedEnvelope parse_envelope(const std::string_view message_json)
{
    if (message_json.empty() || message_json.size() > kMaximumIpcMessageBytes || !has_valid_depth(message_json))
    {
        throw std::invalid_argument("IPC message size or nesting depth is invalid.");
    }

    const JsonObject root = JsonObject::Parse(winrt::to_hstring(message_json));
    require_allowed_properties(root, {L"protocolVersion", L"requestId", L"type", L"payload"});

    const std::uint64_t protocol_version = get_json_integer(root, L"protocolVersion");
    if (protocol_version != kIpcProtocolVersion)
    {
        throw std::out_of_range("IPC protocol version is not supported.");
    }

    const std::string request_id = winrt::to_string(root.GetNamedString(L"requestId"));
    if (!is_valid_request_id(request_id))
    {
        throw std::invalid_argument("IPC requestId must be a UUID.");
    }

    const std::string type_name = winrt::to_string(root.GetNamedString(L"type"));
    const IJsonValue payload = root.GetNamedValue(L"payload");
    if (payload.ValueType() != JsonValueType::Object && payload.ValueType() != JsonValueType::Null)
    {
        throw std::invalid_argument("IPC payload must be an object or null.");
    }

    return {request_id, parse_message_type(type_name), payload};
}

[[nodiscard]] bool constant_time_equal(const std::string_view left, const std::string_view right) noexcept
{
    const std::size_t maximum = (std::max)(left.size(), right.size());
    unsigned char difference = static_cast<unsigned char>(left.size() ^ right.size());
    for (std::size_t index = 0U; index < maximum; ++index)
    {
        const unsigned char left_value =
            index < left.size() ? static_cast<unsigned char>(left[index]) : static_cast<unsigned char>(0U);
        const unsigned char right_value =
            index < right.size() ? static_cast<unsigned char>(right[index]) : static_cast<unsigned char>(0U);
        difference |= static_cast<unsigned char>(left_value ^ right_value);
    }
    return difference == 0U;
}

[[nodiscard]] JsonObject make_base_response(const std::string_view request_id, const std::wstring_view type)
{
    JsonObject response;
    response.SetNamedValue(L"protocolVersion", JsonValue::CreateNumberValue(kIpcProtocolVersion));
    response.SetNamedValue(L"requestId", JsonValue::CreateStringValue(winrt::to_hstring(request_id)));
    response.SetNamedValue(L"type", JsonValue::CreateStringValue(type));
    return response;
}

[[nodiscard]] IpcSessionResult make_ack(const std::string_view request_id, JsonObject payload)
{
    JsonObject response = make_base_response(request_id, L"Ack");
    response.SetNamedValue(L"payload", payload);
    return {winrt::to_string(response.Stringify()), false};
}

[[nodiscard]] IpcSessionResult make_error(const std::string_view request_id, const std::wstring_view code,
                                          const std::wstring_view message, const bool disconnect)
{
    JsonObject payload;
    payload.SetNamedValue(L"code", JsonValue::CreateStringValue(code));
    payload.SetNamedValue(L"message", JsonValue::CreateStringValue(message));

    JsonObject response = make_base_response(request_id, L"Error");
    response.SetNamedValue(L"payload", payload);
    return {winrt::to_string(response.Stringify()), disconnect};
}

[[nodiscard]] JsonObject require_payload_object(const IJsonValue &payload)
{
    if (payload.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("IPC command requires an object payload.");
    }
    return payload.GetObject();
}

[[nodiscard]] IpcSessionResult handle_hello(const ParsedEnvelope &envelope, const std::string_view expected_token,
                                            bool &authenticated)
{
    const JsonObject payload = require_payload_object(envelope.payload);
    require_allowed_properties(payload, {L"token"});
    const std::string token = winrt::to_string(payload.GetNamedString(L"token"));
    if (!constant_time_equal(token, expected_token))
    {
        return make_error(envelope.request_id, L"AuthenticationFailed", L"The handshake token is invalid.", true);
    }

    authenticated = true;
    JsonObject ack;
    ack.SetNamedValue(L"command", JsonValue::CreateStringValue(L"Hello"));
    ack.SetNamedValue(L"hostProcessId", JsonValue::CreateNumberValue(GetCurrentProcessId()));
    ack.SetNamedValue(L"protocolVersion", JsonValue::CreateNumberValue(kIpcProtocolVersion));
    return make_ack(envelope.request_id, ack);
}

[[nodiscard]] IpcSessionResult handle_get_state(const ParsedEnvelope &envelope, const IpcHostStateSnapshot &state)
{
    if (envelope.payload.ValueType() == JsonValueType::Object)
    {
        require_allowed_properties(envelope.payload.GetObject(), {});
    }

    JsonObject ack;
    ack.SetNamedValue(L"command", JsonValue::CreateStringValue(L"GetState"));
    ack.SetNamedValue(L"configurationVersion",
                      JsonValue::CreateNumberValue(static_cast<double>(state.configuration_version)));
    ack.SetNamedValue(L"snapshot", JsonValue::Parse(winrt::to_hstring(state.snapshot_json)));
    return make_ack(envelope.request_id, ack);
}

[[nodiscard]] IpcSessionResult handle_apply_snapshot(const ParsedEnvelope &envelope, IpcHostState &host_state)
{
    const JsonObject payload = require_payload_object(envelope.payload);
    require_allowed_properties(payload, {L"configurationVersion", L"snapshot"});
    const std::uint64_t version = get_json_integer(payload, L"configurationVersion");
    if (version == 0U)
    {
        return make_error(envelope.request_id, L"InvalidConfigurationVersion",
                          L"configurationVersion must be greater than zero.", false);
    }

    const IJsonValue snapshot = payload.GetNamedValue(L"snapshot");
    if (snapshot.ValueType() != JsonValueType::Object)
    {
        return make_error(envelope.request_id, L"InvalidSnapshot", L"snapshot must be a JSON object.", false);
    }

    const IpcApplyResult apply_result = host_state.apply(version, winrt::to_string(snapshot.Stringify()));
    if (apply_result == IpcApplyResult::stale_version)
    {
        return make_error(envelope.request_id, L"StaleConfigurationVersion",
                          L"An older configuration cannot replace the current state.", false);
    }
    if (apply_result == IpcApplyResult::version_conflict)
    {
        return make_error(envelope.request_id, L"ConfigurationVersionConflict",
                          L"The configuration version was reused with different content.", false);
    }

    JsonObject ack;
    ack.SetNamedValue(L"command", JsonValue::CreateStringValue(L"ApplySnapshot"));
    ack.SetNamedValue(L"configurationVersion", JsonValue::CreateNumberValue(static_cast<double>(version)));
    ack.SetNamedValue(L"alreadyApplied",
                      JsonValue::CreateBooleanValue(apply_result == IpcApplyResult::already_applied));
    return make_ack(envelope.request_id, ack);
}

[[nodiscard]] IpcSessionResult handle_show_toast(const ParsedEnvelope &envelope, IpcHostState &host_state)
{
    const JsonObject payload = require_payload_object(envelope.payload);
    require_allowed_properties(payload, {L"id", L"deduplicationKey", L"text", L"category", L"priority", L"durationMs"});
    host_state.show_toast(winrt::to_string(payload.Stringify()));

    JsonObject ack;
    ack.SetNamedValue(L"command", JsonValue::CreateStringValue(L"ShowToast"));
    ack.SetNamedValue(L"id", JsonValue::CreateStringValue(payload.GetNamedString(L"id")));
    return make_ack(envelope.request_id, ack);
}

[[nodiscard]] IpcSessionResult handle_validate_script(const ParsedEnvelope &envelope, IpcHostState &host_state)
{
    const JsonObject payload = require_payload_object(envelope.payload);
    require_allowed_properties(payload, {L"scope", L"source", L"settings"});
    const std::string result = host_state.validate_script(winrt::to_string(payload.Stringify()));

    JsonObject ack;
    ack.SetNamedValue(L"command", JsonValue::CreateStringValue(L"ValidateScript"));
    ack.SetNamedValue(L"declarations", JsonObject::Parse(winrt::to_hstring(result)));
    return make_ack(envelope.request_id, ack);
}

[[nodiscard]] IpcSessionResult handle_authenticated_message(const ParsedEnvelope &envelope, IpcHostState &host_state)
{
    switch (envelope.type)
    {
    case MessageType::get_state:
        return handle_get_state(envelope, host_state.snapshot());
    case MessageType::apply_snapshot:
        return handle_apply_snapshot(envelope, host_state);
    case MessageType::show_toast:
        return handle_show_toast(envelope, host_state);
    case MessageType::validate_script:
        return handle_validate_script(envelope, host_state);
    case MessageType::hello:
        return make_error(envelope.request_id, L"AlreadyAuthenticated", L"The Hello handshake has already completed.",
                          false);
    case MessageType::set_active_profile:
    case MessageType::set_switch_state:
    case MessageType::set_monitor_selection:
        return make_error(envelope.request_id, L"CommandNotAvailable",
                          L"The command is recognized but is not available in this Host phase.", false);
    case MessageType::ack:
    case MessageType::error:
    case MessageType::host_state_changed:
        return make_error(envelope.request_id, L"InvalidClientMessage",
                          L"The message type is reserved for Host responses.", false);
    }
    return make_error(envelope.request_id, L"InvalidClientMessage", L"The message type is invalid.", false);
}
} // namespace

IpcApplyResult IpcHostState::apply(const std::uint64_t configuration_version, std::string snapshot_json)
{
    std::scoped_lock lock(mutex_);
    if (configuration_version < configuration_version_)
    {
        return IpcApplyResult::stale_version;
    }
    if (configuration_version == configuration_version_)
    {
        return snapshot_json == snapshot_json_ ? IpcApplyResult::already_applied : IpcApplyResult::version_conflict;
    }

    if (validator_)
    {
        validator_(configuration_version, snapshot_json);
    }
    configuration_version_ = configuration_version;
    snapshot_json_ = std::move(snapshot_json);
    return IpcApplyResult::applied;
}

IpcClientError::IpcClientError(std::wstring code, std::wstring message)
    : std::runtime_error("Authenticated IPC command was rejected."), code_(std::move(code)),
      message_(std::move(message))
{
    if (code_.empty() || message_.empty())
    {
        throw std::invalid_argument("IPC client error code and message cannot be empty.");
    }
}

const std::wstring &IpcClientError::code() const noexcept
{
    return code_;
}

const std::wstring &IpcClientError::wide_message() const noexcept
{
    return message_;
}

IpcHostState::IpcHostState(SnapshotValidator validator, ToastHandler toast_handler, ScriptValidator script_validator)
    : validator_(std::move(validator)), toast_handler_(std::move(toast_handler)),
      script_validator_(std::move(script_validator))
{
}

IpcHostStateSnapshot IpcHostState::snapshot() const
{
    std::scoped_lock lock(mutex_);
    return {configuration_version_, snapshot_json_};
}

std::uint64_t IpcHostState::host_snapshot_revision() const
{
    std::scoped_lock lock(mutex_);
    return host_snapshot_revision_;
}

std::optional<IpcHostStateChange> IpcHostState::wait_for_host_snapshot_after(const std::uint64_t observed_revision,
                                                                             const std::stop_token stop_token) const
{
    std::unique_lock lock(mutex_);
    const bool changed = host_snapshot_changed_.wait(lock, stop_token, [this, observed_revision]
                                                     { return host_snapshot_revision_ != observed_revision; });
    if (!changed)
    {
        return std::nullopt;
    }
    return IpcHostStateChange{host_snapshot_revision_, {configuration_version_, snapshot_json_}};
}

void IpcHostState::publish_host_snapshot(std::string snapshot_json)
{
    if (snapshot_json.empty())
    {
        throw std::invalid_argument("Host snapshot JSON cannot be empty.");
    }

    {
        std::scoped_lock lock(mutex_);
        if (configuration_version_ < kMaximumJsonInteger)
        {
            ++configuration_version_;
        }
        host_snapshot_revision_ =
            host_snapshot_revision_ == (std::numeric_limits<std::uint64_t>::max)() ? 0U : host_snapshot_revision_ + 1U;
        snapshot_json_ = std::move(snapshot_json);
    }
    host_snapshot_changed_.notify_all();
}

void IpcHostState::show_toast(std::string payload_json) const
{
    if (!toast_handler_)
    {
        throw IpcClientError(L"CommandNotAvailable", L"ShowToast is not available in this Host instance.");
    }
    toast_handler_(payload_json);
}

std::string IpcHostState::validate_script(std::string payload_json) const
{
    if (!script_validator_)
    {
        throw IpcClientError(L"CommandNotAvailable", L"Script validation is not available in this Host instance.");
    }
    try
    {
        return script_validator_(payload_json);
    }
    catch (const IpcClientError &)
    {
        throw;
    }
    catch (const std::exception &error)
    {
        throw IpcClientError(L"InvalidScript", winrt::to_hstring(error.what()).c_str());
    }
}

IpcSession::IpcSession(IpcHostState &host_state, std::string handshake_token)
    : host_state_(host_state), handshake_token_(std::move(handshake_token))
{
    if (handshake_token_.size() < 32U || handshake_token_.size() > 256U)
    {
        throw std::invalid_argument("IPC handshake token length is invalid.");
    }
}

IpcSessionResult IpcSession::handle_message(const std::string_view message_json)
{
    std::optional<ParsedEnvelope> envelope;
    try
    {
        envelope = parse_envelope(message_json);
    }
    catch (const std::out_of_range &)
    {
        return make_error(kUnknownRequestId, L"UnsupportedProtocolVersion",
                          L"The IPC protocol version is not supported.", true);
    }
    catch (const std::exception &)
    {
        return make_error(kUnknownRequestId, L"MalformedMessage", L"The IPC message is malformed.", true);
    }

    const auto cached = response_cache_.find(envelope->request_id);
    if (cached != response_cache_.end())
    {
        if (cached->second.request_json == message_json)
        {
            return cached->second.result;
        }
        return make_error(envelope->request_id, L"DuplicateRequestId",
                          L"The requestId was reused for different content.", false);
    }

    IpcSessionResult result;
    if (!authenticated_)
    {
        if (envelope->type != MessageType::hello)
        {
            result = make_error(envelope->request_id, L"HandshakeRequired",
                                L"Hello must be the first message on a connection.", true);
        }
        else
        {
            result = handle_hello(*envelope, handshake_token_, authenticated_);
        }
    }
    else
    {
        try
        {
            result = handle_authenticated_message(*envelope, host_state_);
        }
        catch (const IpcClientError &error)
        {
            result = make_error(envelope->request_id, error.code(), error.wide_message(), false);
        }
        catch (const std::exception &)
        {
            result = make_error(envelope->request_id, L"InvalidPayload", L"The command payload is invalid.", false);
        }
    }

    cache_response(envelope->request_id, std::string(message_json), result);
    return result;
}

bool IpcSession::authenticated() const noexcept
{
    return authenticated_;
}

void IpcSession::cache_response(std::string request_id, std::string request_json, const IpcSessionResult &result)
{
    if (response_cache_.size() == kMaximumCachedRequests)
    {
        response_cache_.erase(response_cache_order_.front());
        response_cache_order_.pop_front();
    }

    response_cache_order_.push_back(request_id);
    response_cache_.emplace(std::move(request_id), CachedRequest{std::move(request_json), result});
}

std::string make_host_state_changed(const IpcHostStateSnapshot &state)
{
    JsonObject payload;
    payload.SetNamedValue(L"configurationVersion",
                          JsonValue::CreateNumberValue(static_cast<double>(state.configuration_version)));
    payload.SetNamedValue(L"snapshot", JsonValue::Parse(winrt::to_hstring(state.snapshot_json)));

    JsonObject response = make_base_response(kUnknownRequestId, L"HostStateChanged");
    response.SetNamedValue(L"payload", payload);
    return winrt::to_string(response.Stringify());
}
} // namespace external_peepsight
