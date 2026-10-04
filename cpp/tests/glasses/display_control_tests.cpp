#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>
#include <type_traits>
#include <utility>

#include <gtest/gtest.h>

#include "cg/glasses/display_control.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "fake_display_control.hpp"
#include "fake_viture_api.hpp"

namespace cg::glasses::test {
namespace {

/// Bounded predicate wait for the two concurrency tests; teardown and watchdog
/// only, never wall-clock pacing of the assertion itself.
bool WaitFor(const std::function<bool()> &predicate, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        if (predicate()) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return predicate();
}

/// A stable identity sample for the source lifecycle used by the CXX-01
/// concurrency tests (the display tests never inspect the pose).
cg_head_sample CgSampleForDisplayTest(std::uint32_t sequence) noexcept {
    cg_head_sample sample{};
    sample.host_time = static_cast<cg_time_ns>(sequence) * 1'000'000;
    sample.pose.p = cg_vec3{0.0F, 0.0F, 0.0F};
    sample.pose.q = cg_quat{1.0F, 0.0F, 0.0F, 0.0F};
    sample.state = CG_TRACK_STABLE;
    sample.sequence = sequence;
    return sample;
}

// The interface is the S5 deliverable: pin the exact signatures and the
// noexcept Stop the contract suite and callers rely on.
static_assert(std::is_same_v<decltype(std::declval<const IDisplayControl &>().Get()), Result<DisplayMode>>);
static_assert(
    std::is_same_v<decltype(std::declval<IDisplayControl &>().Set(std::declval<DisplayMode>())), Result<void>>);
static_assert(noexcept(std::declval<IDisplayControl &>().Stop()));

TEST(VitureDisplayControl, SetForwardsToTheSeamAndGetReportsSeamRefreshWithCachedSbs) {
    FakeVitureApi api;
    VitureDisplayControl control(api);

    ASSERT_TRUE(control.Set(DisplayMode{120, true}).ok());
    EXPECT_EQ(api.set_display_mode_calls.load(), 1U);
    EXPECT_EQ(api.last_refresh_hz, 120U);
    EXPECT_TRUE(api.last_sbs);

    // GetRefreshHz is authoritative for the refresh rate; SBS is the cached
    // value because the seam has no SBS getter (provisional pending U-08).
    api.refresh_result = Ok(std::uint32_t{120});
    const Result<DisplayMode> mode = control.Get();
    ASSERT_TRUE(mode.ok());
    EXPECT_EQ((*mode).refresh_hz, 120U);
    EXPECT_TRUE((*mode).sbs);
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 1U);
}

TEST(VitureDisplayControl, ASeamFailureOnGetSurfacesUnchanged) {
    FakeVitureApi api;
    api.refresh_result = Err<std::uint32_t>(Status{StatusCode::Device, "seam: display gone"});
    VitureDisplayControl control(api);

    const Result<DisplayMode> mode = control.Get();
    ASSERT_FALSE(mode.ok());
    EXPECT_EQ(mode.status().code(), StatusCode::Device);
}

TEST(VitureDisplayControl, ASeamFailureOnSetSurfacesAndKeepsTheCachedSbs) {
    FakeVitureApi api;
    VitureDisplayControl control(api);
    ASSERT_TRUE(control.Set(DisplayMode{90, true}).ok());

    api.set_display_mode_result = Err<void>(Status{StatusCode::Device, "seam: set rejected"});
    const Result<void> failed = control.Set(DisplayMode{72, false});
    ASSERT_FALSE(failed.ok());
    EXPECT_EQ(failed.status().code(), StatusCode::Device);

    const Result<DisplayMode> mode = control.Get();
    ASSERT_TRUE(mode.ok());
    EXPECT_EQ((*mode).refresh_hz, 90U); // FakeVitureApi's default refresh result.
    EXPECT_TRUE((*mode).sbs);           // The last successful Set stays cached.
}

TEST(VitureDisplayControl, UnsupportedSeamPassesThroughUnchanged) {
    // U-08 placeholder behaviour: while the display-mode API is unconfirmed, a
    // seam that reports Unsupported must surface it rather than guess a mode.
    FakeVitureApi api;
    api.set_display_mode_result = Err<void>(Status{StatusCode::Unsupported, "u-08: unknown display API"});
    api.refresh_result = Err<std::uint32_t>(Status{StatusCode::Unsupported, "u-08: unknown display API"});
    VitureDisplayControl control(api);

    EXPECT_EQ(control.Set(DisplayMode{90, false}).status().code(), StatusCode::Unsupported);
    EXPECT_EQ(control.Get().status().code(), StatusCode::Unsupported);
}

TEST(VitureDisplayControl, StopIsIdempotentAndLatchesWithoutCallingTheSeam) {
    FakeVitureApi api;
    VitureDisplayControl control(api);

    control.Stop();
    control.Stop();
    EXPECT_TRUE(true); // The calls above must be noexcept and idempotent.
    EXPECT_EQ(api.set_display_mode_calls.load(), 0U);
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 0U);

    EXPECT_EQ(control.Set(DisplayMode{90, false}).status().code(), StatusCode::NotReady);
    EXPECT_EQ(control.Get().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.set_display_mode_calls.load(), 0U);
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 0U);
}

TEST(VitureDisplayControl, RefusesWhileTheSourceIsRunningWithoutCallingTheSeam) {
    FakeVitureApi api;
    bool running = true;
    VitureDisplayControl control(api, [&running](const DisplaySeamAction &action) -> Result<void> {
        if (running) {
            return Err<void>(Status{StatusCode::NotReady, "test gate: source running"});
        }
        return action();
    });

    EXPECT_EQ(control.Set(DisplayMode{120, true}).status().code(), StatusCode::NotReady);
    EXPECT_EQ(control.Get().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.set_display_mode_calls.load(), 0U);
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 0U);

    running = false; // The source stopped: configuring is legal again.
    ASSERT_TRUE(control.Set(DisplayMode{120, true}).ok());
    api.refresh_result = Ok(std::uint32_t{120}); // The seam is authoritative for Get.
    const Result<DisplayMode> mode = control.Get();
    ASSERT_TRUE(mode.ok());
    EXPECT_EQ((*mode).refresh_hz, 120U);
    EXPECT_EQ(api.set_display_mode_calls.load(), 1U);
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 1U);
}

TEST(VitureDisplayControl, WiredToTheVitureSourceRejectsWhileRunningAndAcceptsAfterStop) {
    FakeVitureApi api;
    ManualHostClock clock;
    VitureHeadPoseSource source(api, clock);
    VitureDisplayControl control(
        api, [&source](const DisplaySeamAction &action) { return source.WithDeviceStopped(action); });

    // Before Start the source is not running: configure the display.
    ASSERT_TRUE(control.Set(DisplayMode{90, false}).ok());
    ASSERT_TRUE(source.Start().ok());
    EXPECT_TRUE(source.Running());

    EXPECT_EQ(control.Set(DisplayMode{120, true}).status().code(), StatusCode::NotReady);
    EXPECT_EQ(control.Get().status().code(), StatusCode::NotReady);
    EXPECT_EQ(api.set_display_mode_calls.load(), 1U); // Only the pre-Start set reached the seam.
    EXPECT_EQ(api.get_refresh_hz_calls.load(), 0U);

    source.Stop();
    EXPECT_FALSE(source.Running());
    ASSERT_TRUE(control.Set(DisplayMode{72, true}).ok());
    EXPECT_EQ(api.set_display_mode_calls.load(), 2U);
}

/// CXX-01 regression: hold the display seam open inside `Set` and prove a
/// concurrent `Start` cannot reach `CreateDevice` until the gate releases. The
/// old is-running-predicate design let `Start` run its whole lifecycle while
/// `SetDisplayMode` was still in flight; the gate serialises them. The seam is
/// parked on a condition variable, so the outcome does not depend on timing.
TEST(VitureDisplayControl, AnOpenDisplaySeamBlocksStartUntilItCompletes) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSampleForDisplayTest(1)};
    VitureHeadPoseSource source(api, clock);
    VitureDisplayControl control(
        api, [&source](const DisplaySeamAction &action) { return source.WithDeviceStopped(action); });

    std::mutex seam_mutex;
    std::condition_variable seam_cv;
    bool seam_entered = false;
    bool seam_release = false;
    api.on_set_display_mode = [&](std::uint64_t) {
        std::unique_lock<std::mutex> lock(seam_mutex);
        seam_entered = true;
        seam_cv.notify_all();
        seam_cv.wait(lock, [&] { return seam_release; });
    };

    Result<void> set_result = Err<void>(Status{StatusCode::Internal, "the seam never ran"});
    std::thread setter([&] { set_result = control.Set(DisplayMode{120, true}); });
    bool entered = false;
    {
        std::unique_lock<std::mutex> lock(seam_mutex);
        entered = seam_cv.wait_for(lock, std::chrono::seconds(2), [&] { return seam_entered; });
    }
    EXPECT_TRUE(entered) << "Set never entered the seam";

    // Start must be blocked on the lifecycle gate: with the old predicate it
    // would proceed and call CreateDevice (the predicate only sampled
    // `running_`, which is false before Start).
    std::thread starter([&] { (void)source.Start(); });
    EXPECT_FALSE(WaitFor([&] { return api.create_calls.load() > 0U; }, std::chrono::milliseconds(100)))
        << "Start reached CreateDevice while a display seam call was open";

    {
        const std::lock_guard<std::mutex> lock(seam_mutex);
        seam_release = true;
    }
    seam_cv.notify_all();
    setter.join();
    starter.join();
    source.Stop();

    EXPECT_TRUE(entered);
    ASSERT_TRUE(set_result.ok());
    EXPECT_EQ(api.create_calls.load(), 1U);
    EXPECT_EQ(api.set_display_mode_calls.load(), 1U);
}

/// CXX-01 threaded stress: `Start`/`Stop` racing `Set`/`Get` must never let a
/// lifecycle or poll call observe a display seam action in flight. The fake's
/// `on_device_call` hook flags any overlap; the gate wrapper publishes the
/// in-flight window. Runs under the TSan lane too.
TEST(VitureDisplayControl, StartStopRacingDisplayCallsNeverOverlapASeamCall) {
    FakeVitureApi api;
    ManualHostClock clock;
    api.samples = {CgSampleForDisplayTest(1), CgSampleForDisplayTest(2), CgSampleForDisplayTest(3)};
    api.empty_poll_result = Ok(CgSampleForDisplayTest(3));
    VitureHeadPoseSource source(api, clock);

    std::atomic<bool> in_display{false};
    std::atomic<bool> overlap{false};
    VitureDisplayControl control(api, [&source, &in_display](const DisplaySeamAction &action) -> Result<void> {
        return source.WithDeviceStopped([&action, &in_display]() -> Result<void> {
            in_display.store(true, std::memory_order_seq_cst);
            const Result<void> result = action();
            in_display.store(false, std::memory_order_seq_cst);
            return result;
        });
    });
    api.on_device_call = [&] {
        if (in_display.load(std::memory_order_seq_cst)) {
            overlap.store(true, std::memory_order_seq_cst);
        }
    };

    constexpr int kIterations = 100;
    std::atomic<bool> go{false};
    std::thread setter([&] {
        while (!go.load(std::memory_order_acquire)) {
        }
        for (int i = 0; i < kIterations; ++i) {
            (void)control.Set(DisplayMode{static_cast<std::uint32_t>(60 + (i % 4)), (i % 2) != 0});
            (void)control.Get();
        }
    });
    std::thread lifecycle([&] {
        while (!go.load(std::memory_order_acquire)) {
        }
        for (int i = 0; i < kIterations; ++i) {
            if (source.Start().ok()) {
                (void)WaitFor([&] { return api.poll_calls.load() >= 1U; }, std::chrono::milliseconds(50));
            }
            source.Stop();
        }
    });
    go.store(true, std::memory_order_release);
    setter.join();
    lifecycle.join();
    source.Stop();

    EXPECT_FALSE(overlap.load(std::memory_order_seq_cst))
        << "a lifecycle or poll call ran while a display seam action was in flight";
    EXPECT_GT(api.set_display_mode_calls.load() + api.get_refresh_hz_calls.load(), 0U)
        << "the stress never reached the display seam";
}

TEST(FakeDisplayControl, RoundTripsStateAndInjectsFailures) {
    FakeDisplayControl control;
    EXPECT_TRUE(control.Set(DisplayMode{120, true}).ok());
    EXPECT_EQ(control.set_calls, 1U);
    const Result<DisplayMode> mode = control.Get();
    ASSERT_TRUE(mode.ok());
    EXPECT_EQ((*mode).refresh_hz, 120U);
    EXPECT_TRUE((*mode).sbs);

    control.get_result = Err<void>(Status{StatusCode::Timeout, "injected get"});
    EXPECT_EQ(control.Get().status().code(), StatusCode::Timeout);
    control.set_result = Err<void>(Status{StatusCode::Unsupported, "injected set"});
    EXPECT_EQ(control.Set(DisplayMode{72, false}).status().code(), StatusCode::Unsupported);
    EXPECT_EQ(control.state.refresh_hz, 120U); // A failed set leaves the state alone.
}

TEST(FakeDisplayControl, StopLatchesLikeTheAdapter) {
    FakeDisplayControl control;
    control.Stop();
    control.Stop();
    EXPECT_EQ(control.stop_calls, 2U);
    EXPECT_TRUE(control.stopped());
    EXPECT_EQ(control.Get().status().code(), StatusCode::NotReady);
    EXPECT_EQ(control.Set(DisplayMode{90, false}).status().code(), StatusCode::NotReady);
}

} // namespace
} // namespace cg::glasses::test
