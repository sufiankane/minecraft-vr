#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <vector>

#include "cg/capture/fake_stereo_source.hpp"

namespace {

using cg::Duration;
using cg::HostTime;
using cg::IStereoFrameSink;
using cg::StereoFrame;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;

/// Deep-copies the delivered images so assertions can run after the callback
/// returned: the contract says the views are valid only during `OnFrame`, so a
/// test that keeps the source's pointers would be testing the wrong thing.
class RecordingSink final : public IStereoFrameSink {
  public:
    void OnFrame(const StereoFrame &frame) noexcept override {
        ++calls;
        seqs.push_back(frame.seq);
        times.push_back(frame.time);
        widths.push_back(frame.f0.width);
        heights.push_back(frame.f0.height);
        strides.push_back(frame.f0.stride);
        last_f0_left = frame.f0.left;
        last_f1_left = frame.f1.left;
        Copy(frame.f0.left, frame.f0.stride, frame.f0.height, f0_left);
        Copy(frame.f0.right, frame.f0.stride, frame.f0.height, f0_right);
        Copy(frame.f1.left, frame.f1.stride, frame.f1.height, f1_left);
    }

    int calls = 0;
    std::vector<std::uint64_t> seqs;
    std::vector<HostTime> times;
    std::vector<int> widths;
    std::vector<int> heights;
    std::vector<int> strides;
    std::vector<std::uint8_t> f0_left;
    std::vector<std::uint8_t> f0_right;
    std::vector<std::uint8_t> f1_left;
    const std::uint8_t *last_f0_left = nullptr;
    const std::uint8_t *last_f1_left = nullptr;

  private:
    static void Copy(const std::uint8_t *data, int stride, int height, std::vector<std::uint8_t> &out) noexcept {
        out.assign(data, data + static_cast<std::size_t>(stride) * static_cast<std::size_t>(height));
    }
};

TEST(FakeStereoSourceContract, StartIsIdempotentAndKeepsTheFirstSink) {
    FakeStereoSource source;
    RecordingSink first;
    RecordingSink second;
    ASSERT_TRUE(source.Start(&first).ok());
    ASSERT_TRUE(source.Start(&second).ok()) << "a repeated Start stays successful";
    source.EmitFrame();
    EXPECT_EQ(first.calls, 1);
    EXPECT_EQ(second.calls, 0) << "the first sink owns the stream";
    source.Stop();
}

TEST(FakeStereoSourceContract, StartRejectsANullSink) {
    FakeStereoSource source;
    const cg::Result<void> started = source.Start(nullptr);
    ASSERT_FALSE(started.ok());
    EXPECT_EQ(started.status().code(), cg::StatusCode::InvalidArgument);
}

TEST(FakeStereoSourceContract, EmitFrameDeliversTheConfiguredGeometryAndStride) {
    FakeStereoConfig config;
    config.width = 640;
    config.height = 480;
    config.stride = 648; // padded rows prove stride plumbing
    FakeStereoSource source(config);
    RecordingSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    source.EmitFrame();
    ASSERT_EQ(sink.calls, 1);
    EXPECT_EQ(sink.widths[0], 640);
    EXPECT_EQ(sink.heights[0], 480);
    EXPECT_EQ(sink.strides[0], 648);
    EXPECT_EQ(sink.f0_left.size(), static_cast<std::size_t>(648) * 480U);
    source.Stop();
}

TEST(FakeStereoSourceContract, SequenceIncreasesStrictlyAndTimeIsMonotonic) {
    FakeStereoConfig config;
    config.first_seq = 7;
    config.first_time = 5'000'000;
    config.time_step = Duration{2'000'000}; // 2 ms
    FakeStereoSource source(config);
    RecordingSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    for (int i = 0; i < 5; ++i) {
        source.EmitFrame();
    }
    ASSERT_EQ(sink.calls, 5);
    for (std::size_t i = 0; i < sink.seqs.size(); ++i) {
        EXPECT_EQ(sink.seqs[i], 7U + i);
        EXPECT_EQ(sink.times[i], 5'000'000 + static_cast<HostTime>(i) * 2'000'000);
    }
    source.Stop();
}

TEST(FakeStereoSourceContract, SyntheticPatternsDifferPerStreamAndFollowTheSequence) {
    FakeStereoConfig config;
    config.width = 16;
    config.height = 2;
    config.stride = 16;
    FakeStereoSource source(config);
    RecordingSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    source.EmitFrame(); // seq 1
    source.EmitFrame(); // seq 2
    ASSERT_EQ(sink.calls, 2);
    // f0_left of the second frame is (i + 2) & 0xFF.
    for (std::size_t i = 0; i < sink.f0_left.size(); ++i) {
        const auto expected = static_cast<std::uint8_t>((i + sink.seqs[1]) & 0xFFU);
        EXPECT_EQ(sink.f0_left[i], expected) << "byte " << i;
    }
    EXPECT_NE(sink.f0_left, sink.f0_right) << "the right stream carries a different bias";
    source.Stop();
}

TEST(FakeStereoSourceContract, DeliveredViewsAreTheSourceBuffersDuringTheCallback) {
    FakeStereoSource source;
    RecordingSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    source.EmitFrame();
    ASSERT_EQ(sink.calls, 1);
    EXPECT_EQ(sink.last_f0_left, source.Left0());
    EXPECT_EQ(sink.last_f1_left, source.Left1());
    source.Stop();
}

TEST(FakeStereoSourceContract, StopGuaranteesNoCallbackAfterReturn) {
    FakeStereoSource source;
    RecordingSink sink;
    ASSERT_TRUE(source.Start(&sink).ok());
    source.EmitFrame();
    ASSERT_EQ(sink.calls, 1);
    source.Stop();
    source.EmitFrame();
    source.EmitFrame();
    EXPECT_EQ(sink.calls, 1) << "no callback may follow Stop";
    EXPECT_EQ(source.EmittedAfterStop(), 2U) << "suppressed emissions are counted, not silent";
    EXPECT_FALSE(source.Started());
}

TEST(FakeStereoSourceContract, StopIsSafeBeforeStartAndTwice) {
    FakeStereoSource source;
    RecordingSink sink;
    source.Stop();
    ASSERT_TRUE(source.Start(&sink).ok());
    source.Stop();
    source.Stop();
    source.EmitFrame();
    EXPECT_EQ(sink.calls, 0);
    EXPECT_EQ(source.EmittedAfterStop(), 1U);
}

} // namespace
