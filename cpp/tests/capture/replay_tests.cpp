// Replay source (S8 Task 4): a recorded `.cgrec` session plays back into a
// sink fast or on its recorded timeline, and an invalid session fails Start
// with the validator's named reason.

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <thread>
#include <vector>

#include "cg/capture/cgrec.hpp"
#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/recorder.hpp"
#include "cg/capture/replay_stereo_source.hpp"

#include <gtest/gtest.h>

namespace {

using cg::HostTime;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::IFrameClock;
using cg::capture::ReadFrameImages;
using cg::capture::Recorder;
using cg::capture::RecorderConfig;
using cg::capture::ReplayMode;
using cg::capture::ReplayStereoSource;
using cg::capture::SessionInfo;
using cg::capture::ValidateSession;

class TempDir {
  public:
    TempDir() {
        static std::atomic<unsigned> counter{0};
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        path_ = std::filesystem::temp_directory_path() /
                ("cgrec-replay-" + std::to_string(++counter) + "-" + std::to_string(stamp));
        std::filesystem::create_directories(path_);
    }

    ~TempDir() {
        std::error_code error;
        std::filesystem::remove_all(path_, error);
    }

    TempDir(const TempDir &) = delete;
    TempDir &operator=(const TempDir &) = delete;

    [[nodiscard]] const std::filesystem::path &Path() const { return path_; }

  private:
    std::filesystem::path path_;
};

class ManualClock final : public IFrameClock {
  public:
    [[nodiscard]] HostTime Now() const noexcept override { return now_.load(std::memory_order_acquire); }

    void Advance(HostTime delta) noexcept { now_.fetch_add(delta, std::memory_order_release); }

  private:
    std::atomic<HostTime> now_{0};
};

class RecordingSink final : public cg::IStereoFrameSink {
  public:
    void OnFrame(const cg::StereoFrame &frame) noexcept override {
        ++calls;
        seqs.push_back(frame.seq);
        times.push_back(frame.time);
        clock_values.push_back(clock == nullptr ? 0 : clock->Now());
        const std::size_t bytes = static_cast<std::size_t>(frame.f0.stride) * static_cast<std::size_t>(frame.f0.height);
        left0.assign(frame.f0.left, frame.f0.left + bytes); // the last frame's first image
    }

    IFrameClock *clock = nullptr;
    int calls = 0;
    std::vector<std::uint64_t> seqs;
    std::vector<HostTime> times;
    std::vector<HostTime> clock_values;
    std::vector<std::uint8_t> left0;
};

/// Records a scripted fake session with `frames` frames and `step_ns` spacing.
std::filesystem::path RecordSession(const std::filesystem::path &session, int width, int height, int stride, int frames,
                                    HostTime step_ns) {
    FakeStereoConfig geometry;
    geometry.width = width;
    geometry.height = height;
    geometry.stride = stride;
    geometry.time_step = cg::Duration{step_ns};
    FakeStereoSource source(geometry);
    RecorderConfig config;
    config.session_dir = session;
    config.queue_capacity = 16;
    Recorder recorder(config);
    if (!recorder.Open().ok()) {
        return {};
    }
    if (!source.Start(&recorder).ok()) {
        return {};
    }
    for (int i = 0; i < frames; ++i) {
        source.EmitFrame();
    }
    source.Stop();
    recorder.Close();
    return session;
}

TEST(ReplaySource, FastReplayDeliversEveryRecordedFrameByteEqual) {
    TempDir temp;
    const auto session = RecordSession(temp.Path() / "session", 20, 4, 20, 6, 100'000'000);
    ASSERT_FALSE(session.empty());
    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_TRUE(info.ok()) << info.status().message();

    cg::capture::SteadyFrameClock clock;
    ReplayStereoSource replay(session, ReplayMode::Fast, clock);
    RecordingSink sink;
    ASSERT_TRUE(replay.Start(&sink).ok());
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!replay.Finished() && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    ASSERT_TRUE(replay.Finished()) << "fast replay must finish on its own";
    replay.Stop();

    ASSERT_EQ(sink.calls, 6);
    for (int i = 0; i < 6; ++i) {
        EXPECT_EQ(sink.seqs.at(static_cast<std::size_t>(i)), static_cast<std::uint64_t>(i) + 1U);
        EXPECT_EQ(sink.times.at(static_cast<std::size_t>(i)), static_cast<HostTime>(i) * 100'000'000);
    }
    // The last delivered frame equals the recorded frame file.
    const cg::Result<std::vector<std::uint8_t>> recorded = ReadFrameImages(session, *info, 6);
    ASSERT_TRUE(recorded.ok()) << recorded.status().message();
    ASSERT_EQ((*recorded).size(), static_cast<std::size_t>(20) * 4U * 4U) << "four images per frame";
    EXPECT_EQ(sink.left0.size(), static_cast<std::size_t>(20) * 4U);
    const std::vector<std::uint8_t> expected_left0((*recorded).begin(), (*recorded).begin() + 20 * 4);
    EXPECT_TRUE(sink.left0 == expected_left0);
}

TEST(ReplaySource, RealTimeHonoursTheRecordedTimeline) {
    TempDir temp;
    const auto session = RecordSession(temp.Path() / "session", 16, 2, 16, 3, 100'000'000);
    ASSERT_FALSE(session.empty());

    ManualClock clock;
    ReplayStereoSource replay(session, ReplayMode::RealTime, clock);
    RecordingSink sink;
    sink.clock = &clock;
    ASSERT_TRUE(replay.Start(&sink).ok());

    // Frame 1 targets the start instant; let the worker deliver it.
    const auto wait_for = [&sink](int calls) {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
        while (sink.calls < calls && std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
    };
    wait_for(1);
    ASSERT_EQ(sink.calls, 1) << "the first frame plays at the start instant";
    EXPECT_EQ(sink.clock_values.at(0), 0);

    clock.Advance(90'000'000); // 90 ms: still before frame 2's 100 ms target
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    EXPECT_EQ(sink.calls, 1) << "a frame must never play early";

    clock.Advance(20'000'000); // 110 ms: frame 2 plays, frame 3 (200 ms) does not
    wait_for(2);
    ASSERT_EQ(sink.calls, 2);
    EXPECT_GE(sink.clock_values.at(1), 100'000'000);
    EXPECT_LT(sink.clock_values.at(1), 200'000'000);

    clock.Advance(100'000'000); // 210 ms: frame 3 plays
    wait_for(3);
    ASSERT_EQ(sink.calls, 3);
    EXPECT_GE(sink.clock_values.at(2), 200'000'000);
    replay.Stop();
}

TEST(ReplaySource, StopInterruptsAWaitingReplayAndForbidsCallbacksAfterReturn) {
    TempDir temp;
    const auto session = RecordSession(temp.Path() / "session", 16, 2, 16, 5, 1'000'000'000);
    ASSERT_FALSE(session.empty());

    ManualClock clock; // never advances: the replay waits after frame 1
    ReplayStereoSource replay(session, ReplayMode::RealTime, clock);
    RecordingSink sink;
    ASSERT_TRUE(replay.Start(&sink).ok());
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (sink.calls < 1 && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    ASSERT_EQ(sink.calls, 1);
    replay.Stop();
    const int after_stop = sink.calls;
    clock.Advance(10'000'000'000);
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    EXPECT_EQ(sink.calls, after_stop) << "no callback may follow Stop";
}

TEST(ReplaySource, InvalidSessionFailsStartWithTheValidatorReason) {
    TempDir temp;
    cg::capture::SteadyFrameClock clock;
    ReplayStereoSource replay(temp.Path() / "missing", ReplayMode::Fast, clock);
    RecordingSink sink;
    const cg::Result<void> started = replay.Start(&sink);
    ASSERT_FALSE(started.ok());
    EXPECT_NE(std::string(started.status().message()).find("manifest"), std::string::npos);
    EXPECT_EQ(sink.calls, 0);
}

TEST(ReplaySource, StopBeforeStartAndDoubleStopAreSafe) {
    TempDir temp;
    cg::capture::SteadyFrameClock clock;
    ReplayStereoSource replay(temp.Path() / "missing", ReplayMode::Fast, clock);
    replay.Stop();
    replay.Stop();
}

} // namespace
