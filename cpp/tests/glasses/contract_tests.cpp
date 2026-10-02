#include "fake_script.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <gtest/gtest.h>

#include "cg/core_math/quat.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "fake_viture_api.hpp"

namespace cg::glasses::test {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadiansToDegrees = 180.0 / kPi;
constexpr std::int64_t kSamplePeriodNs = 10'000'000;
constexpr std::int64_t kHundredMillisecondsNs = 100'000'000;

/// `HeadSample` is an aggregate with a non-default-constructible `Quat`, so
/// every read target and test fixture is built through this placeholder.
HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

/// Forward axis of a head pose: right-handed, Y up, zero yaw faces -Z
/// (ADR-0004).
core_math::Vec3 Forward(const core_math::Pose &pose) noexcept {
    return pose.rotation.Rotate(core_math::Vec3{0.0, 0.0, -1.0});
}

double YawDegrees(const core_math::Pose &pose) noexcept {
    const core_math::Vec3 forward = Forward(pose);
    return std::atan2(-forward.x, -forward.z) * kRadiansToDegrees;
}

double PitchDegrees(const core_math::Pose &pose) noexcept {
    const core_math::Vec3 forward = Forward(pose);
    return std::asin(std::clamp(forward.y, -1.0, 1.0)) * kRadiansToDegrees;
}

void ExpectFiniteUnitPose(const HeadSample &sample) {
    EXPECT_TRUE(std::isfinite(sample.pose.rotation.w()));
    EXPECT_TRUE(std::isfinite(sample.pose.rotation.x()));
    EXPECT_TRUE(std::isfinite(sample.pose.rotation.y()));
    EXPECT_TRUE(std::isfinite(sample.pose.rotation.z()));
    EXPECT_TRUE(sample.pose.rotation.IsNormalized(1e-9));
    EXPECT_TRUE(std::isfinite(sample.pose.position.x));
    EXPECT_TRUE(std::isfinite(sample.pose.position.y));
    EXPECT_TRUE(std::isfinite(sample.pose.position.z));
}

void StartIsIdempotent(SourceUnderTest &uut) {
    EXPECT_TRUE(uut.source->Start().ok());
    EXPECT_TRUE(uut.source->Start().ok());
    uut.source->Stop();
}

void StopIsIdempotentAndTotal(SourceUnderTest &uut) {
    uut.source->Stop();
    uut.source->Stop();
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    uut.advance(2);
    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(before, Duration{0}));
    uut.source->Stop();
    uut.source->Stop();
    uut.advance(4);
    HeadSample after = PlaceholderSample();
    if (uut.source->TryGetLatest(after, Duration{0})) {
        EXPECT_EQ(after.seq, before.seq);
        EXPECT_EQ(after.time, before.time);
    }
    ASSERT_TRUE(uut.source->Start().ok());
    uut.advance(1);
    HeadSample resumed = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(resumed, Duration{0}));
    EXPECT_GT(resumed.seq, before.seq);
    EXPECT_GT(resumed.time, before.time);
    uut.source->Stop();
}

void TryGetLatestIsFalseBeforeTheFirstSample(SourceUnderTest &uut) {
    HeadSample sample = PlaceholderSample();
    EXPECT_FALSE(uut.source->TryGetLatest(sample, Duration{0}));
}

void SequenceAndTimeStrictlyIncrease(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    HeadSample previous = PlaceholderSample();
    bool have_previous = false;
    for (int i = 0; i < 8; ++i) {
        uut.advance(1);
        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
        if (have_previous) {
            EXPECT_GT(sample.seq, previous.seq);
            EXPECT_GT(sample.time, previous.time);
        }
        previous = sample;
        have_previous = true;
    }
    uut.source->Stop();
}

void PosesAreFiniteAndUnitQuaternions(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    for (int i = 0; i < 5; ++i) {
        uut.advance(1);
        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
        ExpectFiniteUnitPose(sample);
    }
    uut.source->Stop();
}

void RecenterZeroesYawAndPreservesPitchRoll(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    for (int i = 0; i < 5; ++i) {
        uut.advance(1);
    }
    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(before, Duration{0}));
    ASSERT_TRUE(uut.source->Recenter().ok());
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(after, Duration{0}));

    EXPECT_NEAR(YawDegrees(after.pose), 0.0, 0.1);
    EXPECT_NEAR(PitchDegrees(after.pose), PitchDegrees(before.pose), 0.1);

    // Recentring is a pure world-yaw rotation of the pose: the delta between
    // the two rotations has no X or Z quaternion component.
    const core_math::Quat delta = after.pose.rotation * before.pose.rotation.Inverse();
    EXPECT_NEAR(delta.x(), 0.0, 1e-6);
    EXPECT_NEAR(delta.z(), 0.0, 1e-6);
    uut.source->Stop();
}

void PredictZeroReturnsTheNewestSampleVerbatim(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    uut.advance(1);
    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(newest, Duration{0}));
    HeadSample predicted = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(predicted, Duration{50'000'000}));
    // A prediction never publishes: it carries the pre-predict sample's seq
    // and state, with only the pose and time extrapolated.
    EXPECT_EQ(predicted.seq, newest.seq);
    EXPECT_EQ(predicted.state, newest.state);
    EXPECT_EQ(predicted.time, newest.time + 50'000'000);
    HeadSample verbatim = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(verbatim, Duration{0}));

    EXPECT_EQ(verbatim.seq, newest.seq);
    EXPECT_EQ(verbatim.time, newest.time);
    EXPECT_EQ(verbatim.state, newest.state);
    EXPECT_DOUBLE_EQ(verbatim.pose.rotation.w(), newest.pose.rotation.w());
    EXPECT_DOUBLE_EQ(verbatim.pose.rotation.x(), newest.pose.rotation.x());
    EXPECT_DOUBLE_EQ(verbatim.pose.rotation.y(), newest.pose.rotation.y());
    EXPECT_DOUBLE_EQ(verbatim.pose.rotation.z(), newest.pose.rotation.z());
    EXPECT_DOUBLE_EQ(verbatim.pose.position.x, newest.pose.position.x);
    EXPECT_DOUBLE_EQ(verbatim.pose.position.y, newest.pose.position.y);
    EXPECT_DOUBLE_EQ(verbatim.pose.position.z, newest.pose.position.z);
    uut.source->Stop();
}

void PredictExtrapolatesInTheSweepDirection(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    uut.advance(1);
    HeadSample first = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(first, Duration{0}));
    for (int i = 0; i < 5; ++i) {
        uut.advance(1);
    }
    HeadSample newest = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(newest, Duration{0}));
    HeadSample predicted = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(predicted, Duration{50'000'000}));

    const double direction = YawDegrees(newest.pose) - YawDegrees(first.pose);
    const double predicted_delta = YawDegrees(predicted.pose) - YawDegrees(newest.pose);
    if (direction > 0.0) {
        EXPECT_GT(predicted_delta, 0.0);
    } else if (direction < 0.0) {
        EXPECT_LT(predicted_delta, 0.0);
    } else {
        EXPECT_DOUBLE_EQ(predicted_delta, 0.0);
    }
    EXPECT_EQ(predicted.time, newest.time + 50'000'000);
    uut.source->Stop();
}

void PredictIsCappedAtHundredMilliseconds(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    uut.advance(2);
    HeadSample capped = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(capped, Duration{kHundredMillisecondsNs}));
    HeadSample beyond = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(beyond, Duration{1'000'000'000}));

    EXPECT_EQ(beyond.time, capped.time);
    EXPECT_DOUBLE_EQ(beyond.pose.rotation.w(), capped.pose.rotation.w());
    EXPECT_DOUBLE_EQ(beyond.pose.rotation.x(), capped.pose.rotation.x());
    EXPECT_DOUBLE_EQ(beyond.pose.rotation.y(), capped.pose.rotation.y());
    EXPECT_DOUBLE_EQ(beyond.pose.rotation.z(), capped.pose.rotation.z());
    uut.source->Stop();
}

void RestartResumesOrdering(SourceUnderTest &uut) {
    ASSERT_TRUE(uut.source->Start().ok());
    ASSERT_TRUE(uut.advance) << "the contract suite needs a sample driver";
    uut.advance(2);
    HeadSample before = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(before, Duration{0}));
    uut.source->Stop();
    ASSERT_TRUE(uut.source->Start().ok());
    uut.advance(2);
    HeadSample after = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(after, Duration{0}));
    EXPECT_GT(after.seq, before.seq);
    EXPECT_GT(after.time, before.time);
    uut.source->Stop();
}

struct ContractCase {
    const char *name;
    void (*body)(SourceUnderTest &);
};

constexpr ContractCase kContractCases[] = {
    {"StartIsIdempotent", &StartIsIdempotent},
    {"StopIsIdempotentAndTotal", &StopIsIdempotentAndTotal},
    {"TryGetLatestIsFalseBeforeTheFirstSample", &TryGetLatestIsFalseBeforeTheFirstSample},
    {"SequenceAndTimeStrictlyIncrease", &SequenceAndTimeStrictlyIncrease},
    {"PosesAreFiniteAndUnitQuaternions", &PosesAreFiniteAndUnitQuaternions},
    {"RecenterZeroesYawAndPreservesPitchRoll", &RecenterZeroesYawAndPreservesPitchRoll},
    {"PredictZeroReturnsTheNewestSampleVerbatim", &PredictZeroReturnsTheNewestSampleVerbatim},
    {"PredictExtrapolatesInTheSweepDirection", &PredictExtrapolatesInTheSweepDirection},
    {"PredictIsCappedAtHundredMilliseconds", &PredictIsCappedAtHundredMilliseconds},
    {"RestartResumesOrdering", &RestartResumesOrdering},
};

class FactoryContractTest : public ::testing::Test {
  public:
    FactoryContractTest(ContractFactory factory, ContractCase test_case)
        : factory_(std::move(factory)), test_case_(test_case) {}

    void TestBody() override {
        SourceUnderTest uut = factory_.make();
        ASSERT_NE(uut.source, nullptr);
        test_case_.body(uut);
    }

  private:
    ContractFactory factory_;
    ContractCase test_case_;
};

TEST(StatusResult, FromCgStatusMapsEveryCodeOneToOne) {
    const std::pair<cg_status, cg::StatusCode> kTable[] = {
        {CG_OK, cg::StatusCode::Ok},
        {CG_ERR_INVALID_ARG, cg::StatusCode::InvalidArgument},
        {CG_ERR_NOT_READY, cg::StatusCode::NotReady},
        {CG_ERR_DEVICE, cg::StatusCode::Device},
        {CG_ERR_TIMEOUT, cg::StatusCode::Timeout},
        {CG_ERR_UNSUPPORTED, cg::StatusCode::Unsupported},
        {CG_ERR_INTERNAL, cg::StatusCode::Internal},
    };
    for (const std::pair<cg_status, cg::StatusCode> &row : kTable) {
        const cg::Status status = cg::FromCgStatus(row.first);
        EXPECT_EQ(status.code(), row.second) << "cg_status: " << static_cast<int>(row.first);
        EXPECT_EQ(status.IsOk(), row.first == CG_OK);
    }
}

TEST(StatusResult, ValueResultCarriesEitherValueOrStatus) {
    cg::Result<int> value = cg::Ok(42);
    EXPECT_TRUE(value.ok());
    EXPECT_TRUE(value.status().IsOk());
    EXPECT_EQ(*value, 42);

    cg::Result<int> error = cg::Err<int>(cg::Status{cg::StatusCode::Timeout, "poll timed out"});
    EXPECT_FALSE(error.ok());
    EXPECT_EQ(error.status().code(), cg::StatusCode::Timeout);
    EXPECT_STREQ(error.status().message(), "poll timed out");
}

TEST(StatusResult, VoidResultCarriesStatus) {
    const cg::Result<void> ok = cg::Ok();
    EXPECT_TRUE(ok.ok());
    EXPECT_TRUE(ok.status().IsOk());

    const cg::Result<void> error = cg::Err<void>(cg::Status{cg::StatusCode::Device, "device removed"});
    EXPECT_FALSE(error.ok());
    EXPECT_EQ(error.status().code(), cg::StatusCode::Device);
    EXPECT_STREQ(error.status().message(), "device removed");
}

TEST(StatusResult, ResultMovesMoveOnlyValuesWithoutHeap) {
    cg::Result<std::unique_ptr<int>> value = cg::Ok(std::make_unique<int>(7));
    ASSERT_TRUE(value.ok());
    EXPECT_EQ(**value, 7);
}

TEST(PoseSlot, TryReadIsFalseBeforeTheFirstPublish) {
    PoseSlot slot;
    HeadSample out = PlaceholderSample();
    EXPECT_FALSE(slot.TryRead(out));
}

TEST(PoseSlot, ReadsBackThePublishedSampleVerbatim) {
    PoseSlot slot;
    const core_math::Quat kRotation = core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, 0.5);
    const HeadSample kSample{123'456'789, core_math::Pose{core_math::Vec3{1.0, 2.0, 3.0}, kRotation},
                             TrackState::Unstable, 4};
    slot.Publish(kSample);

    HeadSample out = PlaceholderSample();
    ASSERT_TRUE(slot.TryRead(out));
    EXPECT_EQ(out.seq, kSample.seq);
    EXPECT_EQ(out.time, kSample.time);
    EXPECT_EQ(out.state, kSample.state);
    EXPECT_DOUBLE_EQ(out.pose.position.x, kSample.pose.position.x);
    EXPECT_DOUBLE_EQ(out.pose.rotation.w(), kSample.pose.rotation.w());
    EXPECT_DOUBLE_EQ(out.pose.rotation.y(), kSample.pose.rotation.y());
}

TEST(PoseSlot, ConcurrentReadersNeverSeeTornOrOutOfOrderSamples) {
    constexpr std::uint32_t kSamples = 20'000;
    PoseSlot slot;
    std::atomic<bool> writer_done{false};
    std::atomic<int> torn_count{0};
    std::atomic<int> order_violations{0};
    std::atomic<std::uint64_t> false_count{0};

    std::thread writer([&slot, &writer_done] {
        for (std::uint32_t i = 1; i <= kSamples; ++i) {
            const HeadSample sample{
                static_cast<std::int64_t>(i) * 1000,
                core_math::Pose{core_math::Vec3{static_cast<double>(i), 0.0, 0.0}, core_math::Quat::kIdentity},
                TrackState::Stable, i};
            slot.Publish(sample);
            // A real polling writer blocks between samples. Pacing keeps the
            // readers out of the transient bounded-retry exhaustion state
            // (which must yield false, never a torn sample).
            std::this_thread::yield();
        }
        writer_done.store(true, std::memory_order_release);
    });

    const auto reader = [&slot, &writer_done, &torn_count, &order_violations,
                         &false_count](std::atomic<std::uint64_t> *valid_count) {
        std::uint32_t last_seq = 0;
        while (!writer_done.load(std::memory_order_acquire)) {
            HeadSample sample = PlaceholderSample();
            if (!slot.TryRead(sample)) {
                // Transient exhaustion is allowed (R39); a false read must be
                // retried by the caller, which keeps its previous frame.
                ++false_count;
                continue;
            }
            ++*valid_count;
            const bool torn = sample.time != static_cast<std::int64_t>(sample.seq) * 1000 ||
                              sample.pose.position.x != static_cast<double>(sample.seq);
            if (torn) {
                ++torn_count;
            } else if (sample.seq < last_seq) {
                ++order_violations;
            }
            last_seq = sample.seq;
        }
    };
    std::atomic<std::uint64_t> first_valid{0};
    std::atomic<std::uint64_t> second_valid{0};
    std::thread first(reader, &first_valid);
    std::thread second(reader, &second_valid);

    writer.join();
    first.join();
    second.join();
    EXPECT_EQ(torn_count.load(), 0);
    EXPECT_EQ(order_violations.load(), 0);
    EXPECT_NE(first_valid.load(), 0U);
    EXPECT_NE(second_valid.load(), 0U);
}

TEST(FakeSource, StaticHoldsIdentityAtTheFixedRate) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::Static());
    ASSERT_TRUE(uut.source->Start().ok());
    uut.advance(1);
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.seq, 1U);
    EXPECT_EQ(sample.time, 0);
    EXPECT_EQ(sample.state, TrackState::Stable);
    EXPECT_DOUBLE_EQ(sample.pose.rotation.w(), 1.0);
    EXPECT_DOUBLE_EQ(sample.pose.position.x, 0.0);

    uut.advance(1);
    ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.seq, 2U);
    EXPECT_EQ(sample.time, kSamplePeriodNs);
    uut.source->Stop();
}

TEST(FakeSource, YawSweepAdvancesAtTheRequestedRate) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::YawSweep(90.0));
    ASSERT_TRUE(uut.source->Start().ok());
    for (int i = 0; i < 10; ++i) {
        uut.advance(1);
    }
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
    EXPECT_NEAR(YawDegrees(sample.pose), 9.0, 1e-9);
    uut.source->Stop();
}

TEST(FakeSource, PitchSweepAdvancesAtTheRequestedRate) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::PitchSweep(45.0));
    ASSERT_TRUE(uut.source->Start().ok());
    for (int i = 0; i < 10; ++i) {
        uut.advance(1);
    }
    HeadSample sample = PlaceholderSample();
    ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
    EXPECT_NEAR(PitchDegrees(sample.pose), 4.5, 1e-9);
    uut.source->Stop();
}

TEST(FakeSource, DropoutReportsLostForItsWindow) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::Dropout(2, 2));
    ASSERT_TRUE(uut.source->Start().ok());
    const TrackState kExpected[] = {TrackState::Stable, TrackState::Stable, TrackState::Lost, TrackState::Lost,
                                    TrackState::Stable};
    for (const TrackState expected : kExpected) {
        uut.advance(1);
        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
        EXPECT_EQ(sample.state, expected);
    }
    uut.source->Stop();
}

TEST(FakeSource, UnstableReportsUnstableForItsWindow) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::Unstable(1, 2));
    ASSERT_TRUE(uut.source->Start().ok());
    const TrackState kExpected[] = {TrackState::Stable, TrackState::Unstable, TrackState::Unstable, TrackState::Stable};
    for (const TrackState expected : kExpected) {
        uut.advance(1);
        HeadSample sample = PlaceholderSample();
        ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
        EXPECT_EQ(sample.state, expected);
    }
    uut.source->Stop();
}

TEST(FakeSource, JitterIsBoundedAndDeterministicForASeed) {
    SourceUnderTest same_a = MakeFakeSource(FakeScript::Jitter(5.0, 7));
    SourceUnderTest same_b = MakeFakeSource(FakeScript::Jitter(5.0, 7));
    SourceUnderTest other = MakeFakeSource(FakeScript::Jitter(5.0, 8));
    ASSERT_TRUE(same_a.source->Start().ok());
    ASSERT_TRUE(same_b.source->Start().ok());
    ASSERT_TRUE(other.source->Start().ok());

    bool differs = false;
    for (int i = 0; i < 32; ++i) {
        same_a.advance(1);
        same_b.advance(1);
        other.advance(1);
        HeadSample a = PlaceholderSample();
        HeadSample b = PlaceholderSample();
        HeadSample c = PlaceholderSample();
        ASSERT_TRUE(same_a.source->TryGetLatest(a, Duration{0}));
        ASSERT_TRUE(same_b.source->TryGetLatest(b, Duration{0}));
        ASSERT_TRUE(other.source->TryGetLatest(c, Duration{0}));
        EXPECT_LE(std::abs(YawDegrees(a.pose)), 5.0 + 1e-9);
        EXPECT_LE(std::abs(PitchDegrees(a.pose)), 5.0 + 1e-9);
        EXPECT_DOUBLE_EQ(YawDegrees(a.pose), YawDegrees(b.pose));
        EXPECT_DOUBLE_EQ(PitchDegrees(a.pose), PitchDegrees(b.pose));
        if (YawDegrees(a.pose) != YawDegrees(c.pose)) {
            differs = true;
        }
    }
    EXPECT_TRUE(differs);
    same_a.source->Stop();
    same_b.source->Stop();
    other.source->Stop();
}

TEST(FakeSource, AppearAfterSuppressesSamplesThenStarts) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::AppearAfter(3));
    ASSERT_TRUE(uut.source->Start().ok());
    uut.advance(3);
    HeadSample sample = PlaceholderSample();
    EXPECT_FALSE(uut.source->TryGetLatest(sample, Duration{0}));
    uut.advance(1);
    ASSERT_TRUE(uut.source->TryGetLatest(sample, Duration{0}));
    EXPECT_EQ(sample.seq, 1U);
    EXPECT_EQ(sample.time, 3 * kSamplePeriodNs);
    uut.source->Stop();
}

TEST(FakeSource, InvalidRateIsRejectedWithoutPublishing) {
    SourceUnderTest uut = MakeFakeSource(FakeScript::Static(), 0.0);
    const cg::Result<void> result = uut.source->Start();
    EXPECT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), cg::StatusCode::InvalidArgument);
    HeadSample sample = PlaceholderSample();
    EXPECT_FALSE(uut.source->TryGetLatest(sample, Duration{0}));
}

/// Owns a `VitureHeadPoseSource` together with the fake API and the manual
/// host clock it reads, in an order that keeps the fakes alive until after the
/// source has stopped. `advance` releases exactly one gated poll per step, so
/// the slot is quiescent between reads (free-running sources would race the
/// multi-read contract cases).
class VitureHarnessSource final : public IHeadPoseSource {
  public:
    VitureHarnessSource(std::shared_ptr<test::FakeVitureApi> api, std::shared_ptr<ManualHostClock> clock)
        : api_(std::move(api)), clock_(std::move(clock)), source_(*api_, *clock_) {}

    ~VitureHarnessSource() override { source_.Stop(); }

    Result<void> Start() override {
        const Result<void> result = source_.Start();
        if (result.ok()) {
            stopped_.store(false, std::memory_order_relaxed);
        }
        return result;
    }

    void Stop() noexcept override {
        source_.Stop();
        stopped_.store(true, std::memory_order_relaxed);
    }

    [[nodiscard]] bool TryGetLatest(HeadSample &out, Duration predict) const noexcept override {
        return source_.TryGetLatest(out, predict);
    }

    Result<void> Recenter() override {
        // The polling thread must make progress for the reset to be handled;
        // opening the gate lets one poll through while the reset is in flight.
        api_->SetPollGate(false);
        const Result<void> result = source_.Recenter();
        api_->SetPollGate(true);
        return result;
    }

    [[nodiscard]] bool stopped() const noexcept { return stopped_.load(std::memory_order_relaxed); }
    void AllowOnePoll() noexcept { api_->AllowOnePoll(); }

  private:
    std::shared_ptr<test::FakeVitureApi> api_;
    std::shared_ptr<ManualHostClock> clock_;
    VitureHeadPoseSource source_;
    std::atomic<bool> stopped_{true};
};

SourceUnderTest MakeVitureSource() {
    auto api = std::make_shared<test::FakeVitureApi>();
    auto clock = std::make_shared<ManualHostClock>();
    static_cast<void>(api->FeedScript(FakeScript::YawSweep(10.0), 100.0));
    api->SetPollGate(true);
    auto harness = std::make_unique<VitureHarnessSource>(api, clock);
    auto last_seq = std::make_shared<std::atomic<std::uint32_t>>(0);

    ManualHostClock *clock_raw = clock.get();
    VitureHarnessSource *raw = harness.get();
    SourceUnderTest uut;
    uut.advance = [raw, clock_raw, last_seq](std::uint32_t count) {
        for (std::uint32_t i = 0; i < count; ++i) {
            if (raw->stopped()) {
                return;
            }
            clock_raw->Advance(Duration{kSamplePeriodNs});
            raw->AllowOnePoll();
            const std::uint32_t target = last_seq->load() + 1;
            HeadSample sample = PlaceholderSample();
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
            while (std::chrono::steady_clock::now() < deadline) {
                if (raw->TryGetLatest(sample, Duration{0}) && sample.seq >= target) {
                    last_seq->store(sample.seq);
                    break;
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }
    };
    uut.source = std::move(harness);
    return uut;
}

[[maybe_unused]] const bool kFakeFactoryRegistered = [] {
    AddContractFactory(ContractFactory{"fake", [] { return MakeFakeSource(FakeScript::YawSweep(45.0)); }});
    return true;
}();

[[maybe_unused]] const bool kVitureFactoryRegistered = [] {
    AddContractFactory(ContractFactory{"viture-fake", [] { return MakeVitureSource(); }});
    return true;
}();

} // namespace

SourceUnderTest MakeFakeSource(FakeScript script, double rate_hz) {
    auto clock = std::make_shared<ManualClock>();
    auto source = std::make_unique<FakeHeadPoseSource>(FakeHeadPoseSourceConfig{rate_hz, clock.get(), script});
    SourceUnderTest uut;
    uut.clock = clock.get();
    uut.advance = [raw = source.get(), keeper = clock](std::uint32_t count) { raw->AdvanceSamples(count); };
    uut.source = std::move(source);
    return uut;
}

std::vector<ContractFactory> &ContractFactories() {
    static std::vector<ContractFactory> factories;
    return factories;
}

void AddContractFactory(ContractFactory factory) { ContractFactories().push_back(std::move(factory)); }

void RegisterContractSuite() {
    for (const ContractFactory &factory : ContractFactories()) {
        for (const ContractCase &test_case : kContractCases) {
            const std::string name = std::string(test_case.name) + "/" + factory.name;
            ::testing::RegisterTest(
                "GlassesContract", name.c_str(), nullptr, nullptr, __FILE__, __LINE__,
                [factory, test_case]() -> ::testing::Test * { return new FactoryContractTest(factory, test_case); });
        }
    }
}

} // namespace cg::glasses::test

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    cg::glasses::test::RegisterContractSuite();
    return RUN_ALL_TESTS();
}
