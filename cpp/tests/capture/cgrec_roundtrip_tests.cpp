// `.cgrec` round trip (S8 Task 3): record a scripted fake session, validate it
// and compare every frame byte-for-byte and every CSV row exactly. The
// validator's negative cases get named reasons.

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "cg/capture/cgrec.hpp"
#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/recorder.hpp"

#include <gtest/gtest.h>
#include <nlohmann/json.hpp>

namespace {

using cg::HostTime;
using cg::capture::CgrecManifest;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::FrameStorage;
using cg::capture::ReadFrameImages;
using cg::capture::ReadStereoCsv;
using cg::capture::Recorder;
using cg::capture::RecorderConfig;
using cg::capture::SessionInfo;
using cg::capture::StereoCsvRow;
using cg::capture::ValidateSession;
using cg::capture::WriteManifest;

class TempDir {
  public:
    TempDir() {
        static std::atomic<unsigned> counter{0};
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        path_ = std::filesystem::temp_directory_path() /
                ("cgrec-rt-" + std::to_string(++counter) + "-" + std::to_string(stamp));
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

/// Deep-copies every delivered image so the recorded files can be compared
/// without knowing the fake's private pattern biases.
class CapturingSink final : public cg::IStereoFrameSink {
  public:
    void OnFrame(const cg::StereoFrame &frame) noexcept override {
        Frame copy;
        copy.seq = frame.seq;
        copy.time = frame.time;
        const std::vector<std::uint8_t> left0 = Pack(frame.f0);
        const std::vector<std::uint8_t> right0 = Pack(frame.f0.right, frame.f0);
        const std::vector<std::uint8_t> left1 = Pack(frame.f1);
        const std::vector<std::uint8_t> right1 = Pack(frame.f1.right, frame.f1);
        copy.packed.reserve(left0.size() * 4U);
        copy.packed.insert(copy.packed.end(), left0.begin(), left0.end());
        copy.packed.insert(copy.packed.end(), right0.begin(), right0.end());
        copy.packed.insert(copy.packed.end(), left1.begin(), left1.end());
        copy.packed.insert(copy.packed.end(), right1.begin(), right1.end());
        frames.push_back(std::move(copy));
    }

    struct Frame {
        std::uint64_t seq = 0;
        HostTime time = 0;
        std::vector<std::uint8_t> packed; // four images (padded stride preserved)
    };

    std::vector<Frame> frames;

  private:
    static std::vector<std::uint8_t> Pack(const cg::StereoImage &left) {
        return std::vector<std::uint8_t>(left.left, left.left + static_cast<std::size_t>(left.stride) *
                                                                    static_cast<std::size_t>(left.height));
    }

    static std::vector<std::uint8_t> Pack(const std::uint8_t *other, const cg::StereoImage &image) {
        return std::vector<std::uint8_t>(other, other + static_cast<std::size_t>(image.stride) *
                                                            static_cast<std::size_t>(image.height));
    }
};

RecorderConfig ConfigFor(const std::filesystem::path &session, FrameStorage storage) {
    RecorderConfig config;
    config.session_dir = session;
    config.queue_capacity = 16;
    config.storage = storage;
    return config;
}

TEST(CgrecRoundTrip, RecordedFakeSessionReplaysByteEqual) {
    TempDir temp;
    const auto session = temp.Path() / "session";
    FakeStereoConfig geometry;
    geometry.width = 24;
    geometry.height = 6;
    geometry.stride = 24;
    FakeStereoSource source(geometry);
    CapturingSink expected;
    Recorder recorder(ConfigFor(session, FrameStorage::Pgm));
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(source.Start(&recorder).ok());
    constexpr int kFrames = 12;
    for (int i = 0; i < kFrames; ++i) {
        source.EmitFrame();
    }
    source.Stop();
    recorder.Close();

    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_TRUE(info.ok()) << info.status().message();
    ASSERT_EQ((*info).frame_count, static_cast<std::uint64_t>(kFrames));

    // Second pass with a fresh source: the same scripted sequence (seq 1..N,
    // times 0..N-1 ms) into the capturer.
    FakeStereoSource replay(geometry);
    ASSERT_TRUE(replay.Start(&expected).ok());
    for (int i = 0; i < kFrames; ++i) {
        replay.EmitFrame();
    }
    replay.Stop();
    ASSERT_EQ(expected.frames.size(), static_cast<std::size_t>(kFrames));

    const cg::Result<std::vector<StereoCsvRow>> rows = ReadStereoCsv(session);
    ASSERT_TRUE(rows.ok()) << rows.status().message();
    ASSERT_EQ((*rows).size(), static_cast<std::size_t>(kFrames));
    for (int i = 0; i < kFrames; ++i) {
        const StereoCsvRow &row = (*rows).at(static_cast<std::size_t>(i));
        EXPECT_EQ(row.seq, expected.frames.at(static_cast<std::size_t>(i)).seq);
        EXPECT_EQ(row.time, expected.frames.at(static_cast<std::size_t>(i)).time);
        EXPECT_EQ(row.width, geometry.width);
        EXPECT_EQ(row.height, geometry.height);
    }

    for (int i = 0; i < kFrames; ++i) {
        const std::uint64_t seq = expected.frames.at(static_cast<std::size_t>(i)).seq;
        const cg::Result<std::vector<std::uint8_t>> frame = ReadFrameImages(session, *info, seq);
        ASSERT_TRUE(frame.ok()) << frame.status().message();
        ASSERT_EQ((*frame).size(), expected.frames.at(static_cast<std::size_t>(i)).packed.size());
        EXPECT_TRUE(*frame == expected.frames.at(static_cast<std::size_t>(i)).packed)
            << "frame " << seq << " differs from the delivered image";
    }
}

TEST(CgrecRoundTrip, PgmPacksRowsAndPackedBinPreservesPadding) {
    TempDir temp;
    for (const FrameStorage storage : {FrameStorage::Pgm, FrameStorage::PackedBin}) {
        const auto session = temp.Path() / (storage == FrameStorage::Pgm ? "pgm" : "bin");
        FakeStereoConfig geometry;
        geometry.width = 20;
        geometry.height = 3;
        geometry.stride = 24; // four bytes of padding per row
        FakeStereoSource source(geometry);
        CapturingSink expected;
        Recorder recorder(ConfigFor(session, storage));
        ASSERT_TRUE(recorder.Open().ok());
        ASSERT_TRUE(source.Start(&recorder).ok());
        for (int i = 0; i < 3; ++i) {
            source.EmitFrame();
        }
        source.Stop();
        recorder.Close();

        const cg::Result<SessionInfo> info = ValidateSession(session);
        ASSERT_TRUE(info.ok()) << info.status().message();
        FakeStereoSource replay(geometry);
        ASSERT_TRUE(replay.Start(&expected).ok());
        for (int i = 0; i < 3; ++i) {
            replay.EmitFrame();
        }
        replay.Stop();

        const cg::Result<std::vector<std::uint8_t>> frame = ReadFrameImages(session, *info, 1);
        ASSERT_TRUE(frame.ok()) << frame.status().message();
        const std::vector<std::uint8_t> &delivered = expected.frames.at(0).packed;
        if (storage == FrameStorage::PackedBin) {
            EXPECT_EQ((*frame).size(), delivered.size());
            EXPECT_TRUE(*frame == delivered) << "packed bin must preserve row padding";
        } else {
            EXPECT_EQ((*frame).size(), static_cast<std::size_t>(20) * 3U * 4U);
            for (int image = 0; image < 4; ++image) {
                for (int row = 0; row < 3; ++row) {
                    for (int x = 0; x < 20; ++x) {
                        const std::size_t packed_at = static_cast<std::size_t>(image) * 20U * 3U +
                                                      static_cast<std::size_t>(row) * 20U + static_cast<std::size_t>(x);
                        const std::size_t padded_at = static_cast<std::size_t>(image) * 24U * 3U +
                                                      static_cast<std::size_t>(row) * 24U + static_cast<std::size_t>(x);
                        EXPECT_EQ((*frame).at(packed_at), delivered.at(padded_at));
                    }
                }
            }
        }
    }
}

TEST(CgrecValidator, MissingManifestIsNamed) {
    TempDir temp;
    const cg::Result<SessionInfo> info = ValidateSession(temp.Path());
    ASSERT_FALSE(info.ok());
    EXPECT_NE(std::string(info.status().message()).find("manifest"), std::string::npos);
}

TEST(CgrecValidator, UnknownSchemaIsNamed) {
    TempDir temp;
    const auto session = temp.Path() / "session";
    std::filesystem::create_directories(session / "frames");
    nlohmann::json manifest;
    manifest["schema"] = 99;
    manifest["width"] = 8;
    manifest["height"] = 2;
    manifest["stride"] = 8;
    manifest["frame_count"] = 0;
    manifest["dropped"] = 0;
    manifest["storage"] = "pgm";
    std::ofstream(session / "manifest.json") << manifest.dump();
    std::ofstream(session / "stereo.csv") << "seq,host_time_ns,sdk_time_s,width,height\n";
    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_FALSE(info.ok());
    EXPECT_NE(std::string(info.status().message()).find("schema"), std::string::npos);
}

TEST(CgrecValidator, MissingFrameFileIsNamed) {
    TempDir temp;
    const auto session = temp.Path() / "session";
    FakeStereoConfig geometry;
    geometry.width = 8;
    geometry.height = 2;
    geometry.stride = 8;
    FakeStereoSource source(geometry);
    Recorder recorder(ConfigFor(session, FrameStorage::Pgm));
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(source.Start(&recorder).ok());
    source.EmitFrame();
    source.EmitFrame();
    source.Stop();
    recorder.Close();

    std::filesystem::remove(session / "frames" / "00000002_l1.pgm");
    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_FALSE(info.ok());
    EXPECT_NE(std::string(info.status().message()).find("frame"), std::string::npos);
}

TEST(CgrecValidator, ManifestFrameCountMismatchIsNamed) {
    TempDir temp;
    const auto session = temp.Path() / "session";
    FakeStereoConfig geometry;
    geometry.width = 8;
    geometry.height = 2;
    geometry.stride = 8;
    FakeStereoSource source(geometry);
    Recorder recorder(ConfigFor(session, FrameStorage::Pgm));
    ASSERT_TRUE(recorder.Open().ok());
    ASSERT_TRUE(source.Start(&recorder).ok());
    source.EmitFrame();
    source.Stop();
    recorder.Close();

    CgrecManifest manifest;
    manifest.width = 8;
    manifest.height = 2;
    manifest.stride = 8;
    manifest.frame_count = 5; // lie
    ASSERT_TRUE(WriteManifest(session, manifest).ok());
    const cg::Result<SessionInfo> info = ValidateSession(session);
    ASSERT_FALSE(info.ok());
    EXPECT_NE(std::string(info.status().message()).find("count"), std::string::npos);
}

} // namespace
