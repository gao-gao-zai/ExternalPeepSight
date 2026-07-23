#include "diagnostics.h"
#include "host_snapshot.h"
#include "host_threads.h"
#include "monitor_descriptor.h"
#include "render_recovery.h"

#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <future>
#include <memory>
#include <stdexcept>
#include <thread>
#include <vector>

namespace
{
using namespace std::chrono_literals;

[[nodiscard]] std::shared_ptr<const external_peepsight::HostSnapshot> create_snapshot(const std::uint64_t version,
                                                                                      const int monitor_count)
{
    std::vector<external_peepsight::SnapshotMonitor> monitors;
    for (int index = 0; index < monitor_count; ++index)
    {
        monitors.push_back(
            {{L"monitor-" + std::to_wstring(index)}, {index * 100, 0, index * 100 + 100, static_cast<LONG>(version)}});
    }
    return std::make_shared<const external_peepsight::HostSnapshot>(external_peepsight::HostSnapshot{
        version,
        std::move(monitors),
        version % 2U == 0U,
        std::make_shared<const external_peepsight::RenderConfiguration>(
            external_peepsight::make_default_render_configuration()),
    });
}

TEST(AtomicHostSnapshot, RejectsInvalidAndStaleVersions)
{
    external_peepsight::AtomicHostSnapshot snapshots;

    EXPECT_EQ(external_peepsight::SnapshotPublishResult::invalid_snapshot, snapshots.publish(nullptr));
    EXPECT_EQ(external_peepsight::SnapshotPublishResult::published, snapshots.publish(create_snapshot(2U, 2)));
    EXPECT_EQ(external_peepsight::SnapshotPublishResult::stale_version, snapshots.publish(create_snapshot(1U, 1)));
    EXPECT_EQ(2U, snapshots.load()->version);
}

TEST(AtomicHostSnapshot, ReadersObserveOnlyCompleteSnapshots)
{
    external_peepsight::AtomicHostSnapshot snapshots;
    ASSERT_EQ(external_peepsight::SnapshotPublishResult::published, snapshots.publish(create_snapshot(1U, 1)));

    std::atomic<bool> invalid_observation = false;
    std::jthread reader(
        [&snapshots, &invalid_observation](const std::stop_token stop_token)
        {
            while (!stop_token.stop_requested())
            {
                const auto snapshot = snapshots.load();
                if (!snapshot || snapshot->monitors.empty())
                {
                    invalid_observation.store(true);
                    return;
                }
                for (const auto &monitor : snapshot->monitors)
                {
                    if (monitor.bounds_px.bottom != static_cast<LONG>(snapshot->version))
                    {
                        invalid_observation.store(true);
                        return;
                    }
                }
            }
        });

    for (std::uint64_t version = 2U; version < 500U; ++version)
    {
        ASSERT_EQ(external_peepsight::SnapshotPublishResult::published,
                  snapshots.publish(create_snapshot(version, static_cast<int>(version % 4U) + 1)));
    }
    reader.request_stop();
    reader.join();

    EXPECT_FALSE(invalid_observation.load());
}

TEST(HostWorkerThread, StartsStopsAndAllowsRepeatedShutdown)
{
    std::promise<void> entered;
    external_peepsight::HostWorkerThread worker(external_peepsight::HostThreadRole::input,
                                                [&entered](const std::stop_token stop_token)
                                                {
                                                    entered.set_value();
                                                    external_peepsight::wait_for_stop(stop_token);
                                                });

    worker.start();
    ASSERT_EQ(std::future_status::ready, entered.get_future().wait_for(1s));
    EXPECT_EQ(external_peepsight::HostThreadState::running, worker.state());

    worker.request_stop();
    worker.request_stop();
    worker.join();
    worker.join();

    EXPECT_EQ(external_peepsight::HostThreadState::stopped, worker.state());
}

TEST(HostWorkerThread, PropagatesWorkerFailure)
{
    external_peepsight::HostWorkerThread worker(external_peepsight::HostThreadRole::ipc,
                                                [](std::stop_token) { throw std::runtime_error("worker failure"); });

    worker.start();
    worker.join();
    EXPECT_EQ(external_peepsight::HostThreadState::failed, worker.state());
    EXPECT_THROW(worker.rethrow_if_failed(), std::runtime_error);
}

TEST(ThreadAffinity, RejectsCallsFromAnotherThread)
{
    const external_peepsight::ThreadAffinity affinity;
    EXPECT_NO_THROW(affinity.assert_current("Owner operation"));

    auto result = std::async(std::launch::async,
                             [&affinity]
                             {
                                 try
                                 {
                                     affinity.assert_current("Foreign operation");
                                     return false;
                                 }
                                 catch (const std::logic_error &)
                                 {
                                     return true;
                                 }
                             });
    EXPECT_TRUE(result.get());
}

TEST(MonitorId, UsesNormalizedDevicePathIndependentOfBounds)
{
    const RECT first_bounds{0, 0, 1920, 1080};
    const RECT second_bounds{-1920, 0, 0, 1080};

    const auto first = external_peepsight::make_monitor_id(L"\\\\?\\DISPLAY#ABC#123", L"\\\\.\\DISPLAY1", first_bounds);
    const auto second =
        external_peepsight::make_monitor_id(L"\\\\?\\display#abc#123", L"\\\\.\\DISPLAY9", second_bounds);

    EXPECT_EQ(first, second);
}

TEST(MonitorId, FallsBackToNormalizedGdiName)
{
    const RECT bounds{0, 0, 1920, 1080};

    EXPECT_EQ(external_peepsight::make_monitor_id(L"", L"\\\\.\\DISPLAY2", bounds),
              external_peepsight::make_monitor_id(L"", L"\\\\.\\display2", bounds));
}

TEST(DeviceRecoveryStateMachine, EnforcesReleaseBeforeRecreation)
{
    external_peepsight::DeviceRecoveryStateMachine recovery;

    recovery.mark_initialized();
    recovery.begin_recovery();
    EXPECT_THROW(recovery.mark_recreated(), std::logic_error);
    recovery.mark_targets_released();
    recovery.mark_recreated();

    EXPECT_EQ(external_peepsight::DeviceRecoveryState::ready, recovery.state());
}

TEST(Diagnostics, PreservesNativeStatusAndEscapesJson)
{
    const external_peepsight::NativeError error(static_cast<HRESULT>(E_ACCESSDENIED), "Open resource");
    const std::string record = external_peepsight::format_diagnostic(
        external_peepsight::DiagnosticLevel::error, "resource.open", "quote \" and newline\n", error.status());

    EXPECT_EQ(external_peepsight::NativeErrorDomain::hresult, error.status().domain);
    EXPECT_NE(std::string::npos, record.find("\"event\":\"resource.open\""));
    EXPECT_NE(std::string::npos, record.find("quote \\\" and newline\\n"));
    EXPECT_NE(std::string::npos, record.find("\"nativeCode\":\"0x80070005\""));
}
} // namespace
