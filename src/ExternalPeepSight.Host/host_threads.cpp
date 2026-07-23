#include "host_threads.h"

#include <format>
#include <stdexcept>
#include <utility>

namespace external_peepsight
{
ThreadAffinity::ThreadAffinity() noexcept : owner_thread_id_(GetCurrentThreadId()) {}

DWORD ThreadAffinity::owner_thread_id() const noexcept
{
    return owner_thread_id_;
}

bool ThreadAffinity::is_current() const noexcept
{
    return GetCurrentThreadId() == owner_thread_id_;
}

void ThreadAffinity::assert_current(const std::string_view operation) const
{
    const DWORD current = GetCurrentThreadId();
    if (!is_current())
    {
        throw std::logic_error(
            std::format("{} requires thread {}, but thread {} invoked it.", operation, owner_thread_id_, current));
    }
}

HostWorkerThread::HostWorkerThread(const HostThreadRole role, Worker worker) : role_(role), worker_(std::move(worker))
{
    if (!worker_)
    {
        throw std::invalid_argument("Host worker callback cannot be empty.");
    }
}

HostWorkerThread::~HostWorkerThread()
{
    request_stop();
    join();
}

void HostWorkerThread::start()
{
    HostThreadState expected = HostThreadState::created;
    if (!state_.compare_exchange_strong(expected, HostThreadState::starting))
    {
        throw std::logic_error("Host worker can only be started once.");
    }

    thread_ = std::jthread([this](const std::stop_token stop_token) { run(stop_token); });

    std::unique_lock lock(mutex_);
    state_changed_.wait(lock,
                        [this]
                        {
                            const HostThreadState current = state_.load(std::memory_order_acquire);
                            return current == HostThreadState::running || current == HostThreadState::failed ||
                                   current == HostThreadState::stopped;
                        });
    lock.unlock();
    rethrow_if_failed();
}

void HostWorkerThread::request_stop() noexcept
{
    if (!thread_.joinable())
    {
        return;
    }

    HostThreadState expected = HostThreadState::running;
    if (state_.compare_exchange_strong(expected, HostThreadState::stop_requested))
    {
        state_changed_.notify_all();
    }
    thread_.request_stop();
}

void HostWorkerThread::join() noexcept
{
    if (thread_.joinable())
    {
        thread_.join();
    }
}

void HostWorkerThread::rethrow_if_failed() const
{
    std::exception_ptr failure;
    {
        std::scoped_lock lock(mutex_);
        failure = failure_;
    }
    if (failure)
    {
        std::rethrow_exception(failure);
    }
}

HostThreadRole HostWorkerThread::role() const noexcept
{
    return role_;
}

HostThreadState HostWorkerThread::state() const noexcept
{
    return state_.load(std::memory_order_acquire);
}

void HostWorkerThread::run(const std::stop_token stop_token) noexcept
{
    try
    {
        state_.store(HostThreadState::running, std::memory_order_release);
        state_changed_.notify_all();
        worker_(stop_token);
        state_.store(HostThreadState::stopped, std::memory_order_release);
    }
    catch (...)
    {
        {
            std::scoped_lock lock(mutex_);
            failure_ = std::current_exception();
        }
        state_.store(HostThreadState::failed, std::memory_order_release);
    }
    state_changed_.notify_all();
}

void wait_for_stop(const std::stop_token stop_token)
{
    std::mutex mutex;
    std::condition_variable_any stop_requested;
    std::unique_lock lock(mutex);
    stop_requested.wait(lock, stop_token, [] { return false; });
}
} // namespace external_peepsight
