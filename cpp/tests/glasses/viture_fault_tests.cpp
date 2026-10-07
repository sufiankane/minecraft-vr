#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
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
constexpr double kRadiansToDegrees = 180.0 / kPi;
constexpr std::int64_t kMillisecondNs = 1'000'000;
constexpr std::int64_t kTenMillisecondsNs = 10 * kMillisecondNs;
constexpr std::int64_t kHundredMillisecondsNs = 100 * kMillisecondNs;

/// `HeadSample` is not default-constructible, so every read target is built
/// through this placeholder.
HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

/// Yaw of a pose: the heading of its forward axis (-Z) about world +Y.
double YawDegrees(const core_math::Pose &pose) noexcept {
    const core_math::Vec3 forward = pose.rotation.Rotate(core_math::Vec3{0.0, 0.0, -1.0});
    return std::atan2(-forward.x, -forward.z) * kRadiansToDegrees;
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

/// Watchdog for every wait in this suite. Predicate progress is always a
/// published sequence/state or a manual-clock deadline, never wall-clock
/// pacing, so the bound only stops a hung polling thread from hanging the
/// suite: it is deliberately generous because a loaded sanitizer/coverage
/// runner is not a test failure.
constexpr std::chrono::milliseconds kWaitTimeout{30'000};

bool WaitFor(const std::function<bool()> &predicate, std::chrono::milliseconds timeout = kWaitTimeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        if (predicate()) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return predicate();
}

/// Clock-pumping wait for predicates whose progress the source gates on the
/// manual clock (reconnect backoffs, quiet-state thresholds). The source
/// derives its deadline from `clock.Now()` when it arms; advancing only after
/// a failed probe means a deadline armed after a previous advance is still
/// reached by a later one, so the wait never assumes that the polling thread
/// observed a particular wall-clock instant.
bool WaitForAdvancing(ManualHostClock &clock, Duration step, const std::function<bool()> &predicate,
                      std::chrono::milliseconds timeout = kWaitTimeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    for (;;) {
        if (predicate()) {
            return true;
        }
        if (std::chrono::steady_clock::now() >= deadline) {
            return false;
        }
        clock.Advance(step);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}

bool WaitForSample(const VitureHeadPoseSource &source, HeadSample &out, std::uint32_t min_seq) {
    return WaitFor([&] { return source.TryGetLatest(out, Duration{0}) && out.seq >= min_seq; });
}

// --- CXX-02 deterministic arming-race seam --------------------------------
// The production `Recenter` invokes `g_arm_hook` while holding its arming
// mutex; the test parks the poster there so a failing resolution is forced to
// contend with an armed-but-unreleased transaction.
std::mutex g_arm_mutex;
std::condition_variable g_arm_cv;
bool g_arm_entered = false;
bool g_arm_release = false;

void ParkedArmHook(void *) noexcept {
    std::unique_lock<std::mutex> lock(g_arm_mutex);
    g_arm_entered = true;
    g_arm_cv.notify_all();
    g_arm_cv.wait(lock, [] { return g_arm_release; });
}

/// Arms `VitureHeadPoseSource::SetTestRecentreArmHook` for the enclosing
/// scope. The destructor clears the hook and releases a still-parked poster,
/// so no test failure can leave a thread parked forever.
class ScopedArmHook {
  public:
    ScopedArmHook() {
        {
            const std::lock_guard<std::mutex> lock(g_arm_mutex);
            g_arm_entered = false;
            g_arm_release = false;
        }
        VitureHeadPoseSource::SetTestRecentreArmHook(&ParkedArmHook, nullptr);
    }

    ~ScopedArmHook() {
        VitureHeadPoseSource::SetTestRecentreArmHook(nullptr, nullptr);
        {
            const std::lock_guard<std::mutex> lock(g_arm_mutex);
            g_arm_release = true;
        }
        g_arm_cv.notify_all();
    }

    ScopedArmHook(const ScopedArmHook &) = delete;
    ScopedArmHook &operator=(const ScopedArmHook &) = delete;
};

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

/// CXX-06 rule: the FIRST published sample is anchored to the host timeline at
/// poll time. `ClockMapper::AddSample` runs before the first `Map`, so the
/// SDK's own seconds epoch (here 42 s) must never leak into `HeadSample::time`;
/// the published instant is exactly the injected host clock's now (5 s).
TEST(VitureFault, FirstPublishedSampleIsAnchoredToTheHostTimeline) {
    FakeVitureApi api;
    ManualHostClock clock(5'000'000'000);
    api.poll_script = {Ok(CgSample(1, 42'000'000'000, CG_TRACK_STABLE, 10.0))};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));
    EXPECT_EQ(sample.time, 5'000'000'000) << "the first sample must map to its host arrival instant, not the SDK epoch";
    source.Stop();
}

/// A sustained poll failure (past the 250 ms warm-up grace) tears the device
/// down and reconnects on a clock-gated backoff: 100 ms first, then 200 ms for
/// the second consecutive failure. Failures *inside* the grace keep the
/// session (`TransientPollErrorsInsideTheGraceKeepTheSession`).
TEST(VitureFault, PollErrorsReconnectWithDoublingBackoff) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0))};
    // Everything after the scripted sample fails: the streak must persist
    // through the warm-up grace before the device is torn down.
    api.empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    EXPECT_EQ(first.seq, 1U);

    // The first sustained failure destroys the device. The wait pumps the
    // clock in 10 ms steps (an order below the 100 ms backoff), because the
    // retry pacing inside the grace is clock-gated: a coarse step could jump
    // past the whole backoff before the test observes the destroy.
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{kTenMillisecondsNs}, [&] { return api.destroy_calls.load() == 1U; }));
    EXPECT_EQ(api.create_calls.load(), 1U);

    // The first backoff is 100 ms: after spending half of it no reconnect may
    // be in flight (the pump overshoot is at most one 10 ms step, so the
    // thread cannot have reached the 100 ms deadline yet).
    clock.Advance(Duration{50 * kMillisecondNs});
    EXPECT_EQ(api.create_calls.load(), 1U) << "the 100 ms backoff must not release after 50 ms";

    // Release the first backoff and let the recreated session fail again.
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{kTenMillisecondsNs}, [&] { return api.create_calls.load() == 2U; }));
    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{2 * kTenMillisecondsNs}, [&] { return api.destroy_calls.load() == 2U; }));

    // The second consecutive failure doubles the backoff to 200 ms; the pump
    // overshoot is at most one 20 ms step, so +100 ms cannot reach 200 ms.
    clock.Advance(Duration{kHundredMillisecondsNs});
    EXPECT_EQ(api.create_calls.load(), 2U) << "the doubled backoff must not release after 100 ms";

    // Let the third session succeed and publish the second scripted sample.
    api.empty_poll_result = Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 1.0));
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{200 * kMillisecondNs}, [&] {
        return source.TryGetLatest(second, Duration{0}) && second.state == TrackState::Stable && second.seq >= 2U;
    }));
    EXPECT_EQ(api.create_calls.load(), 3U);
    EXPECT_GT(second.seq, first.seq);
    EXPECT_NEAR(YawDegrees(second.pose), 1.0, 0.1) << "the reconnected sample must be the next scripted one";
    EXPECT_GT(second.time, first.time) << "the reconnected sample must advance the mapped time";
    source.Stop();
}

/// HIL finding (2026-10-06): the Carina VIO engine fails the first poll(s)
/// right after `start` and recovers within milliseconds. Failures inside
/// `kReconnectAfter` must keep the session alive: the previous policy
/// recreated the device on the first `-3` and never published a sample on real
/// hardware, while the vendor quick-start (which ignores the error) streams
/// fine from the second poll.
TEST(VitureFault, TransientPollErrorsInsideTheGraceKeepTheSession) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Err<cg_head_sample>(Status{StatusCode::Device, "fake: warm-up -3"}),
                       Err<cg_head_sample>(Status{StatusCode::Device, "fake: warm-up -3"}),
                       Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 2.0))};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{1 * kMillisecondNs},
                                 [&] { return source.TryGetLatest(sample, Duration{0}) && sample.seq >= 1U; }));
    EXPECT_EQ(api.create_calls.load(), 1U) << "a warm-up error must not recreate the device";
    EXPECT_EQ(api.destroy_calls.load(), 0U) << "a warm-up error must not tear the device down";
    EXPECT_NEAR(YawDegrees(sample.pose), 2.0, 0.1) << "the session must publish once warm-up succeeds";
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

    // The feed is exhausted. The failing polls first stay inside the warm-up
    // grace (the session is kept, quiet synthetics take over) and only a
    // sustained outage past the grace tears the device down.
    //
    // Quiet for 400 ms: still Stable (threshold 500 ms, clock frozen here).
    clock.Advance(Duration{400 * kMillisecondNs});
    ASSERT_TRUE(source.TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.state, TrackState::Stable);

    // Quiet past 500 ms: Unstable. `WaitBackoff` evaluates `PublishQuiet`
    // every iteration, so once the clock is past the threshold the synthetic
    // is published without a wall-clock delay; its time is the frozen instant
    // the source observed.
    clock.Advance(Duration{200 * kMillisecondNs});
    ASSERT_TRUE(WaitForState(source, TrackState::Unstable, sample));
    EXPECT_GE(sample.time, 500 * kMillisecondNs);
    EXPECT_LT(sample.time, 1000 * kMillisecondNs);

    // Quiet past 1000 ms: Lost.
    clock.Advance(Duration{600 * kMillisecondNs});
    ASSERT_TRUE(WaitForState(source, TrackState::Lost, sample));
    EXPECT_GE(sample.time, 1000 * kMillisecondNs);

    // The sustained outage has long passed the grace: the device was torn down.
    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.destroy_calls.load() >= 1U; }));
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
    // The counter advances before the delay starts, so this proves the polling
    // thread is parked inside `PollPose` without a real-time sleep.
    ASSERT_TRUE(WaitFor([&] { return api.poll_calls.load() >= 1U; }));

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

/// A failed `ResetOriginCarina` is now asynchronous: `Recenter` returns `Ok`
/// once the request is posted, the polling thread calls the seam, and the
/// rejection withdraws the read-time correction so the stream stays in its
/// pre-recentre frame. The pose the seam receives is the captured pose in the
/// SDK `float[7]` layout.
TEST(VitureFault, ResetFailureWithdrawsThePostedCorrection) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kSdkNs = 1'000'000'000;
    // The first scripted poll succeeds; every later poll succeeds too (same
    // pose, so whichever sample is newest at the reset has identical values),
    // which keeps the device alive for the Recenter.
    api.poll_script = {Ok(CgSample(1, kSdkNs, CG_TRACK_STABLE, 10.0))};
    api.empty_poll_result = Ok(CgSample(2, kSdkNs, CG_TRACK_STABLE, 10.0));
    api.reset_origin_result = Err<void>(Status{StatusCode::Device, "fake: reset rejected"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    const Result<void> result = source.Recenter();
    ASSERT_TRUE(result.ok()) << result.status().message() << " (Recenter only posts the request)";
    // `reset_origin_calls` increments before the seam body runs, so wait on the
    // payload flag (published after the pose copy) instead; the counter then
    // proves the seam was called exactly once.
    ASSERT_TRUE(WaitFor([&] { return api.has_reset_pose.load(std::memory_order_acquire); }));
    EXPECT_EQ(api.reset_origin_calls.load(), 1U);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[0]), sample.pose.position.x, 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[1]), sample.pose.position.y, 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[2]), sample.pose.position.z, 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[3]), sample.pose.rotation.w(), 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[4]), sample.pose.rotation.x(), 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[5]), sample.pose.rotation.y(), 1e-6);
    EXPECT_NEAR(static_cast<double>(api.last_reset_pose[6]), sample.pose.rotation.z(), 1e-6);

    // The rejection withdrew the correction: the stream reads its
    // pre-recentre heading again (the feed keeps sweeping at 10 degrees).
    ASSERT_TRUE(WaitFor([&] {
        HeadSample latest = PlaceholderSample();
        return source.TryGetLatest(latest, Duration{0}) && std::abs(YawDegrees(latest.pose)) > 5.0;
    })) << "a failed reset must not leave the stream recentred";
    source.Stop();
}

/// CXX-02 regression: a resolution that fails must not clobber an arm posted
/// while that resolution was in flight. The failing seam call for post #1 is
/// parked; post #2 arms and holds its arming transaction open inside
/// `recentre_mutex_` (the test arm hook). The failure then has to observe the
/// newer generation and skip its withdrawal, so post #2 stays armed and
/// reader-visible before its own resolution completes. On the old code the
/// arm stores ran outside the mutex, so that failure zeroed the offset while
/// leaving `pending` set.
TEST(VitureFault, FailedResolutionCannotClobberAFreshArm) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 10.0)),
                       Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 10.0)),
                       Ok(CgSample(3, 1'020'000'000, CG_TRACK_STABLE, 10.0))};
    api.empty_poll_result = Ok(CgSample(3, 1'020'000'000, CG_TRACK_STABLE, 10.0));
    // Resolution #1 fails, resolution #2 succeeds.
    api.reset_origin_script = {Err<void>(Status{StatusCode::Device, "fake: reset rejected"}), Ok()};

    // Park resolution #1 in the seam so post #2 can race it deterministically.
    std::mutex gate_mutex;
    std::condition_variable gate_cv;
    bool resolution1_entered = false;
    bool resolution1_release = false;
    bool resolution2_entered = false;
    bool resolution2_release = false;
    api.on_reset_origin = [&](std::uint64_t call) {
        std::unique_lock<std::mutex> lock(gate_mutex);
        if (call == 1U) {
            resolution1_entered = true;
            gate_cv.notify_all();
            gate_cv.wait(lock, [&] { return resolution1_release; });
        } else if (call == 2U) {
            resolution2_entered = true;
            gate_cv.notify_all();
            gate_cv.wait(lock, [&] { return resolution2_release; });
        }
    };

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    // Post #1: the polling thread enters the failing resolution and parks.
    ASSERT_TRUE(source.Recenter().ok());
    bool entered1 = false;
    {
        std::unique_lock<std::mutex> lock(gate_mutex);
        entered1 = gate_cv.wait_for(lock, std::chrono::seconds(2), [&] { return resolution1_entered; });
    }
    EXPECT_TRUE(entered1) << "resolution #1 never entered the seam";

    // Post #2 while resolution #1 is in flight; the arm hook parks the poster
    // *before* its arming transaction, exactly in the window the old code
    // stored the arm outside the mutex. From here on failures use EXPECT and
    // run to the joins.
    ScopedArmHook arm_hook;
    Result<void> second = Err<void>(Status{StatusCode::Internal, "the poster never ran"});
    std::thread poster([&] { second = source.Recenter(); });
    bool entered_arm = false;
    {
        std::unique_lock<std::mutex> lock(g_arm_mutex);
        entered_arm = g_arm_cv.wait_for(lock, std::chrono::seconds(2), [&] { return g_arm_entered; });
    }
    EXPECT_TRUE(entered_arm) << "post #2 never reached the pre-arming seam";

    // Let the failed resolution run to completion while the poster waits:
    // releasing resolution #1 clears the *old* generation's state. The poll
    // counter can only advance after `HandleRecentre` returned, so it proves
    // the withdrawal finished before the poster arms.
    const std::uint64_t polls_before_release = api.poll_calls.load();
    {
        const std::lock_guard<std::mutex> lock(gate_mutex);
        resolution1_release = true;
    }
    gate_cv.notify_all();
    EXPECT_TRUE(WaitFor([&] { return api.poll_calls.load() > polls_before_release; }, std::chrono::seconds(2)))
        << "the failing resolution did not complete";

    // Now let the poster arm. The fresh arm must survive the earlier failure.
    {
        const std::lock_guard<std::mutex> lock(g_arm_mutex);
        g_arm_release = true;
    }
    g_arm_cv.notify_all();
    poster.join();
    EXPECT_TRUE(second.ok()) << second.status().message();

    // Resolution #2 now reaches the seam and parks. While it is still
    // unresolved, the read path must already carry the fresh arm: the old
    // clobber left the offset zeroed, so this read would still show the raw
    // 10-degree heading.
    bool entered2 = false;
    {
        std::unique_lock<std::mutex> lock(gate_mutex);
        entered2 = gate_cv.wait_for(lock, std::chrono::seconds(2), [&] { return resolution2_entered; });
    }
    EXPECT_TRUE(entered2) << "the second post was not resolved by the polling thread";
    HeadSample armed = PlaceholderSample();
    const bool have_armed = source.TryGetLatest(armed, Duration{0});
    EXPECT_NEAR(YawDegrees(armed.pose), 0.0, 0.1)
        << "the second post must stay armed across the first resolution's failure";

    {
        const std::lock_guard<std::mutex> lock(gate_mutex);
        resolution2_release = true;
    }
    gate_cv.notify_all();
    EXPECT_TRUE(have_armed);
    EXPECT_TRUE(WaitFor([&] { return api.reset_origin_calls.load() == 2U; }));
    source.Stop();
}

/// While a backoff is pending the device is destroyed, so `Recenter` is
/// `NotReady` and the seam is never called in that state.
TEST(VitureFault, RecentreDuringBackoffIsNotReady) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 5.0)};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    // The feed is exhausted: the sustained failure (past the grace) tears the
    // device down and parks in the 100 ms backoff. The wait pumps the clock
    // because the retry pacing inside the grace is clock-gated.
    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.destroy_calls.load() >= 1U; }));
    ASSERT_FALSE(api.device_alive.load());
    const std::uint64_t resets_before = api.reset_origin_calls.load();

    const Result<void> result = source.Recenter();
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.reset_origin_calls.load(), resets_before) << "a destroyed device must never see the reset call";
    source.Stop();
}

/// A recentre posted while the device is about to be lost survives the outage:
/// it is armed for readers immediately, never reaches the destroyed device,
/// and is applied exactly once by the polling thread after the next successful
/// recreate+publish. The feed is gated so every step is driven by observable
/// state (poll, destroy and reset counters) and the manual clock, not by a
/// wall-clock race against the poll loop.
TEST(VitureFault, RecentreWorksAfterSuccessfulRecreate) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kSdkNs = 1'000'000'000;
    api.poll_script = {Ok(CgSample(1, kSdkNs, CG_TRACK_STABLE, 3.0)),
                       Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"})};
    // Until the reconnect every poll fails: the outage is sustained and the
    // device is torn down once the warm-up grace has passed.
    api.empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"});
    api.SetPollGate(true);

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    api.AllowOnePoll();
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    // Poll 2 is in flight (blocked on the gate) and scripted to fail. Post the
    // recentre now, while the device is still alive, and arm the correction:
    // the captured sample must read yaw-zero immediately, before any seam call.
    ASSERT_TRUE(WaitFor([&] { return api.poll_calls.load() >= 2U; }));
    const Result<void> result = source.Recenter();
    ASSERT_TRUE(result.ok()) << result.status().message();
    EXPECT_EQ(api.reset_origin_calls.load(), 0U);
    HeadSample armed = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(armed, Duration{0}));
    ASSERT_EQ(armed.seq, 1U);
    EXPECT_NEAR(YawDegrees(armed.pose), 0.0, 0.1);

    // Release the failing poll with the clock already past the warm-up grace:
    // the device dies with the request still posted.
    clock.Advance(Duration{300 * kMillisecondNs});
    api.AllowOnePoll();
    ASSERT_TRUE(WaitFor([&] { return api.destroy_calls.load() >= 1U; }));
    EXPECT_FALSE(api.device_alive.load());

    // Every poll of the recreated device succeeds, so it publishes a fresh
    // session sample for the pending reset to target.
    api.empty_poll_result = Ok(CgSample(2, kSdkNs + 10'000'000, CG_TRACK_STABLE, 4.0));
    EXPECT_EQ(api.reset_origin_calls.load(), 0U) << "a destroyed device must never see the seam";
    HeadSample during = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(during, Duration{0}));
    EXPECT_NEAR(YawDegrees(during.pose), 0.0, 0.1) << "the requested recentre must stay armed across the outage";

    // Release the backoff on the manual clock. Advancing inside the wait
    // cannot race the source's deadline computation: if the clock moves before
    // the wait arms, the next iteration advances it again.
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.device_alive.load(); }));

    // The recreated device has not published yet, so the pending request must
    // wait for a fresh sample instead of applying the pre-loss pose.
    EXPECT_EQ(api.reset_origin_calls.load(), 0U);
    api.AllowOnePoll();
    // Require the seam call and the reader-visible effect together: the armed
    // correction can zero a read before the polling thread resolves the
    // request, so only the counter proves the resolution happened, and
    // `state == Stable` keeps quiet synthetics out of the read.
    HeadSample recentred = PlaceholderSample();
    ASSERT_TRUE(WaitFor([&] {
        return api.reset_origin_calls.load() == 1U && api.has_reset_pose.load(std::memory_order_acquire) &&
               source.TryGetLatest(recentred, Duration{0}) && recentred.state == TrackState::Stable &&
               recentred.seq >= 2U && std::abs(YawDegrees(recentred.pose)) < 0.1;
    })) << "the applied recentre must zero the newest session sample";
    EXPECT_EQ(api.reset_origin_calls.load(), 1U);
    EXPECT_TRUE(api.has_reset_pose.load(std::memory_order_acquire));

    // Once-only: further successful polls must not call the seam again.
    api.AllowOnePoll();
    HeadSample next = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, next, 3U));
    EXPECT_EQ(api.reset_origin_calls.load(), 1U) << "the pending recentre must be applied exactly once";
    source.Stop();
}

/// `Recenter` posts its request and returns: the polling thread must reach
/// `PollPose` again without any reader call. The old read barrier parked the
/// poll loop on `recentre_hold_` for up to 1 s, so no `TryGetLatest` may be
/// used to observe progress here (one would release the old barrier).
TEST(VitureFault, RecenterDoesNotStallThePollingThread) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 5.0), CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 6.0),
                   CgSample(3, 1'020'000'000, CG_TRACK_STABLE, 7.0)};
    // Keep the device alive after the list is exhausted so the recentre has a
    // live seam to reach.
    api.empty_poll_result = Ok(CgSample(3, 1'020'000'000, CG_TRACK_STABLE, 7.0));

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    const std::uint64_t polls_before = api.poll_calls.load();
    const auto call_start = std::chrono::steady_clock::now();
    const Result<void> result = source.Recenter();
    const auto call_elapsed = std::chrono::steady_clock::now() - call_start;
    ASSERT_TRUE(result.ok()) << result.status().message();
    EXPECT_LT(call_elapsed, std::chrono::milliseconds(250)) << "Recenter must return once the request is posted";
    ASSERT_TRUE(WaitFor([&] { return api.reset_origin_calls.load() == 1U; }));
    // Progress is observed through the seam, never through TryGetLatest.
    ASSERT_TRUE(WaitFor([&] { return api.poll_calls.load() > polls_before; }))
        << "the polling thread stalled waiting for a reader after the recentre";
    source.Stop();
}

/// The fake models the SDK device lifetime: pose calls fail `NotReady` while
/// destroyed and work again after a create.
TEST(VitureFault, FakeVitureApiModelsDeviceLifetime) {
    FakeVitureApi api;
    const std::array<float, kViturePoseFloatCount> pose{0.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F, 0.0F};
    EXPECT_EQ(api.PollPose().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.StartPose().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.ResetOriginCarina(pose).status().code(), StatusCode::NotReady);
    EXPECT_FALSE(api.device_alive.load());

    ASSERT_TRUE(api.CreateDevice().ok());
    EXPECT_TRUE(api.device_alive.load());
    ASSERT_TRUE(api.StartPose().ok());
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)};
    EXPECT_TRUE(api.PollPose().ok());

    api.DestroyDevice();
    EXPECT_FALSE(api.device_alive.load());
    EXPECT_EQ(api.PollPose().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.ResetOriginCarina(pose).status().code(), StatusCode::NotReady);
    api.DestroyDevice(); // Idempotent.
}

/// A failed `StartPose` tears the device down; the next `Start` is clean and
/// reaches the feed.
TEST(VitureFault, StartPoseFailureDestroysTheDeviceThenRetrySucceeds) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.start_script = {Err<void>(Status{StatusCode::Device, "fake: start pose failed"})};
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)};
    // Keep the retried session alive after the scripted sample: the retry must
    // reach the feed, and the `device_alive` observation below must not race
    // the poll loop tearing down an exhausted feed.
    api.empty_poll_result = Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 1.0));

    VitureHeadPoseSource source(api, clock);
    const Result<void> first = source.Start();
    ASSERT_FALSE(first.ok());
    EXPECT_EQ(first.status().code(), StatusCode::Device);
    EXPECT_EQ(api.create_calls.load(), 1U);
    EXPECT_EQ(api.start_calls.load(), 1U);
    EXPECT_EQ(api.destroy_calls.load(), 1U);
    EXPECT_FALSE(api.device_alive.load());

    ASSERT_TRUE(source.Start().ok());
    EXPECT_EQ(api.create_calls.load(), 2U);
    EXPECT_EQ(api.start_calls.load(), 2U);
    EXPECT_TRUE(api.device_alive.load());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));
    source.Stop();
}

/// A yaw step across the +/-180 degree seam must not make the prediction snap:
/// the rate is derived from the unwrapped difference. The pair genuinely
/// crosses the seam (179.9 -> -179.9, i.e. +0.2 unwrapped vs -359.8 wrapped),
/// and the prediction is 2.5 sample periods, because an integer number of
/// periods would alias the wrapped 360-degree error away modulo a full turn.
TEST(VitureFault, PredictionCrossesTheYawSeamWithoutASnap) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kSdkNs = 1'000'000'000;
    constexpr std::int64_t kPeriodNs = 10'000'000;
    constexpr std::int64_t kPredictNs = 25'000'000; // 2.5 periods.
    api.samples = {CgSample(1, kSdkNs, CG_TRACK_STABLE, 179.9),
                   CgSample(2, kSdkNs + kPeriodNs, CG_TRACK_STABLE, -179.9)};
    // The gate pins the host instants of the two polls, so the mapped
    // inter-sample delta is exactly one period.
    api.SetPollGate(true);

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    api.AllowOnePoll();
    HeadSample newer = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, newer, 1U));
    clock.Advance(Duration{kPeriodNs});
    api.AllowOnePoll();
    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, newest, 2U));
    ASSERT_NEAR(YawDegrees(newest.pose), -179.9, 0.01);

    HeadSample predicted = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(predicted, Duration{kPredictNs}));
    const core_math::Quat delta = predicted.pose.rotation * newest.pose.rotation.Inverse();
    const double delta_yaw_deg = 2.0 * std::atan2(delta.y(), delta.w()) * kRadiansToDegrees;
    // +0.2 degrees per 10 ms is 20 deg/s: 25 ms continues the heading across
    // the seam to -179.4 (delta +0.5). A wrapped rate (-35980 deg/s) would add
    // -899.5 degrees, which is 180.5 modulo a full turn, so the pose visibly
    // snaps instead of continuing.
    EXPECT_NEAR(delta_yaw_deg, 0.5, 0.05) << "predicted yaw snapped at the seam";
    EXPECT_NEAR(YawDegrees(predicted.pose), -179.4, 0.05) << "the prediction did not continue past the seam";
    source.Stop();
}

/// If the SDK clock restarts when the device is recreated, the first mapped
/// time must not regress below the last published time (U-01 assumption).
TEST(VitureFault, SdkClockRestartAfterReconnectDoesNotRegressMappedTime) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kSdkBefore = 100'000'000'000;
    api.poll_script.push_back(Ok(CgSample(1, kSdkBefore, CG_TRACK_STABLE, 0.0)));
    for (std::uint32_t i = 1; i <= 8; ++i) {
        api.poll_script.push_back(
            Ok(CgSample(i + 1, kSdkBefore + static_cast<std::int64_t>(i) * 10'000'000, CG_TRACK_STABLE, 0.1 * i)));
    }
    // After the scripted feed every poll fails: the outage must pass the
    // warm-up grace before the device is torn down.
    api.empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    HeadSample last_before = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, last_before, 9U));

    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.destroy_calls.load() >= 1U; }));
    // The recreated session reports a restarted SDK clock.
    api.empty_poll_result = Ok(CgSample(99, 1'000'000'000, CG_TRACK_STABLE, 1.0));
    // Pump the backoff clock and require a *Stable* sample: a deadline armed
    // after an advance is reached by a later one, and quiet synthetics cannot
    // satisfy the predicate.
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] {
        return source.TryGetLatest(second, Duration{0}) && second.state == TrackState::Stable && second.seq >= 10U;
    }));
    EXPECT_GT(second.time, last_before.time) << "a restarted SDK clock must not regress the mapped time";
    source.Stop();
}

/// After 10 consecutive failed recreates the thread stops recreating, keeps
/// probing and reporting Lost, then recovers on a successful poll without
/// breaking sequence order or creating another device.
TEST(VitureFault, ReconnectCapStopsRecreatingAndRecoversOnLostProbe) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_delay_ns = 1'000'000; // 1 ms per poll: paces the fake, no assertion depends on it.
    const std::int64_t kSdkStart = 1'000'000'000;
    api.poll_script.push_back(Ok(CgSample(1, kSdkStart, CG_TRACK_STABLE, 0.0)));
    // Every poll after the first sample fails until the recovery below: each
    // recreate attempt requires the warm-up grace to pass, and the cache of
    // scripted errors must not run out mid-way through the cap.
    api.empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    // Release every backoff (0.1+0.2+0.4+0.8+1.6+2*5 = 13.1 s) until the cap
    // stops recreating. The wait pumps the clock in 500 ms steps: every
    // reconnect deadline is at most 2 s away, and a deadline armed after a
    // step is reached by a later one.
    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{500 * kMillisecondNs}, [&] { return api.create_calls.load() >= 11U; }));
    ASSERT_EQ(api.create_calls.load(), 11U) << "10 recreates must have been attempted";
    HeadSample lost = PlaceholderSample();
    ASSERT_TRUE(WaitForState(source, TrackState::Lost, lost));
    EXPECT_EQ(api.create_calls.load(), 11U);

    // The capped thread keeps probing every 2 s on the clock without creating
    // another device. Pair each clock release with the poll it triggers, so
    // the negative assertion does not depend on a wall-clock delay.
    for (int probe = 0; probe < 2; ++probe) {
        const std::uint64_t polls_before = api.poll_calls.load();
        ASSERT_TRUE(WaitForAdvancing(clock, VitureHeadPoseSource::kMaxBackoff,
                                     [&] { return api.poll_calls.load() > polls_before; }));
        EXPECT_EQ(api.create_calls.load(), 11U) << "a capped probe must not create a device";
    }
    HeadSample still_lost = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(still_lost, Duration{0}));
    EXPECT_EQ(still_lost.state, TrackState::Lost);

    // A successful poll resets the streak and continues the sequence. Each
    // capped probe re-arms its 2 s deadline from the current clock, so keep
    // pumping until the recovered sample is reached.
    api.empty_poll_result = Ok(CgSample(2, kSdkStart + 10'000'000, CG_TRACK_STABLE, 1.0));
    HeadSample recovered = PlaceholderSample();
    ASSERT_TRUE(WaitForAdvancing(clock, VitureHeadPoseSource::kMaxBackoff, [&] {
        return source.TryGetLatest(recovered, Duration{0}) && recovered.state == TrackState::Stable &&
               recovered.seq > lost.seq;
    }));
    EXPECT_GT(recovered.time, lost.time);
    EXPECT_EQ(api.create_calls.load(), 11U);
    source.Stop();
}

/// Once `Stop` returns, a pending backoff must not reconnect any more, even
/// when the clock the backoff waits on advances afterwards.
TEST(VitureFault, NoReconnectAfterStop) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 0.0)};

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    // The feed is exhausted now; the sustained failure (past the warm-up
    // grace) tears the device down and parks in the 100 ms backoff.
    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.destroy_calls.load() >= 1U; }));
    EXPECT_EQ(api.create_calls.load(), 1U);

    source.Stop();
    // Stop joins the polling thread, so no API call can follow; advancing the
    // clock cannot revive the cancelled backoff and no wall-clock grace period
    // is needed.
    const std::uint64_t creates_at_stop = api.create_calls.load();
    const std::uint64_t destroys_at_stop = api.destroy_calls.load();
    clock.Advance(Duration{5'000'000'000});
    EXPECT_EQ(api.create_calls.load(), creates_at_stop);
    EXPECT_EQ(api.destroy_calls.load(), destroys_at_stop);
}

/// Sequence and time stay strictly monotonic across a reconnect.
TEST(VitureFault, SequenceStaysMonotonicAcrossReconnect) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.poll_script = {Ok(CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 1.0))};
    // After the scripted sample every poll fails: the outage must pass the
    // warm-up grace before the reconnect.
    api.empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: dropped"});

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));

    ASSERT_TRUE(
        WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] { return api.destroy_calls.load() >= 1U; }));
    // The recreated session publishes the next sample.
    api.empty_poll_result = Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 2.0));
    // Pump the backoff clock and require the next *Stable* sample: a deadline
    // armed after an advance is reached by a later one, and quiet synthetics
    // (which repeat the previous pose) cannot satisfy the predicate.
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForAdvancing(clock, Duration{kHundredMillisecondsNs}, [&] {
        return source.TryGetLatest(second, Duration{0}) && second.state == TrackState::Stable && second.seq > first.seq;
    }));
    EXPECT_NEAR(YawDegrees(second.pose), 2.0, 0.1) << "the reconnected sample must be the next scripted one";
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

/// `LastSdkSeconds` is the diagnostics accessor the probe records: empty
/// before the first publish, then the SDK seconds stamp of the newest
/// published sample. The poll gate makes the two stamps deterministic.
TEST(VitureFault, LastSdkSecondsTracksNewestPublishedSample) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kFirstSdkNs = 5'000'000'000;
    constexpr std::int64_t kSecondSdkNs = 5'100'000'000;
    api.samples = {CgSample(1, kFirstSdkNs, CG_TRACK_STABLE, 0.0), CgSample(2, kSecondSdkNs, CG_TRACK_STABLE, 1.0)};

    VitureHeadPoseSource source(api, clock);
    ASSERT_FALSE(source.LastSdkSeconds().has_value()) << "the stamp must be empty before the first publish";

    api.SetPollGate(true);
    ASSERT_TRUE(source.Start().ok());
    api.AllowOnePoll();
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    ASSERT_TRUE(source.LastSdkSeconds().has_value());
    EXPECT_DOUBLE_EQ(*source.LastSdkSeconds(), core_math::ToSeconds(kFirstSdkNs));

    api.AllowOnePoll();
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, second, 2U));
    ASSERT_TRUE(source.LastSdkSeconds().has_value());
    EXPECT_DOUBLE_EQ(*source.LastSdkSeconds(), core_math::ToSeconds(kSecondSdkNs));

    source.Stop();
    // Stop is total and keeps the newest sample readable, so the stamp stays too.
    ASSERT_TRUE(source.TryGetLatest(second, Duration{0}));
    ASSERT_TRUE(source.LastSdkSeconds().has_value());
    EXPECT_DOUBLE_EQ(*source.LastSdkSeconds(), core_math::ToSeconds(kSecondSdkNs));
}

/// CXX-12: prediction must not extrapolate through quiet synthetics. While the
/// newest sample is `Unstable`/`Lost` (the last real pose with no new data),
/// `TryGetLatest` returns it unchanged regardless of `predict`; the nonzero
/// rate established by the live samples must not be applied.
TEST(VitureFault, PredictionDoesNotExtrapolateThroughQuietSamples) {
    FakeVitureApi api;
    ManualHostClock clock;
    constexpr std::int64_t kSdkNs = 1'000'000'000;
    constexpr std::int64_t kPeriodNs = 10'000'000;
    constexpr std::int64_t kPredictNs = 50'000'000;
    api.samples = {CgSample(1, kSdkNs, CG_TRACK_STABLE, 0.0), CgSample(2, kSdkNs + kPeriodNs, CG_TRACK_STABLE, 10.0)};
    api.SetPollGate(true);

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    api.AllowOnePoll();
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    clock.Advance(Duration{kPeriodNs});
    api.AllowOnePoll();
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, second, 2U));
    ASSERT_EQ(second.state, TrackState::Stable);
    // The pair establishes 10 degrees per 10 ms; a 50 ms prediction on a live
    // sample would add 50 degrees, so the quiet checks below have teeth.

    // Exhaust the feed: the next gated poll fails; the session stays inside
    // the warm-up grace while the quiet synthetics take over.
    api.AllowOnePoll();

    const auto check_quiet = [&](TrackState expected) {
        HeadSample raw = PlaceholderSample();
        ASSERT_TRUE(WaitForState(source, expected, raw));
        HeadSample predicted = PlaceholderSample();
        ASSERT_TRUE(source.TryGetLatest(predicted, Duration{kPredictNs}));
        EXPECT_EQ(predicted.time, raw.time) << "a quiet synthetic must not be advanced by a prediction";
        EXPECT_NEAR(YawDegrees(predicted.pose), YawDegrees(raw.pose), 1e-12);
        EXPECT_NEAR(YawDegrees(predicted.pose), 10.0, 0.1) << "the quiet pose must be the last real sample";
    };

    clock.Advance(Duration{600 * kMillisecondNs});
    check_quiet(TrackState::Unstable);

    // The backoff released at the new clock instant, so the thread is blocked
    // on the closed gate again; one granted poll fails and publishes Lost.
    clock.Advance(Duration{600 * kMillisecondNs});
    api.AllowOnePoll();
    check_quiet(TrackState::Lost);
    source.Stop();
}

/// CXX-17: the read-time recentre offset applies only to samples up to
/// `until_seq`. A sample published after the reset already arrives recentred
/// from the SDK (here, from the scripted feed's own recentre), so applying the
/// production offset again would double-correct it.
TEST(VitureFault, ReadTimeOffsetIsDroppedAfterUntilSeq) {
    FakeVitureApi api;
    ManualHostClock clock;
    ASSERT_TRUE(api.FeedScript(FakeScript::YawSweep(1000.0), 100.0).ok());
    api.SetPollGate(true);

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    api.AllowOnePoll();
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, first, 1U));
    ASSERT_NEAR(YawDegrees(first.pose), 10.0, 0.1);

    api.AllowOnePoll();
    HeadSample second = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, second, 2U));
    ASSERT_NEAR(YawDegrees(second.pose), 20.0, 0.1);

    ASSERT_TRUE(source.Recenter().ok());
    // Armed immediately: the newest pre-reset sample reads yaw-zero.
    HeadSample armed = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(armed, Duration{0}));
    EXPECT_NEAR(YawDegrees(armed.pose), 0.0, 0.1);

    // Release the next poll: sample 3 (raw 30 deg) is published, then the
    // service pass resolves the reset. The scripted feed recentres at the
    // current heading (30 deg), so sample 4 is emitted as 40 - 30 = 10 deg.
    api.AllowOnePoll();
    ASSERT_TRUE(WaitFor([&] { return api.reset_origin_calls.load() == 1U; }));

    api.AllowOnePoll();
    HeadSample fourth = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, fourth, 4U));
    ASSERT_EQ(fourth.state, TrackState::Stable);
    EXPECT_NEAR(YawDegrees(fourth.pose), 10.0, 0.5)
        << "the read-time offset must not apply to samples published after until_seq (a regression would "
           "double-correct to -20 deg)";
    source.Stop();
}

/// CXX-14: `Recenter` returning `Ok` means the request was posted, not
/// applied. A `Stop` that races the post withdraws the unresolved request: the
/// seam is never called, the read-time correction is dropped, and the stream
/// stays in its never-reset frame.
TEST(VitureFault, StopWithdrawsARecentreThatReturnedOk) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSample(1, 1'000'000'000, CG_TRACK_STABLE, 10.0)};
    api.empty_poll_result = Ok(CgSample(2, 1'010'000'000, CG_TRACK_STABLE, 10.0));

    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(WaitForSample(source, sample, 1U));

    ScopedArmHook arm_hook;
    Result<void> poster_result = Err<void>(Status{StatusCode::Internal, "the poster never ran"});
    std::thread poster([&] { poster_result = source.Recenter(); });
    bool entered = false;
    {
        std::unique_lock<std::mutex> lock(g_arm_mutex);
        entered = g_arm_cv.wait_for(lock, std::chrono::seconds(2), [&] { return g_arm_entered; });
    }
    EXPECT_TRUE(entered) << "the poster never reached the pre-arming seam";

    // Stop joins the polling thread (so the request can never be serviced)
    // and then blocks on the caller mutex the parked poster holds.
    std::thread stopper([&] { source.Stop(); });
    EXPECT_TRUE(WaitFor([&] { return api.stop_requested(); }));

    {
        const std::lock_guard<std::mutex> lock(g_arm_mutex);
        g_arm_release = true;
    }
    g_arm_cv.notify_all();
    poster.join();
    stopper.join();

    EXPECT_TRUE(poster_result.ok()) << poster_result.status().message();
    EXPECT_EQ(api.reset_origin_calls.load(), 0U) << "a request withdrawn by Stop must never reach the seam";
    EXPECT_FALSE(source.Running());
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(after, Duration{0}));
    EXPECT_NEAR(YawDegrees(after.pose), 10.0, 0.1) << "a withdrawn recentre must not leave a read-time correction";
}

} // namespace
} // namespace cg::glasses::test
