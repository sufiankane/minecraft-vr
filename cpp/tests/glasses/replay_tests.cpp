#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>

#include <gtest/gtest.h>

#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"
#include "cg/glasses/manual_clock.hpp"
#include "cg/glasses/replay_head_pose_source.hpp"

namespace cg::glasses {
namespace {

using core_math::Pose;
using core_math::Quat;
using core_math::Vec3;

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadiansToDegrees = 180.0 / kPi;

/// The committed fixture: ~200 rows at ~90 Hz, host time starting at 1 s.
/// Layout (data row index, zero-based): rows 0..69 stable, 70..89 unstable,
/// 90..119 stable, 120..142 lost (the ~250 ms dropout window), 143..199
/// stable. Yaw sweeps 0.5 degrees per row and pitch is a constant 3 degrees;
/// lost rows hold the pose of row 119. All positions are zero.
constexpr std::uint64_t kReplayRows = 200;
constexpr std::uint64_t kUnstableFirst = 70;
constexpr std::uint64_t kUnstableCount = 20;
constexpr std::uint64_t kLostFirst = 120;
constexpr std::uint64_t kLostCount = 23;
constexpr std::int64_t kReplayEpochNs = 1'000'000'000;

constexpr std::string_view kHeader = "host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status\n";

HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, Pose{Vec3{0.0, 0.0, 0.0}, Quat::kIdentity}, TrackState::Stable, 0};
}

core_math::Vec3 Forward(const Pose &pose) noexcept { return pose.rotation.Rotate(Vec3{0.0, 0.0, -1.0}); }

double YawDegrees(const Pose &pose) noexcept {
    const Vec3 forward = Forward(pose);
    return std::atan2(-forward.x, -forward.z) * kRadiansToDegrees;
}

double PitchDegrees(const Pose &pose) noexcept {
    const Vec3 forward = Forward(pose);
    return std::asin(std::clamp(forward.y, -1.0, 1.0)) * kRadiansToDegrees;
}

/// Roll of a `yaw * pitch * roll` rotation: `atan2(R(1,0), R(1,1))`, where row
/// 1 of the rotation matrix is the world-Y component of the rotated X/Y axes.
double RollDegrees(const Pose &pose) noexcept {
    const Vec3 x_axis = pose.rotation.Rotate(Vec3{1.0, 0.0, 0.0});
    const Vec3 y_axis = pose.rotation.Rotate(Vec3{0.0, 1.0, 0.0});
    return std::atan2(x_axis.y, y_axis.y) * kRadiansToDegrees;
}

std::int64_t ExpectedHostTimeNs(std::uint64_t index) {
    return kReplayEpochNs +
           static_cast<std::int64_t>(std::llround(static_cast<double>(index) * 1'000'000'000.0 / 90.0));
}

TrackState ExpectedState(std::uint64_t index) {
    if (index >= kLostFirst && index < kLostFirst + kLostCount) {
        return TrackState::Lost;
    }
    if (index >= kUnstableFirst && index < kUnstableFirst + kUnstableCount) {
        return TrackState::Unstable;
    }
    return TrackState::Stable;
}

double ExpectedYawDegrees(std::uint64_t index) {
    if (index >= kLostFirst && index < kLostFirst + kLostCount) {
        index = kLostFirst - 1;
    }
    return 0.5 * static_cast<double>(index);
}

/// Writes `contents` to a uniquely named temp CSV and removes it on scope exit.
struct TempCsv {
    std::filesystem::path path;

    TempCsv(std::string_view name, std::string contents) {
        path = std::filesystem::temp_directory_path() / ("cg_replay_" + std::string(name) + ".csv");
        std::ofstream out(path, std::ios::binary | std::ios::trunc);
        out << contents;
    }

    ~TempCsv() {
        std::error_code ignored;
        std::filesystem::remove(path, ignored);
    }

    TempCsv(const TempCsv &) = delete;
    TempCsv &operator=(const TempCsv &) = delete;
};

void ExpectLoadInvalid(const std::filesystem::path &path, std::string_view row_needle, int test_line) {
    ManualClock clock;
    ReplayHeadPoseSource source(path, clock);
    const Result<void> result = source.Load();
    ASSERT_FALSE(result.ok()) << "test line " << test_line << " must be rejected";
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument) << "test line " << test_line;
    EXPECT_NE(std::string(result.status().message()).find(row_needle), std::string::npos)
        << "test line " << test_line << " message was: " << result.status().message();
}

void ExpectLoadOk(ReplayHeadPoseSource &source) {
    const Result<void> result = source.Load();
    ASSERT_TRUE(result.ok()) << result.status().message();
}

void PublishNext(ReplayHeadPoseSource &source, std::uint32_t count) {
    for (std::uint32_t i = 0; i < count; ++i) {
        source.PublishNext();
    }
}

TEST(ReplaySource, LoadRejectsAMalformedHeader) {
    const TempCsv csv("bad_header", "host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,state\n");
    ExpectLoadInvalid(csv.path, "row 1", __LINE__);
}

TEST(ReplaySource, LoadRejectsAWrongColumnCount) {
    const TempCsv csv("bad_columns", std::string(kHeader) + "0,0.0,0,0,0,1,0,0,0\n");
    ExpectLoadInvalid(csv.path, "row 2", __LINE__);
}

TEST(ReplaySource, LoadRejectsANonNumericField) {
    const TempCsv csv("non_numeric", std::string(kHeader) + "abc,0.0,0,0,0,1,0,0,0,stable\n");
    ExpectLoadInvalid(csv.path, "row 2", __LINE__);
}

TEST(ReplaySource, LoadRejectsAnUnknownStatus) {
    const TempCsv csv("bad_status", std::string(kHeader) + "0,0.0,0,0,0,1,0,0,0,stablee\n");
    ExpectLoadInvalid(csv.path, "row 2", __LINE__);
}

TEST(ReplaySource, LoadRejectsANonFinitePosition) {
    const TempCsv csv("inf_position", std::string(kHeader) + "0,0.0,inf,0,0,1,0,0,0,stable\n");
    ExpectLoadInvalid(csv.path, "row 2", __LINE__);
}

TEST(ReplaySource, LoadRejectsANonFiniteQuaternionOnALaterRow) {
    const TempCsv csv("nan_quaternion",
                      std::string(kHeader) + "0,0.0,0,0,0,1,0,0,0,stable\n100,0.0,0,0,0,nan,0,0,0,stable\n");
    ExpectLoadInvalid(csv.path, "row 3", __LINE__);
}

TEST(ReplaySource, LoadRejectsARegressingHostTime) {
    const TempCsv csv("regressing_time",
                      std::string(kHeader) + "100,0.0,0,0,0,1,0,0,0,stable\n50,0.0,0,0,0,1,0,0,0,stable\n");
    ExpectLoadInvalid(csv.path, "row 3", __LINE__);
}

TEST(ReplaySource, LoadRejectsAMissingFile) {
    const std::filesystem::path path = std::filesystem::temp_directory_path() / "cg_replay_does_not_exist_3a.csv";
    std::error_code ignored;
    std::filesystem::remove(path, ignored);

    ManualClock clock;
    ReplayHeadPoseSource source(path, clock);
    const Result<void> result = source.Load();
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
    EXPECT_NE(std::string(result.status().message()).find(path.filename().string()), std::string::npos)
        << "message was: " << result.status().message();
}

TEST(ReplaySource, LoadAcceptsNormalisesAndPublishesAWellFormedRow) {
    const TempCsv csv("well_formed", std::string(kHeader) + "5000000000,12.5,1.5,-2.5,3.5,2,0,0,0,UNSTABLE\n");
    ManualClock clock;
    ReplayHeadPoseSource source(csv.path, clock);
    ExpectLoadOk(source);
    ASSERT_EQ(source.row_count(), 1U);
    ASSERT_EQ(source.next_row_index(), 0U);

    ASSERT_TRUE(source.Start().ok());
    source.PublishNext();
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.seq, 1U);
    EXPECT_EQ(sample.time, 5'000'000'000);
    EXPECT_EQ(sample.state, TrackState::Unstable);
    EXPECT_DOUBLE_EQ(sample.pose.position.x, 1.5);
    EXPECT_DOUBLE_EQ(sample.pose.position.y, -2.5);
    EXPECT_DOUBLE_EQ(sample.pose.position.z, 3.5);
    // A non-unit quaternion is normalised; (2, 0, 0, 0) becomes identity.
    EXPECT_DOUBLE_EQ(sample.pose.rotation.w(), 1.0);
    EXPECT_DOUBLE_EQ(sample.pose.rotation.x(), 0.0);
    EXPECT_TRUE(sample.pose.rotation.IsNormalized(1e-9));
    EXPECT_EQ(clock.Now(), 5'000'000'000);
}

TEST(ReplaySource, StartBeforeLoadIsNotReady) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    const Result<void> started = source.Start();
    ASSERT_FALSE(started.ok());
    EXPECT_EQ(started.status().code(), StatusCode::NotReady);
}

TEST(ReplaySource, AHeaderOnlyDatasetPublishesNothing) {
    const TempCsv csv("empty", std::string(kHeader));
    ManualClock clock;
    ReplayHeadPoseSource source(csv.path, clock);
    ExpectLoadOk(source);
    EXPECT_EQ(source.row_count(), 0U);
    ASSERT_TRUE(source.Start().ok());
    source.PublishNext();
    HeadSample sample = PlaceholderSample();
    EXPECT_FALSE(source.TryGetLatest(sample, Duration{0}));
}

TEST(ReplaySource, PlaybackPreservesOrderCountStatesAndTiming) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_EQ(source.row_count(), static_cast<std::size_t>(kReplayRows));
    ASSERT_EQ(source.next_row_index(), 0U);
    ASSERT_TRUE(source.Start().ok());

    std::uint64_t unstable_seen = 0;
    std::uint64_t lost_seen = 0;
    for (std::uint64_t i = 0; i < kReplayRows; ++i) {
        source.PublishNext();
        EXPECT_EQ(source.next_row_index(), static_cast<std::size_t>(i + 1));

        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(source.TryGetLatest(sample, Duration{0})) << "row " << i;
        const std::int64_t expected_time = ExpectedHostTimeNs(i);
        EXPECT_EQ(sample.seq, static_cast<std::uint32_t>(i + 1));
        EXPECT_EQ(sample.time, expected_time);
        EXPECT_EQ(clock.Now(), expected_time);
        EXPECT_EQ(sample.state, ExpectedState(i));
        EXPECT_DOUBLE_EQ(sample.pose.position.x, 0.0);
        EXPECT_DOUBLE_EQ(sample.pose.position.y, 0.0);
        EXPECT_DOUBLE_EQ(sample.pose.position.z, 0.0);
        EXPECT_NEAR(YawDegrees(sample.pose), ExpectedYawDegrees(i), 1e-6);
        EXPECT_NEAR(PitchDegrees(sample.pose), 3.0, 1e-6);
        if (sample.state == TrackState::Unstable) {
            ++unstable_seen;
        }
        if (sample.state == TrackState::Lost) {
            ++lost_seen;
        }
    }
    EXPECT_EQ(unstable_seen, kUnstableCount);
    EXPECT_EQ(lost_seen, kLostCount);

    // Exhausted: further publishes are no-ops and the newest sample stays put.
    source.PublishNext();
    source.PublishNext();
    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(newest, Duration{0}));
    EXPECT_EQ(newest.seq, static_cast<std::uint32_t>(kReplayRows));
    EXPECT_EQ(newest.time, ExpectedHostTimeNs(kReplayRows - 1));
    EXPECT_EQ(source.next_row_index(), static_cast<std::size_t>(kReplayRows));
    source.Stop();
}

TEST(ReplaySource, DropoutWindowIsAboutTwoHundredFiftyMilliseconds) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());

    PublishNext(source, static_cast<std::uint32_t>(kLostFirst + kLostCount));
    HeadSample first_lost = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(first_lost, Duration{0}));
    ASSERT_EQ(first_lost.state, TrackState::Lost);

    const std::int64_t last_lost_time = ExpectedHostTimeNs(kLostFirst + kLostCount - 1);
    const std::int64_t first_lost_time = ExpectedHostTimeNs(kLostFirst);
    const std::int64_t span_ns = last_lost_time - first_lost_time;
    EXPECT_GE(span_ns, 240'000'000);
    EXPECT_LE(span_ns, 260'000'000);

    source.PublishNext();
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(after, Duration{0}));
    EXPECT_EQ(after.state, TrackState::Stable);
    EXPECT_EQ(after.seq, static_cast<std::uint32_t>(kLostFirst + kLostCount + 1));
    source.Stop();
}

TEST(ReplaySource, TimingGapsArePreservedExactly) {
    const TempCsv csv("gaps", std::string(kHeader) + "0,0.000000000,0,0,0,1,0,0,0,stable\n"
                                                     "4000000,0.044444444,0,0,0,1,0,0,0,stable\n"
                                                     "250000000,2.777777778,0,0,0,1,0,0,0,stable\n"
                                                     "261111111,2.901234567,0,0,0,1,0,0,0,stable\n");
    ManualClock clock;
    ReplayHeadPoseSource source(csv.path, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());

    const std::int64_t expected[] = {0, 4'000'000, 250'000'000, 261'111'111};
    for (std::size_t i = 0; i < 4; ++i) {
        source.PublishNext();
        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(source.TryGetLatest(sample, Duration{0})) << "row " << i;
        EXPECT_EQ(sample.time, expected[i]);
        EXPECT_EQ(clock.Now(), expected[i]);
    }
    source.Stop();
}

TEST(ReplaySource, RestartResumesAtTheNextRow) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    PublishNext(source, 3);

    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(before, Duration{0}));
    EXPECT_EQ(before.seq, 3U);
    EXPECT_EQ(before.time, ExpectedHostTimeNs(2));

    source.Stop();
    source.PublishNext();
    EXPECT_EQ(source.next_row_index(), 3U);
    ASSERT_TRUE(source.Start().ok());
    source.PublishNext();
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(after, Duration{0}));
    EXPECT_EQ(after.seq, before.seq + 1);
    EXPECT_EQ(after.time, ExpectedHostTimeNs(3));
    EXPECT_EQ(clock.Now(), ExpectedHostTimeNs(3));
    source.Stop();
}

TEST(ReplaySource, LoadClearsThePreviousDatasetsPlaybackState) {
    // A second Load() replaces the dataset: the previous playback's newest
    // sample must not stay readable, and the new dataset starts a fresh
    // sequence (M-8).
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    PublishNext(source, 3);
    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(before, Duration{0}));
    EXPECT_EQ(before.seq, 3U);

    source.Stop();
    ExpectLoadOk(source);
    HeadSample after = PlaceholderSample();
    EXPECT_FALSE(source.TryGetLatest(after, Duration{0})) << "a reloaded dataset must not expose the old sample";
    ASSERT_TRUE(source.Start().ok());
    source.PublishNext();
    ASSERT_TRUE(source.TryGetLatest(after, Duration{0}));
    EXPECT_EQ(after.seq, 1U) << "a reloaded dataset starts a fresh sequence";
    EXPECT_EQ(after.time, ExpectedHostTimeNs(0));
    source.Stop();
}

TEST(ReplaySource, RecenterAppliesAReplayLocalYawOffset) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    PublishNext(source, 5);

    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(before, Duration{0}));
    EXPECT_NEAR(YawDegrees(before.pose), 2.0, 1e-6);

    ASSERT_TRUE(source.Recenter().ok());
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(after, Duration{0}));
    EXPECT_NEAR(YawDegrees(after.pose), 0.0, 0.1);
    EXPECT_NEAR(PitchDegrees(after.pose), PitchDegrees(before.pose), 0.1);
    const Quat delta = after.pose.rotation * before.pose.rotation.Inverse();
    EXPECT_NEAR(delta.x(), 0.0, 1e-6);
    EXPECT_NEAR(delta.z(), 0.0, 1e-6);

    // The offset persists for later rows: the next sweep step reads as +0.5
    // degrees from the recentred heading, and pitch is untouched.
    source.PublishNext();
    HeadSample next = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(next, Duration{0}));
    EXPECT_NEAR(YawDegrees(next.pose), 0.5, 0.1);
    EXPECT_NEAR(PitchDegrees(next.pose), 3.0, 0.1);
    source.Stop();
}

TEST(ReplaySource, PredictExtrapolatesTheCsvYawRate) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    PublishNext(source, 2);

    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(newest, Duration{0}));
    HeadSample predicted = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(predicted, Duration{50'000'000}));
    EXPECT_EQ(predicted.seq, newest.seq);
    EXPECT_EQ(predicted.state, newest.state);
    EXPECT_EQ(predicted.time, newest.time + 50'000'000);
    EXPECT_NEAR(YawDegrees(predicted.pose), 2.75, 1e-6);
    EXPECT_NEAR(PitchDegrees(predicted.pose), 3.0, 1e-6);
    source.Stop();
}

TEST(ReplaySource, PredictComposesTheDeltaOnTheRecordedRolledRotation) {
    // Two rows that differ only in yaw, over a recorded rotation that carries a
    // 20 degree roll. Rebuilding the pose from absolute yaw/pitch would drop
    // that roll; the adapter must compose the delta onto the recorded rotation.
    const Quat row1 = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 10.0 * kPi / 180.0) *
                      Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, 5.0 * kPi / 180.0) *
                      Quat::FromAxisAngle(Vec3{0.0, 0.0, 1.0}, 20.0 * kPi / 180.0);
    const Quat row2 = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 11.0 * kPi / 180.0) *
                      Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, 5.0 * kPi / 180.0) *
                      Quat::FromAxisAngle(Vec3{0.0, 0.0, 1.0}, 20.0 * kPi / 180.0);
    std::ostringstream csv;
    csv << std::setprecision(17) << kHeader;
    csv << "0,0.0,0,0,0," << row1.w() << "," << row1.x() << "," << row1.y() << "," << row1.z() << ",stable\n";
    csv << "10000000,0.01,0,0,0," << row2.w() << "," << row2.x() << "," << row2.y() << "," << row2.z() << ",stable\n";

    const TempCsv file("rolled_predict", csv.str());
    ManualClock clock;
    ReplayHeadPoseSource source(file.path, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    source.PublishNext();
    source.PublishNext();

    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(newest, Duration{0}));
    EXPECT_NEAR(RollDegrees(newest.pose), 20.0, 1e-6);
    EXPECT_NEAR(YawDegrees(newest.pose), 11.0, 1e-6);
    EXPECT_NEAR(PitchDegrees(newest.pose), 5.0, 1e-6);

    HeadSample predicted = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(predicted, Duration{50'000'000}));
    // The row-to-row yaw rate is 100 deg/s, so 50 ms adds 5 degrees of world
    // yaw on top of the recorded rotation; pitch and roll are unchanged.
    const Quat expected = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 5.0 * kPi / 180.0) * newest.pose.rotation;
    EXPECT_NEAR(predicted.pose.rotation.w(), expected.w(), 1e-9);
    EXPECT_NEAR(predicted.pose.rotation.x(), expected.x(), 1e-9);
    EXPECT_NEAR(predicted.pose.rotation.y(), expected.y(), 1e-9);
    EXPECT_NEAR(predicted.pose.rotation.z(), expected.z(), 1e-9);
    EXPECT_NEAR(RollDegrees(predicted.pose), 20.0, 1e-6) << "the recorded roll was dropped";
    EXPECT_NEAR(PitchDegrees(predicted.pose), 5.0, 1e-6);
    EXPECT_EQ(predicted.time, newest.time + 50'000'000);
    source.Stop();
}

TEST(ReplaySource, PublishAllEmitsEveryRemainingRowOnce) {
    ManualClock clock;
    ReplayHeadPoseSource source(CG_POSE_REPLAY_FIXTURE, clock);
    ExpectLoadOk(source);
    ASSERT_TRUE(source.Start().ok());
    source.PublishAll();
    EXPECT_EQ(source.next_row_index(), static_cast<std::size_t>(kReplayRows));

    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(newest, Duration{0}));
    EXPECT_EQ(newest.seq, static_cast<std::uint32_t>(kReplayRows));
    EXPECT_EQ(newest.time, ExpectedHostTimeNs(kReplayRows - 1));
    EXPECT_EQ(clock.Now(), ExpectedHostTimeNs(kReplayRows - 1));

    source.PublishAll();
    HeadSample again = PlaceholderSample();
    ASSERT_TRUE(source.TryGetLatest(again, Duration{0}));
    EXPECT_EQ(again.seq, newest.seq);
    EXPECT_EQ(again.time, newest.time);
    source.Stop();
}

} // namespace
} // namespace cg::glasses
