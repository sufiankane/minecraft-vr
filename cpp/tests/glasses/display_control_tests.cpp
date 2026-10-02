#include <cstdint>
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
    VitureDisplayControl control(api, [&running] { return running; });

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
    VitureDisplayControl control(api, [&source] { return source.Running(); });

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
