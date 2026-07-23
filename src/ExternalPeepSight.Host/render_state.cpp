#include "render_state.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <ranges>
#include <stdexcept>
#include <unordered_set>
#include <utility>

namespace external_peepsight
{
namespace
{
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

void require_allowed_properties(const JsonObject &object, const std::initializer_list<std::wstring_view> allowed)
{
    std::unordered_set<std::wstring> names;
    for (const std::wstring_view name : allowed)
    {
        names.emplace(name);
    }
    for (const auto &entry : object)
    {
        if (!names.contains(std::wstring(entry.Key())))
        {
            throw std::invalid_argument("ShowToast payload contains an unknown property.");
        }
    }
}

[[nodiscard]] std::string bounded_utf8(const JsonObject &object, const std::wstring_view name,
                                       const std::size_t maximum_length)
{
    const std::string value = winrt::to_string(object.GetNamedString(name));
    if (value.empty() || value.size() > maximum_length)
    {
        throw std::invalid_argument("ShowToast string length is invalid.");
    }
    return value;
}

[[nodiscard]] std::int32_t parse_priority(const JsonObject &object)
{
    const double value = object.GetNamedNumber(L"priority", 0.0);
    if (!std::isfinite(value) || std::floor(value) != value || value < -100.0 || value > 100.0)
    {
        throw std::invalid_argument("ShowToast priority is invalid.");
    }
    return static_cast<std::int32_t>(value);
}

[[nodiscard]] std::optional<std::uint32_t> parse_duration(const JsonObject &object)
{
    const IJsonValue value = object.GetNamedValue(L"durationMs", nullptr);
    if (!value)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Number)
    {
        throw std::invalid_argument("ShowToast durationMs must be a number.");
    }
    const double duration = value.GetNumber();
    if (!std::isfinite(duration) || std::floor(duration) != duration || duration < 100.0 || duration > 60'000.0)
    {
        throw std::invalid_argument("ShowToast durationMs is outside the supported range.");
    }
    return static_cast<std::uint32_t>(duration);
}
} // namespace

ToastMessage parse_toast_message(const std::string_view payload_json)
{
    const JsonObject object = JsonObject::Parse(winrt::to_hstring(payload_json));
    require_allowed_properties(object, {L"id", L"deduplicationKey", L"text", L"category", L"priority", L"durationMs"});
    const std::string id = bounded_utf8(object, L"id", 128U);
    const std::string key = bounded_utf8(object, L"deduplicationKey", 128U);
    const std::string text = bounded_utf8(object, L"text", 512U);
    const std::string category = bounded_utf8(object, L"category", 64U);
    return {
        id, key, winrt::to_hstring(text).c_str(), category, parse_priority(object), parse_duration(object),
    };
}

const ActiveToast &ToastQueue::push(ToastMessage message, const std::uint64_t now_ms,
                                    const std::uint32_t default_duration_ms)
{
    if (!active_)
    {
        active_ = activate(std::move(message), now_ms, default_duration_ms);
        return *active_;
    }

    if (message.deduplication_key == active_->message.deduplication_key ||
        message.priority >= active_->message.priority)
    {
        if (message.deduplication_key != active_->message.deduplication_key)
        {
            enqueue_or_replace(std::move(active_->message));
        }
        active_ = activate(std::move(message), now_ms, default_duration_ms);
        return *active_;
    }

    enqueue_or_replace(std::move(message));
    return *active_;
}

std::optional<ActiveToast> ToastQueue::advance(const std::uint64_t now_ms, const std::uint32_t default_duration_ms)
{
    if (active_ && active_->expires_at_ms > now_ms)
    {
        return active_;
    }
    active_.reset();
    if (pending_.empty())
    {
        return std::nullopt;
    }

    const auto next = std::ranges::max_element(pending_, [](const ToastMessage &left, const ToastMessage &right)
                                               { return left.priority < right.priority; });
    ToastMessage message = std::move(*next);
    pending_.erase(next);
    active_ = activate(std::move(message), now_ms, default_duration_ms);
    return active_;
}

const std::optional<ActiveToast> &ToastQueue::active() const noexcept
{
    return active_;
}

void ToastQueue::clear() noexcept
{
    active_.reset();
    pending_.clear();
}

ActiveToast ToastQueue::activate(ToastMessage message, const std::uint64_t now_ms,
                                 const std::uint32_t default_duration_ms) const
{
    const std::uint64_t duration = message.duration_ms.value_or(default_duration_ms);
    const std::uint64_t expires = now_ms > (std::numeric_limits<std::uint64_t>::max)() - duration
                                      ? (std::numeric_limits<std::uint64_t>::max)()
                                      : now_ms + duration;
    return {std::move(message), expires};
}

void ToastQueue::enqueue_or_replace(ToastMessage message)
{
    const auto existing = std::ranges::find(pending_, message.deduplication_key, &ToastMessage::deduplication_key);
    if (existing != pending_.end())
    {
        *existing = std::move(message);
    }
    else
    {
        pending_.push_back(std::move(message));
    }
}

bool RenderUpdateCoalescer::submit(const std::uint64_t version, const std::uint64_t now_ms) noexcept
{
    if (version == 0U || version <= applied_version_ || version <= pending_version_)
    {
        return false;
    }
    pending_version_ = version;
    if (!due_at_ms_)
    {
        due_at_ms_ = now_ms > (std::numeric_limits<std::uint64_t>::max)() - WindowMs
                         ? (std::numeric_limits<std::uint64_t>::max)()
                         : now_ms + WindowMs;
    }
    return true;
}

std::optional<std::uint64_t> RenderUpdateCoalescer::take_due(const std::uint64_t now_ms) noexcept
{
    if (!due_at_ms_ || now_ms < *due_at_ms_)
    {
        return std::nullopt;
    }
    applied_version_ = pending_version_;
    pending_version_ = 0U;
    due_at_ms_.reset();
    return applied_version_;
}

std::optional<std::uint64_t> RenderUpdateCoalescer::due_at_ms() const noexcept
{
    return due_at_ms_;
}
} // namespace external_peepsight
