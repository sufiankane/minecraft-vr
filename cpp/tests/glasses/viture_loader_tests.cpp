#include <atomic>
#include <chrono>
#include <memory>
#include <string>
#include <thread>

#include <gtest/gtest.h>

#include "cg/glasses/viture_loader.hpp"
#include "fake_viture_api.hpp"

namespace cg::glasses {
namespace {

using test::FakeVitureApi;

// A name that no build or install tree provides, so LoadLibraryW/dlopen must
// fail without any vendor DLL present.
constexpr const char *kMissingDll = "cg_no_such_viture_sdk_2a.dll";

TEST(VitureLoader, MissingLibraryIsUnsupportedAndNamesThePath) {
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(kMissingDll);

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    EXPECT_NE(std::string(result.status().message()).find(kMissingDll), std::string::npos)
        << "message was: " << result.status().message();
}

TEST(VitureLoader, EmptyPathIsInvalidArgument) {
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi("");

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
}

/// The seam tests below compile and pin the programmable fake the Task 2b
/// Viture source drives; the loader itself is tested on its error paths only.
constexpr cg_head_sample Sample(std::uint32_t sequence, std::int64_t host_time) noexcept {
    return cg_head_sample{host_time, cg_pose{cg_vec3{0.0F, 0.0F, 0.0F}, cg_quat{1.0F, 0.0F, 0.0F, 0.0F}},
                          CG_TRACK_STABLE, sequence};
}

TEST(FakeVitureApi, FeedsSamplesVerbatimAndCountsCalls) {
    FakeVitureApi api;
    api.samples = {Sample(1, 100), Sample(2, 200)};

    EXPECT_TRUE(api.CreateDevice().ok());
    EXPECT_TRUE(api.StartPose().ok());

    const Result<cg_head_sample> first = api.PollPose();
    ASSERT_TRUE(first.ok());
    EXPECT_EQ((*first).sequence, 1U);
    EXPECT_EQ((*first).host_time, 100);
    const Result<cg_head_sample> second = api.PollPose();
    ASSERT_TRUE(second.ok());
    EXPECT_EQ((*second).sequence, 2U);
    const Result<cg_head_sample> exhausted = api.PollPose();
    ASSERT_FALSE(exhausted.ok());
    EXPECT_EQ(exhausted.status().code(), StatusCode::Timeout);

    api.DestroyDevice();
    EXPECT_EQ(api.create_calls.load(), 1U);
    EXPECT_EQ(api.poll_calls.load(), 3U);
    EXPECT_EQ(api.destroy_calls.load(), 1U);
}

TEST(FakeVitureApi, ScriptsResultsAndRecordsDisplayAndRecentreArguments) {
    FakeVitureApi api;
    api.create_script.push_back(Err<void>(Status{StatusCode::Device, "no device"}));
    EXPECT_EQ(api.CreateDevice().status().code(), StatusCode::Device);
    EXPECT_TRUE(api.CreateDevice().ok());

    const float kPose[7] = {1.0F, 2.0F, 3.0F, 1.0F, 0.0F, 0.0F, 0.0F};
    EXPECT_TRUE(api.ResetOriginCarina(kPose).ok());
    EXPECT_EQ(api.reset_origin_calls.load(), 1U);
    EXPECT_TRUE(api.has_reset_pose);
    EXPECT_FLOAT_EQ(api.last_reset_pose[2], 3.0F);

    EXPECT_TRUE(api.SetDisplayMode(90, true).ok());
    EXPECT_EQ(api.last_refresh_hz, 90U);
    EXPECT_TRUE(api.last_sbs);
    const Result<std::uint32_t> refresh_hz = api.GetRefreshHz();
    ASSERT_TRUE(refresh_hz.ok());
    EXPECT_EQ(*refresh_hz, 90U);
    EXPECT_EQ(api.SdkVersion(), "fake-viture-0.0.0");
}

TEST(FakeVitureApi, ScriptFeedPublishesTask1PatternSamples) {
    FakeVitureApi api;
    ASSERT_TRUE(api.FeedScript(FakeScript::Static(), 100.0).ok());
    ASSERT_NE(api.script_clock(), nullptr);
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());

    const Result<cg_head_sample> first = api.PollPose();
    ASSERT_TRUE(first.ok());
    EXPECT_EQ((*first).sequence, 1U);
    EXPECT_EQ((*first).host_time, 0);
    EXPECT_EQ((*first).state, CG_TRACK_STABLE);
}

TEST(FakeVitureApi, LongPollReturnsPromptlyAfterRequestStop) {
    FakeVitureApi api;
    api.poll_delay_ns = 5'000'000'000; // 5 s; the test must not wait it out.

    std::atomic<bool> done{false};
    StatusCode code = StatusCode::Ok;
    std::thread poller([&api, &done, &code] {
        code = api.PollPose().status().code();
        done.store(true, std::memory_order_release);
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    api.RequestStop();
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!done.load(std::memory_order_acquire) && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    const bool finished = done.load(std::memory_order_acquire);
    poller.join();

    EXPECT_TRUE(finished) << "the blocked poll ignored RequestStop";
    EXPECT_EQ(code, StatusCode::Timeout);
    EXPECT_TRUE(api.stop_requested());
    EXPECT_EQ(api.poll_calls.load(), 1U);
}

} // namespace
} // namespace cg::glasses
