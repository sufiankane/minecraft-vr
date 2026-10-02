#include "cg/glasses/viture_head_pose_source.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>

#include "cg/core_math/convert.hpp"
#include "cg/core_math/time.hpp"
#include "yaw_unwrap.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace cg::glasses {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadiansToDegrees = 180.0 / kPi;
constexpr double kDegreesToRadians = kPi / 180.0;

/// Maps the C ABI tracking state onto the contract vocabulary.
[[nodiscard]] TrackState MapState(cg_track_state state) noexcept {
    switch (state) {
    case CG_TRACK_STABLE:
        return TrackState::Stable;
    case CG_TRACK_UNSTABLE:
        return TrackState::Unstable;
    case CG_TRACK_LOST:
        return TrackState::Lost;
    }
    return TrackState::Lost;
}

/// Yaw of a pose: the heading of its forward axis (-Z) about world +Y.
[[nodiscard]] double YawDegrees(const core_math::Pose &pose) noexcept {
    const core_math::Vec3 forward = pose.rotation.Rotate(core_math::Vec3{0.0, 0.0, -1.0});
    return std::atan2(-forward.x, -forward.z) * kRadiansToDegrees;
}

/// Pitch of a pose: the elevation of its forward axis (-Z).
[[nodiscard]] double PitchDegrees(const core_math::Pose &pose) noexcept {
    const core_math::Vec3 forward = pose.rotation.Rotate(core_math::Vec3{0.0, 0.0, -1.0});
    return std::asin(std::clamp(forward.y, -1.0, 1.0)) * kRadiansToDegrees;
}

} // namespace

VitureHeadPoseSource::VitureHeadPoseSource(IVitureApi &api, IHostClock &clock, ThreadSetupHook setup_hook)
    : api_(api), clock_(clock), setup_hook_(std::move(setup_hook)) {}

VitureHeadPoseSource::~VitureHeadPoseSource() { Stop(); }

Result<void> VitureHeadPoseSource::Start() {
    const std::lock_guard<std::mutex> lock(lifecycle_mutex_);
    if (running_.load(std::memory_order_acquire)) {
        return Ok();
    }

    const Result<void> created = api_.CreateDevice();
    if (!created.ok()) {
        return created;
    }
    device_alive_.store(true, std::memory_order_release);
    const Result<void> started = api_.StartPose();
    if (!started.ok()) {
        device_alive_.store(false, std::memory_order_release);
        api_.DestroyDevice();
        return started;
    }

    stop_requested_.store(false, std::memory_order_release);
    recentre_requested_.store(false, std::memory_order_release);
    recentre_hold_.store(false, std::memory_order_release);
    running_.store(true, std::memory_order_release);
    try {
        thread_ = std::jthread([this](std::stop_token stop) { PollLoop(stop); });
    } catch (...) {
        running_.store(false, std::memory_order_release);
        device_alive_.store(false, std::memory_order_release);
        api_.DestroyDevice();
        return Err<void>(Status{StatusCode::Internal, "viture: could not start the polling thread"});
    }
    return Ok();
}

void VitureHeadPoseSource::Stop() noexcept {
    const std::lock_guard<std::mutex> lock(lifecycle_mutex_);
    if (!thread_.joinable()) {
        running_.store(false, std::memory_order_release);
        return;
    }
    stop_requested_.store(true, std::memory_order_release);
    api_.RequestStop();
    thread_.request_stop();
    thread_.join();
    device_alive_.store(false, std::memory_order_release);
    api_.DestroyDevice();
    running_.store(false, std::memory_order_release);
    {
        // Wake a Recenter that raced with the shutdown so it cannot park here.
        const std::lock_guard<std::mutex> recentre_lock(recentre_mutex_);
        recentre_cv_.notify_all();
    }
}

Result<void> VitureHeadPoseSource::Recenter() {
    const std::lock_guard<std::mutex> call_lock(recentre_call_mutex_);
    std::unique_lock<std::mutex> lock(recentre_mutex_);
    if (!running_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "viture: source is not running"});
    }
    if (!has_published_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "viture: no pose to recentre"});
    }
    if (!device_alive_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "viture: device is not alive"});
    }
    recentre_requested_.store(true, std::memory_order_release);
    recentre_cv_.wait(lock, [this] { return recentre_done_ || !running_.load(std::memory_order_acquire); });
    if (!recentre_done_) {
        return Err<void>(Status{StatusCode::NotReady, "viture: source stopped before the recentre completed"});
    }
    recentre_done_ = false;
    return recentre_result_;
}

std::optional<double> VitureHeadPoseSource::LastSdkSeconds() const noexcept {
    if (!has_sdk_seconds_.load(std::memory_order_acquire)) {
        return std::nullopt;
    }
    return last_sdk_seconds_.load(std::memory_order_relaxed);
}

bool VitureHeadPoseSource::TryGetLatest(HeadSample &out, Duration predict) const noexcept {
    HeadSample newest{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
    if (!slot_.TryRead(newest)) {
        return false;
    }
    out = newest;

    // Samples published before the most recent reset are still in the old
    // frame; later samples already arrive recentred from the SDK.
    const std::uint32_t until_seq = recentre_until_seq_.load(std::memory_order_acquire);
    const bool recentred = newest.seq <= until_seq;
    const std::int64_t capped_ns = std::clamp<std::int64_t>(predict.ns, 0, kMaxPredictNs);
    if (recentred || capped_ns > 0) {
        const double dt_s = core_math::ToSeconds(capped_ns);
        const double yaw_delta_deg = (recentred ? yaw_offset_deg_.load(std::memory_order_relaxed) : 0.0) +
                                     yaw_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        const double pitch_delta_deg = pitch_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        if (yaw_delta_deg != 0.0 || pitch_delta_deg != 0.0) {
            out.pose.rotation =
                core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_delta_deg * kDegreesToRadians) *
                out.pose.rotation *
                core_math::Quat::FromAxisAngle(core_math::Vec3{1.0, 0.0, 0.0}, pitch_delta_deg * kDegreesToRadians);
        }
        if (capped_ns > 0) {
            out.time = newest.time + capped_ns;
        }
    }

    if (recentre_hold_.load(std::memory_order_acquire)) {
        // A recentre is waiting for a reader to observe the corrected sample.
        recentre_hold_.store(false, std::memory_order_release);
    }
    return true;
}

void VitureHeadPoseSource::PollLoop(std::stop_token stop) noexcept {
    SetupThread();

    Duration backoff = kInitialBackoff;
    int attempts = 0;
    last_success_ns_ = clock_.Now();
    quiet_state_ = TrackState::Stable;

    for (;;) {
        if (StopRequested(stop)) {
            break;
        }
        ServiceRecentre(stop);

        const Result<cg_head_sample> polled = api_.PollPose();
        if (StopRequested(stop)) {
            break;
        }
        const HostTime now = clock_.Now();
        if (polled.ok()) {
            const cg_head_sample &sdk = *polled;
            const double sdk_seconds = core_math::ToSeconds(sdk.host_time);
            mapper_.AddSample(sdk_seconds, now);
            HostTime mapped = mapper_.Map(sdk_seconds);
            if (last_published_.has_value() && mapped < last_published_->time) {
                // U-01 assumption: the SDK stamp is monotonic within a session.
                // If a device recreate restarted it (or a stale window maps the
                // new base into the past), re-seed the mapper from this sample
                // so the published time cannot regress.
                mapper_ = core_math::ClockMapper{};
                mapper_.AddSample(sdk_seconds, now);
                mapped = mapper_.Map(sdk_seconds);
            }
            const float sdk_pose[7] = {sdk.pose.p.x, sdk.pose.p.y, sdk.pose.p.z, sdk.pose.q.w,
                                       sdk.pose.q.x, sdk.pose.q.y, sdk.pose.q.z};
            HeadSample sample{mapped, core_math::PoseFromSdk(sdk_pose), MapState(sdk.state), ++seq_};
            UpdateRate(sample);
            // Diagnostics-only stamp: stored before the slot publish, so a
            // reader that sees this sample cannot observe an older stamp.
            last_sdk_seconds_.store(sdk_seconds, std::memory_order_relaxed);
            has_sdk_seconds_.store(true, std::memory_order_release);
            slot_.Publish(sample);
            last_published_ = sample;
            has_published_.store(true, std::memory_order_release);
            attempts = 0;
            backoff = kInitialBackoff;
            last_success_ns_ = now;
            quiet_state_ = TrackState::Stable;
            continue;
        }

        PublishQuiet(now);
        if (attempts < kMaxReconnectAttempts) {
            ++attempts;
            device_alive_.store(false, std::memory_order_release);
            api_.DestroyDevice();
            if (!WaitBackoff(backoff, stop)) {
                break;
            }
            backoff = Duration{std::min<std::int64_t>(backoff.ns * 2, kMaxBackoff.ns)};
            const Result<void> created = api_.CreateDevice();
            if (!created.ok()) {
                continue;
            }
            device_alive_.store(true, std::memory_order_release);
            const Result<void> started = api_.StartPose();
            if (!started.ok()) {
                device_alive_.store(false, std::memory_order_release);
                api_.DestroyDevice();
                continue;
            }
        } else if (!WaitBackoff(kMaxBackoff, stop)) {
            break;
        }
    }
}

void VitureHeadPoseSource::SetupThread() noexcept {
#ifdef _WIN32
    ::SetThreadDescription(::GetCurrentThread(), L"cg-viture-poll");
#endif
    if (setup_hook_) {
        try {
            setup_hook_();
        } catch (...) {
            // A priority hook must never take the polling thread down.
        }
    }
}

bool VitureHeadPoseSource::StopRequested(const std::stop_token &stop) const noexcept {
    return stop.stop_requested() || stop_requested_.load(std::memory_order_relaxed);
}

void VitureHeadPoseSource::ServiceRecentre(std::stop_token stop) noexcept {
    if (!recentre_requested_.exchange(false, std::memory_order_acq_rel)) {
        return;
    }
    HandleRecentre();
    WaitForRecentreRead(stop);
}

void VitureHeadPoseSource::HandleRecentre() noexcept {
    Result<void> result = Err<void>(Status{StatusCode::NotReady, "viture: no pose to recentre"});
    if (last_published_.has_value() && device_alive_.load(std::memory_order_acquire)) {
        const HeadSample &newest = *last_published_;
        const float pose[7] = {
            static_cast<float>(newest.pose.position.x),   static_cast<float>(newest.pose.position.y),
            static_cast<float>(newest.pose.position.z),   static_cast<float>(newest.pose.rotation.w()),
            static_cast<float>(newest.pose.rotation.x()), static_cast<float>(newest.pose.rotation.y()),
            static_cast<float>(newest.pose.rotation.z())};
        result = api_.ResetOriginCarina(pose);
        if (result.ok()) {
            const double yaw_deg = YawDegrees(newest.pose);
            yaw_offset_deg_.store(-yaw_deg, std::memory_order_relaxed);
            recentre_until_seq_.store(newest.seq, std::memory_order_release);
            recentre_hold_.store(true, std::memory_order_release);
            // Later quiet synthetics must carry the recentred pose, and the
            // next real sample's rate is measured from the corrected heading.
            last_published_->pose.rotation =
                core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, -yaw_deg * kDegreesToRadians) *
                last_published_->pose.rotation;
            previous_yaw_deg_ = YawDegrees(last_published_->pose);
            previous_pitch_deg_ = PitchDegrees(last_published_->pose);
        }
    }
    {
        const std::lock_guard<std::mutex> lock(recentre_mutex_);
        recentre_result_ = result;
        recentre_done_ = true;
        recentre_cv_.notify_all();
    }
}

void VitureHeadPoseSource::WaitForRecentreRead(std::stop_token stop) noexcept {
    const HostTime deadline = clock_.Now() + kRecentreReadTimeout.ns;
    while (recentre_hold_.load(std::memory_order_acquire) && !StopRequested(stop) && clock_.Now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    recentre_hold_.store(false, std::memory_order_release);
}

bool VitureHeadPoseSource::WaitBackoff(Duration duration, std::stop_token stop) noexcept {
    const HostTime deadline = clock_.Now() + duration.ns;
    for (;;) {
        if (StopRequested(stop)) {
            return false;
        }
        ServiceRecentre(stop);
        const HostTime now = clock_.Now();
        PublishQuiet(now);
        if (now >= deadline) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}

void VitureHeadPoseSource::PublishQuiet(HostTime now) noexcept {
    if (!last_published_.has_value()) {
        return;
    }
    const HostTime quiet_ns = now - last_success_ns_;
    TrackState desired = TrackState::Stable;
    if (quiet_ns >= kLostAfter.ns) {
        desired = TrackState::Lost;
    } else if (quiet_ns >= kUnstableAfter.ns) {
        desired = TrackState::Unstable;
    }
    if (desired == quiet_state_) {
        return;
    }
    HeadSample sample{now, last_published_->pose, desired, ++seq_};
    slot_.Publish(sample);
    last_published_ = sample;
    quiet_state_ = desired;
}

void VitureHeadPoseSource::UpdateRate(const HeadSample &sample) noexcept {
    const double yaw_deg = YawDegrees(sample.pose);
    const double pitch_deg = PitchDegrees(sample.pose);
    if (have_previous_ && sample.time > previous_time_) {
        const double dt_s = core_math::ToSeconds(sample.time - previous_time_);
        if (dt_s > 0.0) {
            // A heading that crossed the +/-180 degree seam would otherwise
            // read as a ~360 degree jump and make the predicted yaw snap.
            const double delta_yaw_deg = detail::UnwrapYawDeltaDegrees(yaw_deg - previous_yaw_deg_);
            yaw_rate_deg_per_s_.store(delta_yaw_deg / dt_s, std::memory_order_relaxed);
            pitch_rate_deg_per_s_.store((pitch_deg - previous_pitch_deg_) / dt_s, std::memory_order_relaxed);
        }
    }
    previous_yaw_deg_ = yaw_deg;
    previous_pitch_deg_ = pitch_deg;
    previous_time_ = sample.time;
    have_previous_ = true;
}

} // namespace cg::glasses
