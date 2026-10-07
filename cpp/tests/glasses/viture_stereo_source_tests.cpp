// VitureStereoSource (S8 Task 5): the source registers a frame sink on a live
// device, frames carry the SDK timeline and a per-session sequence, the stride
// is configuration, and Stop clears the sink so no frame follows.

#include <array>
#include <cstddef>
#include <cstdint>
#include <vector>

#include "cg/glasses/viture_stereo_source.hpp"

#include <gtest/gtest.h>

#include "fake_viture_api.hpp"

namespace {

using cg::HostTime;
using cg::IStereoFrameSink;
using cg::StereoFrame;
using cg::glasses::MakeVendorFrame;
using cg::glasses::VendorFrameArgs;
using cg::glasses::VitureStereoConfig;
using cg::glasses::VitureStereoSource;
using cg::glasses::test::FakeVitureApi;

class FrameSink final : public IStereoFrameSink {
  public:
    void OnFrame(const StereoFrame &frame) noexcept override {
        ++calls;
        last = frame;
        const std::size_t bytes = static_cast<std::size_t>(frame.f0.stride) * static_cast<std::size_t>(frame.f0.height);
        left0.assign(frame.f0.left, frame.f0.left + bytes);
    }

    int calls = 0;
    StereoFrame last{};
    std::vector<std::uint8_t> left0;
};

/// Four small buffers with distinct byte patterns per corner.
struct Buffers {
    Buffers() {
        for (std::size_t i = 0; i < left0.size(); ++i) {
            left0.at(i) = static_cast<char>(i);
            right0.at(i) = static_cast<char>(0x10 + i);
            left1.at(i) = static_cast<char>(0x20 + i);
            right1.at(i) = static_cast<char>(0x30 + i);
        }
    }

    std::array<char, 16> left0{};
    std::array<char, 16> right0{};
    std::array<char, 16> left1{};
    std::array<char, 16> right1{};
};

TEST(VitureStereoSourceTests, StartRequiresALiveDevice) {
    FakeVitureApi api;
    VitureStereoSource source(api);
    FrameSink sink;
    const cg::Result<void> started = source.Start(&sink);
    ASSERT_FALSE(started.ok());
    EXPECT_EQ(started.status().code(), cg::StatusCode::NotReady);
    EXPECT_EQ(api.set_frame_sink_calls.load(), 1U);
}

TEST(VitureStereoSourceTests, StartRejectsANullSink) {
    FakeVitureApi api;
    ASSERT_TRUE(api.CreateDevice().ok());
    VitureStereoSource source(api);
    const cg::Result<void> started = source.Start(nullptr);
    ASSERT_FALSE(started.ok());
    EXPECT_EQ(started.status().code(), cg::StatusCode::InvalidArgument);
}

TEST(VitureStereoSourceTests, FramesCarryTheSdkTimelineAndSequence) {
    FakeVitureApi api;
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());
    VitureStereoSource source(api);
    FrameSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    Buffers buffers;

    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 1.25, 4,
                     4);
    ASSERT_EQ(sink.calls, 1);
    EXPECT_EQ(sink.last.seq, 1U);
    EXPECT_EQ(sink.last.time, static_cast<HostTime>(1'250'000'000));
    EXPECT_EQ(sink.last.f0.width, 4);
    EXPECT_EQ(sink.last.f0.height, 4);
    EXPECT_EQ(sink.last.f0.stride, 4) << "packed rows by default (U-03 configuration)";
    ASSERT_EQ(sink.left0.size(), 16U);
    EXPECT_EQ(sink.left0.at(3), 3U);
    EXPECT_EQ(sink.last.f0.left[0], 0);
    EXPECT_EQ(sink.last.f0.right[0], 0x10);

    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 1.30, 4,
                     4);
    ASSERT_EQ(sink.calls, 2);
    EXPECT_EQ(sink.last.seq, 2U);
    EXPECT_EQ(sink.last.time, static_cast<HostTime>(1'300'000'000));
    source.Stop();
}

TEST(VitureStereoSourceTests, StrideConfigurationOverridesPackedRows) {
    FakeVitureApi api;
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());
    VitureStereoConfig config;
    config.stride = 8;
    VitureStereoSource source(api, config);
    FrameSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    Buffers buffers;
    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 2.0, 4,
                     4);
    ASSERT_EQ(sink.calls, 1);
    EXPECT_EQ(sink.last.f0.stride, 8);
    EXPECT_EQ(api.frame_stride, 8);
    source.Stop();
}

TEST(VitureStereoSourceTests, StopClearsTheSinkAndNoFrameFollows) {
    FakeVitureApi api;
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());
    VitureStereoSource source(api);
    FrameSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    Buffers buffers;
    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 1.0, 4,
                     4);
    ASSERT_EQ(sink.calls, 1);

    source.Stop();
    EXPECT_EQ(api.set_frame_sink_calls.load(), 2U) << "Stop clears the sink on the seam";
    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 2.0, 4,
                     4);
    EXPECT_EQ(sink.calls, 1) << "no frame may follow Stop";

    // The same source can reattach after a Stop (fresh sequence from the seam).
    ASSERT_TRUE(source.Start(&sink).ok());
    api.DeliverFrame(buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 3.0, 4,
                     4);
    EXPECT_EQ(sink.calls, 2);
    source.Stop();
}

TEST(VitureStereoSourceTests, StopBeforeStartAndDoubleStopAreSafe) {
    FakeVitureApi api;
    VitureStereoSource source(api);
    source.Stop();
    source.Stop();
    EXPECT_EQ(api.set_frame_sink_calls.load(), 0U) << "a never-started source never touches the seam";
}

TEST(VitureStereoSourceTests, StopAfterTheDeviceDiedDoesNotThrow) {
    FakeVitureApi api;
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());
    VitureStereoSource source(api);
    FrameSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    api.DestroyDevice();
    source.Stop(); // clearing reports NotReady; Stop ignores it
}

TEST(VitureVendorFrameMapping, EncodesTheSdkTimestampAndStride) {
    Buffers buffers;
    const VendorFrameArgs args{
        buffers.left0.data(), buffers.right0.data(), buffers.left1.data(), buffers.right1.data(), 2.5, 4, 4};
    const StereoFrame frame = MakeVendorFrame(args, 0, 7);
    EXPECT_EQ(frame.time, static_cast<HostTime>(2'500'000'000));
    EXPECT_EQ(frame.seq, 7U);
    EXPECT_EQ(frame.f0.width, 4);
    EXPECT_EQ(frame.f0.height, 4);
    EXPECT_EQ(frame.f0.stride, 4) << "stride 0 means packed rows";
    EXPECT_EQ(frame.f1.stride, 4);
    EXPECT_EQ(frame.f0.left[0], 0);
    EXPECT_EQ(frame.f0.right[0], 0x10);
    EXPECT_EQ(frame.f1.left[0], 0x20);
    EXPECT_EQ(frame.f1.right[0], 0x30);

    const StereoFrame padded = MakeVendorFrame(args, 8, 8);
    EXPECT_EQ(padded.f0.stride, 8);
    EXPECT_EQ(padded.f1.stride, 8);
}

} // namespace
