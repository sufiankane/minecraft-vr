#include "cg/glasses/fake_head_pose_source.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>

#include "cg/core_math/time.hpp"

namespace cg::glasses {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kDegreesToRadians = kPi / 180.0;
constexpr std::int64_t kMaxPredictNs = 100'000'000;

} // namespace

FakeScript FakeScript::Static() noexcept { return FakeScript{}; }

FakeScript FakeScript::YawSweep(double degrees_per_second) noexcept {
    FakeScript script;
    script.pattern = Pattern::YawSweep;
    script.value = degrees_per_second;
    return script;
}

FakeScript FakeScript::PitchSweep(double degrees_per_second) noexcept {
    FakeScript script;
    script.pattern = Pattern::PitchSweep;
    script.value = degrees_per_second;
    return script;
}

FakeScript FakeScript::Dropout(std::uint64_t after, std::uint64_t count) noexcept {
    FakeScript script;
    script.pattern = Pattern::Dropout;
    script.after = after;
    script.count = count;
    return script;
}

FakeScript FakeScript::Unstable(std::uint64_t after, std::uint64_t count) noexcept {
    FakeScript script;
    script.pattern = Pattern::Unstable;
    script.after = after;
    script.count = count;
    return script;
}

FakeScript FakeScript::Jitter(double amplitude_degrees, std::uint32_t seed) noexcept {
    FakeScript script;
    script.pattern = Pattern::Jitter;
    script.value = amplitude_degrees;
    script.seed = seed;
    return script;
}

FakeScript FakeScript::AppearAfter(std::uint64_t count) noexcept {
    FakeScript script;
    script.pattern = Pattern::AppearAfter;
    script.count = count;
    return script;
}

FakeHeadPoseSource::FakeHeadPoseSource(FakeHeadPoseSourceConfig config)
    : config_(config), clock_(config.clock != nullptr ? config.clock : &owned_clock_), rng_(config.script.seed) {}

Result<void> FakeHeadPoseSource::Start() {
    if (!std::isfinite(config_.rate_hz) || config_.rate_hz <= 0.0) {
        return Err<void>(Status{StatusCode::InvalidArgument, "rate_hz must be finite and positive"});
    }
    const FakeScript &script = config_.script;
    if (script.pattern == FakeScript::Pattern::Jitter && (!std::isfinite(script.value) || script.value < 0.0)) {
        return Err<void>(Status{StatusCode::InvalidArgument, "jitter amplitude must be finite and non-negative"});
    }
    if ((script.pattern == FakeScript::Pattern::YawSweep || script.pattern == FakeScript::Pattern::PitchSweep) &&
        !std::isfinite(script.value)) {
        return Err<void>(Status{StatusCode::InvalidArgument, "sweep rate must be finite"});
    }
    running_.store(true, std::memory_order_release);
    return Ok();
}

void FakeHeadPoseSource::Stop() noexcept { running_.store(false, std::memory_order_release); }

void FakeHeadPoseSource::AdvanceSamples(std::uint32_t count) noexcept {
    for (std::uint32_t i = 0; i < count; ++i) {
        if (!running_.load(std::memory_order_acquire)) {
            return;
        }
        EmitOne();
    }
}

bool FakeHeadPoseSource::TryGetLatest(HeadSample &out, Duration predict) const noexcept {
    HeadSample newest{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
    if (!slot_.TryRead(newest)) {
        return false;
    }
    out = newest;

    const double yaw_offset_deg = yaw_offset_deg_.load(std::memory_order_relaxed);
    if (yaw_offset_deg != 0.0) {
        out.pose.rotation =
            core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_offset_deg * kDegreesToRadians) *
            out.pose.rotation;
    }

    const std::int64_t capped_ns = std::clamp<std::int64_t>(predict.ns, 0, kMaxPredictNs);
    if (capped_ns > 0) {
        const double dt_s = core_math::ToSeconds(capped_ns);
        const double yaw_deg = yaw_offset_deg + latest_yaw_deg_.load(std::memory_order_relaxed) +
                               yaw_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        const double pitch_deg = latest_pitch_deg_.load(std::memory_order_relaxed) +
                                 pitch_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        out.pose.rotation =
            core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_deg * kDegreesToRadians) *
            core_math::Quat::FromAxisAngle(core_math::Vec3{1.0, 0.0, 0.0}, pitch_deg * kDegreesToRadians);
        out.time = newest.time + capped_ns;
    }
    return true;
}

Result<void> FakeHeadPoseSource::Recenter() {
    if (!has_published_) {
        // There is no pose to make the origin; a never-applied offset would be
        // silently wrong.
        return Err<void>(Status{StatusCode::NotReady, "fake: no sample to recentre"});
    }
    yaw_offset_deg_.store(-latest_yaw_deg_.load(std::memory_order_relaxed), std::memory_order_relaxed);
    return Ok();
}

void FakeHeadPoseSource::EmitOne() noexcept {
    const std::uint64_t index = sample_index_++;
    const FakeScript &script = config_.script;
    const Duration period{core_math::ToNanoseconds(1.0 / config_.rate_hz)};

    if (script.pattern == FakeScript::Pattern::AppearAfter && index < script.count) {
        clock_->Advance(period);
        return;
    }

    const double dt_s = core_math::ToSeconds(period.ns);
    switch (script.pattern) {
    case FakeScript::Pattern::Static:
    case FakeScript::Pattern::Dropout:
    case FakeScript::Pattern::Unstable:
    case FakeScript::Pattern::AppearAfter:
        break;
    case FakeScript::Pattern::YawSweep:
        yaw_deg_ += script.value * dt_s;
        break;
    case FakeScript::Pattern::PitchSweep:
        pitch_deg_ += script.value * dt_s;
        break;
    case FakeScript::Pattern::Jitter:
        yaw_deg_ = RandomSymmetric(script.value);
        pitch_deg_ = RandomSymmetric(script.value);
        break;
    }

    if (has_previous_) {
        yaw_rate_deg_per_s_.store((yaw_deg_ - previous_yaw_deg_) / dt_s, std::memory_order_relaxed);
        pitch_rate_deg_per_s_.store((pitch_deg_ - previous_pitch_deg_) / dt_s, std::memory_order_relaxed);
    }
    previous_yaw_deg_ = yaw_deg_;
    previous_pitch_deg_ = pitch_deg_;
    has_previous_ = true;
    latest_yaw_deg_.store(yaw_deg_, std::memory_order_relaxed);
    latest_pitch_deg_.store(pitch_deg_, std::memory_order_relaxed);

    HeadSample sample{clock_->Now(), core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, RawRotation()}, StateAt(index),
                      next_seq_++};
    slot_.Publish(sample);
    has_published_ = true;
    clock_->Advance(period);
}

TrackState FakeHeadPoseSource::StateAt(std::uint64_t index) const noexcept {
    const FakeScript &script = config_.script;
    const bool in_window = index >= script.after && index - script.after < script.count;
    if (in_window) {
        if (script.pattern == FakeScript::Pattern::Dropout) {
            return TrackState::Lost;
        }
        if (script.pattern == FakeScript::Pattern::Unstable) {
            return TrackState::Unstable;
        }
    }
    return TrackState::Stable;
}

core_math::Quat FakeHeadPoseSource::RawRotation() const noexcept {
    const core_math::Quat yaw =
        core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_deg_ * kDegreesToRadians);
    const core_math::Quat pitch =
        core_math::Quat::FromAxisAngle(core_math::Vec3{1.0, 0.0, 0.0}, pitch_deg_ * kDegreesToRadians);
    return yaw * pitch;
}

double FakeHeadPoseSource::RandomSymmetric(double amplitude) noexcept {
    const double unit = static_cast<double>(rng_()) / static_cast<double>(std::mt19937::max());
    return (2.0 * unit - 1.0) * amplitude;
}

} // namespace cg::glasses
