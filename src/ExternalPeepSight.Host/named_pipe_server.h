#pragma once

#include "ipc_endpoint.h"
#include "ipc_protocol.h"

#include <windows.h>

#include <stop_token>

namespace external_peepsight
{
/// Returns the Named Pipe mode required by the Host security contract.
[[nodiscard]] constexpr DWORD ipc_pipe_mode() noexcept
{
    return PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
}

/// Serves authenticated, length-prefixed JSON messages on one randomized Named Pipe.
class NamedPipeServer
{
  public:
    /// Creates a server for one endpoint and persistent Host state.
    NamedPipeServer(const IpcEndpoint &endpoint, IpcHostState &host_state);

    NamedPipeServer(const NamedPipeServer &) = delete;
    NamedPipeServer &operator=(const NamedPipeServer &) = delete;

    /// Accepts clients until cooperative shutdown is requested.
    ///
    /// Thread: IPC thread only.
    void run(std::stop_token stop_token);

  private:
    const IpcEndpoint &endpoint_;
    IpcHostState &host_state_;
};
} // namespace external_peepsight
