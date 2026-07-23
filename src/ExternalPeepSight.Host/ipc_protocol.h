#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>

namespace external_peepsight
{
/// Current JSON protocol version accepted by the Host.
inline constexpr std::uint32_t kIpcProtocolVersion = 1U;

/// Maximum encoded JSON message size accepted by the Host.
inline constexpr std::size_t kMaximumIpcMessageBytes = 1U * 1024U * 1024U;

/// Maximum nested JSON object and array depth accepted by the Host.
inline constexpr std::size_t kMaximumIpcJsonDepth = 64U;

/// Snapshot of protocol-visible Host state.
struct IpcHostStateSnapshot
{
    /// Latest accepted configuration version.
    std::uint64_t configuration_version;
    /// Normalized JSON snapshot, or `null` before the first update.
    std::string snapshot_json;
};

/// Result of applying one versioned configuration snapshot.
enum class IpcApplyResult
{
    applied,
    already_applied,
    stale_version,
    version_conflict,
};

/// Error that can be returned safely to an authenticated IPC client.
class IpcClientError final : public std::runtime_error
{
  public:
    /// Creates a client-visible protocol error.
    IpcClientError(std::wstring code, std::wstring message);

    /// Returns the stable protocol error code.
    [[nodiscard]] const std::wstring &code() const noexcept;

    /// Returns the client-visible error description.
    [[nodiscard]] const std::wstring &wide_message() const noexcept;

  private:
    std::wstring code_;
    std::wstring message_;
};

/// Stores the last valid IPC configuration independently of client connections.
class IpcHostState
{
  public:
    /// Validation and application performed before a newer snapshot is committed.
    using SnapshotValidator = std::function<void(std::string_view)>;

    /// Creates Host state with optional snapshot validation and side effects.
    explicit IpcHostState(SnapshotValidator validator = {});

    /// Applies a configuration only when its version is newer or exactly idempotent.
    [[nodiscard]] IpcApplyResult apply(std::uint64_t configuration_version, std::string snapshot_json);

    /// Returns a consistent copy of the protocol-visible state.
    [[nodiscard]] IpcHostStateSnapshot snapshot() const;

  private:
    mutable std::mutex mutex_;
    std::uint64_t configuration_version_ = 0U;
    std::string snapshot_json_ = "null";
    SnapshotValidator validator_;
};

/// Result of processing one client message.
struct IpcSessionResult
{
    /// Encoded Ack or Error response.
    std::string response_json;
    /// Whether the server must disconnect the client after sending the response.
    bool disconnect;
};

/// Enforces authentication, request identity, and version ordering for one connection.
class IpcSession
{
  public:
    /// Creates an unauthenticated session for the expected handshake token.
    IpcSession(IpcHostState &host_state, std::string handshake_token);

    /// Parses and handles one complete UTF-8 JSON message.
    [[nodiscard]] IpcSessionResult handle_message(std::string_view message_json);

    /// Returns whether the Hello handshake succeeded.
    [[nodiscard]] bool authenticated() const noexcept;

  private:
    struct CachedRequest
    {
        std::string request_json;
        IpcSessionResult result;
    };

    void cache_response(std::string request_id, std::string request_json, const IpcSessionResult &result);

    IpcHostState &host_state_;
    std::string handshake_token_;
    std::unordered_map<std::string, CachedRequest> response_cache_;
    std::deque<std::string> response_cache_order_;
    bool authenticated_ = false;
};
} // namespace external_peepsight
