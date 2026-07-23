#pragma once

#include <filesystem>
#include <string>
#include <string_view>

namespace external_peepsight
{
/// Randomized endpoint used by one Host instance.
struct IpcEndpoint
{
    /// Full Win32 Named Pipe path.
    std::wstring pipe_name;
    /// Random hexadecimal Hello token.
    std::string handshake_token;
    /// Per-user endpoint discovery file.
    std::filesystem::path discovery_file;
    /// Per-user marker written before a graceful Host shutdown.
    std::filesystem::path graceful_shutdown_file;
};

/// Publishes and removes one per-user IPC endpoint discovery record.
class IpcEndpointRegistration
{
  public:
    /// Creates a randomized endpoint and optionally publishes its discovery file.
    IpcEndpointRegistration(std::wstring_view instance_id, bool publish_discovery);

    IpcEndpointRegistration(const IpcEndpointRegistration &) = delete;
    IpcEndpointRegistration &operator=(const IpcEndpointRegistration &) = delete;

    /// Removes the discovery file published by this registration.
    ~IpcEndpointRegistration();

    /// Returns the registered endpoint.
    [[nodiscard]] const IpcEndpoint &endpoint() const noexcept;

    /// Publishes a marker that prevents connected UI processes from restarting this Host.
    void mark_graceful_shutdown();

  private:
    void publish();

    IpcEndpoint endpoint_;
    bool published_ = false;
};
} // namespace external_peepsight
