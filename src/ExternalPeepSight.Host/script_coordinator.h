#pragma once

#include "host_threads.h"
#include "input_system.h"
#include "script_runtime.h"

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Describes one fully loaded candidate script runtime.
struct ScriptPreparedConfiguration
{
    /// Opaque identifier used to commit or discard this candidate.
    std::uint64_t token;
    /// Conflict-checked input bindings declared by active scripts.
    std::vector<ScriptInputBinding> input_bindings;
    /// Combined startup visibility decision for all active scripts.
    bool allows_visible;
    /// Whether the active profile delegates visibility to Lua.
    bool profile_uses_lua;
    /// Active profile set identifier.
    std::string profile_set_id;
    /// Active profile identifier.
    std::string profile_id;
};

/// Publishes one completed script callback to the Host window thread.
struct ScriptRuntimeUpdate
{
    /// Combined visibility decision after the callback.
    bool allows_visible;
    /// Commands staged by the successful callback.
    std::vector<ScriptCommand> commands;
    /// Whether the callback completed successfully.
    bool succeeded;
    /// Developer-facing error when execution failed.
    std::string error;
};

/// Owns all Lua states and executes them on one dedicated worker thread.
class ScriptCoordinator
{
  public:
    /// Callback invoked on the Script thread after an input callback completes.
    using RuntimeUpdated = std::function<void(ScriptRuntimeUpdate)>;

    /// Creates a coordinator with a non-blocking runtime update callback.
    explicit ScriptCoordinator(RuntimeUpdated runtime_updated);

    ScriptCoordinator(const ScriptCoordinator &) = delete;
    ScriptCoordinator &operator=(const ScriptCoordinator &) = delete;

    /// Stops the Script thread and destroys all Lua states.
    ~ScriptCoordinator();

    /// Starts the Script thread.
    void start();

    /// Loads and starts a candidate without replacing the active runtime.
    [[nodiscard]] ScriptPreparedConfiguration prepare(std::string_view snapshot_json);

    /// Validates one script request and returns JSON declarations for the UI.
    [[nodiscard]] std::string validate(std::string_view request_json);

    /// Atomically promotes a prepared candidate to the active runtime.
    void commit(std::uint64_t token);

    /// Destroys a prepared candidate that was not committed.
    void discard(std::uint64_t token) noexcept;

    /// Queues one normalized input event for the active runtime.
    void dispatch_input(ScriptInputEvent event);

    /// Requests shutdown and waits for the Script thread.
    void stop() noexcept;

  private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace external_peepsight
