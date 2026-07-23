#pragma once

#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <exception>
#include <functional>
#include <mutex>
#include <stop_token>
#include <string_view>
#include <thread>

namespace external_peepsight
{
/// Logical role assigned to a long-lived Host thread.
enum class HostThreadRole
{
    window,
    input,
    render,
    ipc,
};

/// Observable lifecycle state of a Host worker thread.
enum class HostThreadState
{
    created,
    starting,
    running,
    stop_requested,
    stopped,
    failed,
};

/// Captures and verifies ownership by one native thread.
class ThreadAffinity
{
  public:
    /// Captures the current thread as the owner.
    ThreadAffinity() noexcept;

    /// Returns the captured owner thread identifier.
    [[nodiscard]] DWORD owner_thread_id() const noexcept;

    /// Returns whether the caller is the captured owner thread.
    [[nodiscard]] bool is_current() const noexcept;

    /// Throws when called from a thread other than the captured owner.
    void assert_current(std::string_view operation) const;

  private:
    DWORD owner_thread_id_;
};

/// Owns one cooperative, one-shot Host worker thread.
class HostWorkerThread
{
  public:
    /// Work executed on the owned thread until completion or stop.
    using Worker = std::function<void(std::stop_token)>;

    /// Creates a worker for the specified logical role.
    HostWorkerThread(HostThreadRole role, Worker worker);

    HostWorkerThread(const HostWorkerThread &) = delete;
    HostWorkerThread &operator=(const HostWorkerThread &) = delete;

    /// Requests shutdown and joins the worker.
    ~HostWorkerThread();

    /// Starts the worker and waits until it is running or has failed.
    void start();

    /// Requests cooperative shutdown. Repeated calls are allowed.
    void request_stop() noexcept;

    /// Joins the worker. Repeated calls are allowed.
    void join() noexcept;

    /// Rethrows an exception captured from the worker.
    void rethrow_if_failed() const;

    /// Returns the logical thread role.
    [[nodiscard]] HostThreadRole role() const noexcept;

    /// Returns the current lifecycle state.
    [[nodiscard]] HostThreadState state() const noexcept;

  private:
    void run(std::stop_token stop_token) noexcept;

    HostThreadRole role_;
    Worker worker_;
    std::jthread thread_;
    std::atomic<HostThreadState> state_{HostThreadState::created};
    mutable std::mutex mutex_;
    std::condition_variable state_changed_;
    std::exception_ptr failure_;
};

/// Blocks without polling until cooperative shutdown is requested.
void wait_for_stop(std::stop_token stop_token);
} // namespace external_peepsight
