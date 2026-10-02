#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <functional>
#include <thread>

#include <gtest/gtest.h>

#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "fake_viture_api.hpp"

namespace cg::glasses::test {
namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr std::int64_t kMillisecondNs = 1'000'000;
constexpr std::int64_t kHundredMillisecondsNs = 100 * kMillisecondNs;

/// `HeadSample` is not default-constructible, so every read target is built
/// through this placeholder.
HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

/// A seam sample with `host_time` in SDK nanoseconds and a pure yaw rotation.
cg_head_sample CgSample(std::uint32_t sequence, std::int64_t sdk_time_ns, cg_track_state state, double yaw_deg) {
    const double half_yaw = yaw_deg * kPi / 360.0;
    cg_head_sample out{};
    out.host_time = sdk_time_ns;
    out.pose.p.x = 0.0F;
    out.pose.p.y = 0.0F;
    out.pose.p.z = 0.0F;
    out.pose.q.w = static_cast<float>(std::cos(half_yaw));
    out.pose.q.x = 0.0F;
    out.pose.q.y = static_cast<float>(std::sin(half_yaw));
    out.pose.q.z = 0.0F;
    out.state = state;
    out.sequence = sequence;
    return out;
}

bool WaitFor(const std::function<bool()> &predicate,
             std::chrono::milliseconds timeout = std::chrono::milliseconds(2000)) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        if (predicate()) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return predicate();
}

bool WaitForSample(const VitureHeadPoseSource &source, HeadSample &out, std::uint32_t min_seq) {
    return WaitFor([&] { return source.TryGetLatest(out, Duration{0}) && out.seq >= min_seq; });
}

bool WaitForState(const VitureHeadPoseSource &source, TrackState state, HeadSample &out) {
    return WaitFor([&] { return source.TryGetLatest(out, Duration{0}) && out.state == state; });
}

/// `Start` reports a failed creation and leaves the object ready to retry.
TEST(VitureFault, CreateFailureReturnsErrorThenRetrySucceeds) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.create_script.push_back(Err<void>(Status{StatusCode::Device, "fake: create failed"}));
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 5.0)};

    VitureHeadPoseSource source(api, clock);
    const Result<void> first = source.Start();
    ASSERT_FALSE(first.ok());
    EXPECT_EQ(first.status().code(), StatusCode::Device);
    EXPECT_EQ(api.create_calls.load(), 1U);
    EXPECT_EQ(api.start_calls.load(), 0U);

    ASSERT_TRUE(source.Start().ok());
    EXPECT_EQ(api.create_calls.load(), 2U);
    EXPECT_EQ(api.start_calls.load(), 1U);
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));
    EXPECT_EQ(sample.state, TrackState::Stable);
    source.Stop();
}

/// A poll failure tears the device down and reconnects on a clock-gated
/// backoff: 100 ms first, then 200 ms for the second consecutive failure.
TEST(VitureFault, PollErrorsReconnectWithDoublingBackoff) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)),
                       Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"}),
                       Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"}),
                       Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 1.0))};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    EXPECT_EQ(first.seq, 1U);

    // The first failure destroys the device, then waits 100 ms on the injected
    // clock: frozen clock time means no reconnect.
    ASSERT_TRUE(WaitFor([&] { return api.destroy_calls.load() == 1U; }));
    EXPECT_EQ(api.create_calls.load(), 1U);
    std::this_thread::sleep_for(std::chrono::milliseconds(60));
    EXPECT_EQ(api.create_calls.load(), 1U);

    clock.Advance(Duration{kHundredMillisecondsNs});
    ASSERT_TRUE(WaitFor([&] { return api.create_calls.load() == 2U; }));

    // The second consecutive failure doubles the backoff to 200 ms.
    ASSERT_TRUE(WaitFor([&] { return api.destroy_calls.load() == 2U; }));
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    clock.Advance(Duration{kHundredMillisecondsNs});
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    EXPECT_EQ(api.create_calls.load(), 2U);
    clock.Advance(Duration{kHundredMillisecondsNs});
    ASSERT_TRUE(WaitFor([&] { return api.create_calls.load() == 3U; }));

    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, second, 2U));
    EXPECT_EQ(second.seq, 2U);
    EXPECT_GT(second.time, first.time);
    source.Stop();
}

/// Removing the device mid-stream (polls time out, creation keeps failing to
/// produce samples) degrades Stable -> Unstable at 500 ms -> Lost at 1000 ms.
TEST(VitureFault, RemovalMidStreamDowngradesStableUnstableLost) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0), CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 0.0)};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 2U));
    ASSERT_EQ(sample.state, TrackState::Stable);

    // Quiet for 400 ms: still Stable.
    clock.Advance(Duration{400 * kMillisecondNs});
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    ASSERT_TRUE(source.TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.state, TrackState::Stable);

    // Quiet past 500 ms: Unstable.
    clock.Advance(Duration{200 * kMillisecondNs});
    ASSERT_TRUE(WaitForState(source, TrackState::Unstable, sample));

    // Quiet past 1000 ms: Lost.
    clock.Advance(Duration{600 * kMillisecondNs});
    ASSERT_TRUE(WaitForState(source, TrackState::Lost, sample));
    source.Stop();
}

/// A blocking `PollPose` (fake sleeps until `RequestStop`) must not delay
/// `TryGetLatest`, and `Stop` must return within one poll timeout.
TEST(VitureFault, BlockingPollLeavesReadersWaitFreeAndStopPrompt) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_delay_ns = 5'000'000'000; // 5 s; the test must not wait it out.

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    std::this_thread::sleep_for(std::chrono::milliseconds(20));

    HeadSample sample = PlaceholderSample();
    const auto read_start = std::chrono::steady_clock::now();
    EXPECT_FALSE(source.TryGetLatest(sample, Duration{0}));
    const auto read_elapsed = std::chrono::steady_clock::now() - read_start;
    EXPECT_LT(read_elapsed, std::chrono::milliseconds(50));

    const auto stop_start = std::chrono::steady_clock::now();
    source.Stop();
    const auto stop_elapsed = std::chrono::steady_clock::now() - stop_start;
    EXPECT_LT(stop_elapsed, std::chrono::milliseconds(250)) << "Stop must interrupt the blocked poll";
    EXPECT_EQ(api.poll_calls.load(), 1U);
    EXPECT_TRUE(api.stop_requested());
}

/// `Recenter` returns the SDK's `ResetOriginCarina` failure as its own Result.
TEST(VitureFault, ResetFailureSurfacesAsResult) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 10.0)};
    api.reset_origin_result = Err<void>(Status{StatusCode::Device, "fake: reset rejected"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    const Result<void> result = source.Recenter();
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Device);
    EXPECT_EQ(api.reset_origin_calls.load(), 1U);
    EXPECT_TRUE(api.has_reset_pose);
    source.Stop();
}

/// Once `Stop` returns, a pending backoff must not reconnect any more.
TEST(VitureFault, NoReconnectAfterStop) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    // The feed is exhausted now; the first timeout tears the device down and
    // parks in the 100 ms backoff (the clock is frozen at zero).
    ASSERT_TRUE(WaitFor([&] { return api.destroy_calls.load() >= 1U; }));
    EXPECT_EQ(api.create_calls.load(), 1U);

    source.Stop();
    const std::uint64_t creates_at_stop = api.create_calls.load();
    const std::uint64_t destroys_at_stop = api.destroy_calls.load();
    std::this_thread::sleep_for(std::chrono::milliseconds(40));
    EXPECT_EQ(api.create_calls.load(), creates_at_stop);
    EXPECT_EQ(api.destroy_calls.load(), destroys_at_stop);
}

/// Sequence and time stay strictly monotonic across a reconnect.
TEST(VitureFault, SequenceStaysMonotonicAcrossReconnect) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 1.0)),
                       Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"}),
                       Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 2.0))};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    ASSERT_TRUE(WaitFor([&] { return api.destroy_calls.load() >= 1U; }));
    clock.Advance(Duration{kHundredMillisecondsNs});
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, second, 2U));
    EXPECT_EQ(second.seq, 2U);
    EXPECT_GT(second.time, first.time);
    source.Stop();
}

/// The injected setup hook runs on the polling thread (not the caller's).
TEST(VitureFault, ThreadSetupHookRunsOnThePollingThread) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)};
    std::atomic<bool> hook_ran{false};
    std::atomic<std::thread::id> hook_thread{};
    const std::thread::id caller = std::this_thread::get_id();

    VitureHeadPoseSource source(api, clock, [&hook_ran, &hook_thread] {
        hook_ran.store(true, std::memory_order_release);
        hook_thread.store(std::this_thread::get_id(), std::memory_order_release);
    });

    ASSERT_TRUE(source.Start().ok());
    ASSERT_TRUE(WaitFor([&] { return hook_ran.load(std::memory_order_acquire); }));
    EXPECT_NE(hook_thread.load(std::memory_order_acquire), caller);
    source.Stop();
}

} // namespace
} // namespace cg::glasses::test
