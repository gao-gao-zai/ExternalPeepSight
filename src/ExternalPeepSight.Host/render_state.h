#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Defines one authenticated Toast request after JSON validation.
struct ToastMessage
{
    /// Request-provided stable identifier.
    std::string id;
    /// Key used to replace repeated status messages.
    std::string deduplication_key;
    /// User-visible UTF-16 text.
    std::wstring text;
    /// Diagnostic category such as switch or profile.
    std::string category;
    /// Higher values are displayed before lower-priority messages.
    std::int32_t priority;
    /// Optional request-specific lifetime in milliseconds.
    std::optional<std::uint32_t> duration_ms;
};

/// One active Toast with an absolute monotonic expiry time.
struct ActiveToast
{
    /// Validated message data.
    ToastMessage message;
    /// Expiry in the same monotonic millisecond domain supplied to `ToastQueue`.
    std::uint64_t expires_at_ms;
};

/// Parses a ShowToast payload and rejects unknown fields or unsafe lengths.
[[nodiscard]] ToastMessage parse_toast_message(std::string_view payload_json);

/// Maintains Toast priority, deduplication, and expiry deterministically.
class ToastQueue
{
  public:
    /// Inserts or replaces a message and returns the currently visible Toast.
    [[nodiscard]] const ActiveToast &push(ToastMessage message, std::uint64_t now_ms,
                                          std::uint32_t default_duration_ms);

    /// Expires elapsed messages and returns the next visible Toast, if any.
    [[nodiscard]] std::optional<ActiveToast> advance(std::uint64_t now_ms, std::uint32_t default_duration_ms);

    /// Returns the currently visible Toast without changing queue state.
    [[nodiscard]] const std::optional<ActiveToast> &active() const noexcept;

    /// Removes all active and queued Toast messages.
    void clear() noexcept;

  private:
    [[nodiscard]] ActiveToast activate(ToastMessage message, std::uint64_t now_ms,
                                       std::uint32_t default_duration_ms) const;
    void enqueue_or_replace(ToastMessage message);

    std::optional<ActiveToast> active_;
    std::vector<ToastMessage> pending_;
};

/// Coalesces versioned render updates into a fixed time window.
class RenderUpdateCoalescer
{
  public:
    /// Default hot-reload batching window.
    static constexpr std::uint64_t WindowMs = 16U;

    /// Offers a newer version and records the first due time for the batch.
    [[nodiscard]] bool submit(std::uint64_t version, std::uint64_t now_ms) noexcept;

    /// Returns the newest due version and clears the pending batch.
    [[nodiscard]] std::optional<std::uint64_t> take_due(std::uint64_t now_ms) noexcept;

    /// Returns the pending batch deadline.
    [[nodiscard]] std::optional<std::uint64_t> due_at_ms() const noexcept;

  private:
    std::uint64_t applied_version_ = 0U;
    std::uint64_t pending_version_ = 0U;
    std::optional<std::uint64_t> due_at_ms_;
};
} // namespace external_peepsight
