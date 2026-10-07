// Recorder contract (S8 Task 3): the writer queue is bounded and counted, the
// callback path never blocks on disk, and a stalled disk can only cause drops.

#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <string>
#include <thread>

#include "cg/capture/cgrec.hpp"
#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/recorder.hpp"

#include <gtest/gtest.h>

namespace {

using cg::HostTime;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::FrameStorage;
using cg::capture::Recorder;
using cg::capture::RecorderConfig;
using cg::capture::RecorderStats;
using cg::capture::SessionInfo;
using cg::capture::ValidateSession;

class TempDir {
  public:
    TempDir() {
        static std::atomic<unsigned> counter{0};
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        path_ = std::filesystem::temp_directory_path() /
                ("cgrec-test-" + std::to_string(++counter) + "-" + std::to_string(stamp));
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

RecorderConfig ConfigFor(const std::filesystem::path &session, std::size_t capacity, FrameStorage storage) {
    RecorderConfig config;
    config.session_dir = session;
    config.queue_capacity = capacity;
    config.storage = storage;
    return config;
}

FakeStereoConfig FakeGeometry(int width, int height, int stride) {
    FakeStereoConfig config;
    config.width = width;
    config.height = height;
    config.stride = stride;
    return config;
}

TEST(RecorderContract, RecordsAFakeSessionAndWritesTheLayout) {
    TempDir temp;
    const auto session = temp.Path() / "session_1";
    FakeStereoSource source(FakeGeometry(32, 4, 32));
    // Capacity above the burst size: an unpaced 10-frame burst must not drop
    // (the drop path is the stalled-disk test below).
    Recorder recorder(ConfigFor(session, 16, FrameStorage::Pgm));
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(source.Start(&recorder).ok());
    for (int i = 0; i < 10; ++i) {
        source.EmitFrame();
    }
    source.Stop();
    recorder.Close();

    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_TRUE(info.ok()) << info.status().message();
    EXPECT_EQ((*info).frame_count, 10U);
    EXPECT_EQ((*info).dropped, 0U);
    EXPECT_EQ((*info).width, 32);
    EXPECT_EQ((*info).height, 4);
    EXPECT_EQ((*info).storage, FrameStorage::Pgm);
    EXPECT_TRUE(std::filesystem::exists(session / "manifest.json"));
    EXPECT_TRUE(std::filesystem::exists(session / "stereo.csv"));
    EXPECT_TRUE(std::filesystem::exists(session / "frames" / "00000001_l0.pgm"));
    EXPECT_TRUE(std::filesystem::exists(session / "frames" / "00000010_r1.pgm"));
    EXPECT_FALSE(std::filesystem::exists(session / "frames" / "00000011_l0.pgm"));

    const RecorderStats stats = recorder.Stats();
    EXPECT_EQ(stats.submitted, 10U);
    EXPECT_EQ(stats.written, 10U);
    EXPECT_EQ(stats.dropped, 0U);
}

TEST(RecorderContract, AStalledDiskNeverBlocksTheCallbackAndDropsAreCounted) {
    TempDir temp;
    const auto session = temp.Path() / "session_2";
    FakeStereoSource source(FakeGeometry(64, 8, 64));
    RecorderConfig config = ConfigFor(session, 4, FrameStorage::Pgm);
    config.writer_hook = [] { std::this_thread::sleep_for(std::chrono::milliseconds(25)); };
    Recorder recorder(config);
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(source.Start(&recorder).ok());

    std::chrono::steady_clock::duration worst{0};
    constexpr int kFrames = 60;
    for (int i = 0; i < kFrames; ++i) {
        const auto begin = std::chrono::steady_clock::now();
        source.EmitFrame();
        const auto elapsed = std::chrono::steady_clock::now() - begin;
        worst = elapsed > worst ? elapsed : worst;
    }
    const auto worst_ms = std::chrono::duration_cast<std::chrono::milliseconds>(worst).count();
    EXPECT_LT(worst_ms, 5) << "OnFrame must not wait for the stalled writer";

    source.Stop();
    recorder.Close();

    const RecorderStats stats = recorder.Stats();
    EXPECT_EQ(stats.submitted, static_cast<std::uint64_t>(kFrames));
    EXPECT_EQ(stats.written + stats.dropped, stats.submitted);
    EXPECT_GT(stats.dropped, 0U) << "a disk that sleeps 25 ms per frame must force drops";
    EXPECT_LE(stats.written, static_cast<std::uint64_t>(kFrames));

    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_TRUE(info.ok()) << info.status().message();
    EXPECT_EQ((*info).dropped, stats.dropped);
    EXPECT_EQ((*info).frame_count, stats.written);
}

TEST(RecorderContract, CloseIsIdempotentAndOpenIsIdempotent) {
    TempDir temp;
    const auto session = temp.Path() / "session_3";
    FakeStereoSource source(FakeGeometry(16, 2, 16));
    Recorder recorder(ConfigFor(session, 2, FrameStorage::Pgm));
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(recorder.Open().ok()) << "a repeated Open keeps the session";
    ASSERT_TRUE(source.Start(&recorder).ok());
    source.EmitFrame();
    source.Stop();
    recorder.Close();
    recorder.Close(); // second Close is a no-op
    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_TRUE(info.ok()) << info.status().message();
    EXPECT_EQ((*info).frame_count, 1U);
}

TEST(RecorderContract, CloseWithoutOpenIsSafeAndCloseIsTerminal) {
    TempDir temp;
    const auto session = temp.Path() / "session_4";
    FakeStereoSource source(FakeGeometry(16, 2, 16));
    Recorder recorder(ConfigFor(session, 2, FrameStorage::Pgm));
    recorder.Close(); // safe without Open
    EXPECT_FALSE(std::filesystem::exists(session)) << "Close without Open writes nothing";
    const cg::Result<void> reopened = recorder.Open();
    EXPECT_FALSE(reopened.ok()) << "Close is terminal: a recorder never reopens";
    EXPECT_EQ(reopened.status().code(), cg::StatusCode::NotReady);
}

} // namespace
