#include "cg/glasses/replay_head_pose_source.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <fstream>
#include <string_view>
#include <system_error>
#include <utility>

#include "cg/core_math/quat.hpp"
#include "cg/core_math/time.hpp"
#include "cg/core_math/vec3.hpp"
#include "yaw_unwrap.hpp"

namespace cg::glasses {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadiansToDegrees = 180.0 / kPi;
constexpr double kDegreesToRadians = kPi / 180.0;
constexpr std::int64_t kMaxPredictNs = 100'000'000;
constexpr std::size_t kColumnCount = 10;

constexpr std::string_view kHeaderColumns[kColumnCount] = {"host_time_ns", "sdk_time_s", "px", "py", "pz",
                                                           "qw",           "qx",         "qy", "qz", "status"};

[[nodiscard]] std::string_view Trim(std::string_view text) noexcept {
    const auto is_space = [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; };
    while (!text.empty() && is_space(text.front())) {
        text.remove_prefix(1);
    }
    while (!text.empty() && is_space(text.back())) {
        text.remove_suffix(1);
    }
    return text;
}

[[nodiscard]] std::vector<std::string_view> SplitFields(std::string_view line) {
    std::vector<std::string_view> fields;
    std::size_t start = 0;
    for (;;) {
        const std::size_t comma = line.find(',', start);
        if (comma == std::string_view::npos) {
            fields.push_back(line.substr(start));
            return fields;
        }
        fields.push_back(line.substr(start, comma - start));
        start = comma + 1;
    }
}

[[nodiscard]] bool ParseInt64(std::string_view text, std::int64_t &out) noexcept {
    if (text.empty()) {
        return false;
    }
    const char *first = text.data();
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, out);
    return result.ec == std::errc{} && result.ptr == last;
}

[[nodiscard]] bool ParseFiniteDouble(std::string_view text, double &out) noexcept {
    if (text.empty()) {
        return false;
    }
    const char *first = text.data();
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, out, std::chars_format::general);
    return result.ec == std::errc{} && result.ptr == last && std::isfinite(out);
}

[[nodiscard]] std::string ToLowerAscii(std::string_view text) {
    std::string lowered(text);
    for (char &c : lowered) {
        if (c >= 'A' && c <= 'Z') {
            c = static_cast<char>(c - 'A' + 'a');
        }
    }
    return lowered;
}

[[nodiscard]] bool IsHeader(const std::vector<std::string_view> &fields) noexcept {
    if (fields.size() != kColumnCount) {
        return false;
    }
    for (std::size_t column = 0; column < kColumnCount; ++column) {
        if (Trim(fields[column]) != kHeaderColumns[column]) {
            return false;
        }
    }
    return true;
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

ReplayHeadPoseSource::ReplayHeadPoseSource(std::filesystem::path csv, ManualClock &clock)
    : csv_(std::move(csv)), clock_(clock) {}

Result<void> ReplayHeadPoseSource::Load() {
    rows_.clear();
    next_index_ = 0;
    next_seq_ = 1;
    has_published_.store(false, std::memory_order_relaxed);
    has_previous_ = false;
    loaded_ = false;
    error_message_.clear();
    latest_yaw_deg_.store(0.0, std::memory_order_relaxed);
    yaw_rate_deg_per_s_.store(0.0, std::memory_order_relaxed);
    pitch_rate_deg_per_s_.store(0.0, std::memory_order_relaxed);
    yaw_offset_deg_.store(0.0, std::memory_order_relaxed);
    // A reload is not a resume: the previous dataset's newest sample must not
    // stay readable and its sequence numbers must not carry over (M-8).
    slot_.Reset();

    std::ifstream file(csv_, std::ios::binary);
    if (!file.is_open()) {
        error_message_ = "replay: cannot open CSV: " + csv_.string();
        return Err<void>(Status{StatusCode::InvalidArgument, error_message_.c_str()});
    }

    bool header_seen = false;
    bool have_previous_time = false;
    std::int64_t previous_host_time = 0;
    std::int64_t line_number = 0;
    std::string line;
    while (std::getline(file, line)) {
        ++line_number;
        const std::string_view stripped = Trim(line);
        if (stripped.empty()) {
            continue;
        }
        const std::vector<std::string_view> fields = SplitFields(stripped);
        if (!header_seen) {
            if (!IsHeader(fields)) {
                return Fail(line_number, "unexpected CSV header (expected "
                                         "host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status)");
            }
            header_seen = true;
            continue;
        }
        if (fields.size() != kColumnCount) {
            return Fail(line_number, "expected 10 columns, got " + std::to_string(fields.size()));
        }

        std::int64_t host_time_ns = 0;
        if (!ParseInt64(Trim(fields[0]), host_time_ns)) {
            return Fail(line_number, "host_time_ns is not an integer");
        }
        if (have_previous_time && host_time_ns <= previous_host_time) {
            return Fail(line_number, "host_time_ns must strictly increase");
        }

        double sdk_time_s = 0.0;
        if (!ParseFiniteDouble(Trim(fields[1]), sdk_time_s)) {
            return Fail(line_number, "sdk_time_s is not a finite number");
        }

        double position[3] = {0.0, 0.0, 0.0};
        constexpr std::string_view kPositionNames[3] = {"px", "py", "pz"};
        for (std::size_t axis = 0; axis < 3; ++axis) {
            if (!ParseFiniteDouble(Trim(fields[2 + axis]), position[axis])) {
                return Fail(line_number, std::string(kPositionNames[axis]) + " is not a finite number");
            }
        }

        double quaternion[4] = {0.0, 0.0, 0.0, 0.0};
        constexpr std::string_view kQuaternionNames[4] = {"qw", "qx", "qy", "qz"};
        for (std::size_t part = 0; part < 4; ++part) {
            if (!ParseFiniteDouble(Trim(fields[5 + part]), quaternion[part])) {
                return Fail(line_number, std::string(kQuaternionNames[part]) + " is not a finite number");
            }
        }

        const std::string_view status_text = Trim(fields[9]);
        const std::string status = ToLowerAscii(status_text);
        TrackState state = TrackState::Stable;
        if (status == "stable") {
            state = TrackState::Stable;
        } else if (status == "unstable") {
            state = TrackState::Unstable;
        } else if (status == "lost") {
            state = TrackState::Lost;
        } else {
            return Fail(line_number,
                        "unknown status \"" + std::string(status_text) + "\" (expected stable|unstable|lost)");
        }

        const core_math::Pose pose{
            core_math::Vec3{position[0], position[1], position[2]},
            core_math::Quat::FromComponents(quaternion[0], quaternion[1], quaternion[2], quaternion[3])};
        rows_.push_back(Row{host_time_ns, pose, state});
        previous_host_time = host_time_ns;
        have_previous_time = true;
        static_cast<void>(sdk_time_s);
    }

    if (!header_seen) {
        return Fail(1, "missing CSV header");
    }
    loaded_ = true;
    return Ok();
}

Result<void> ReplayHeadPoseSource::Start() {
    if (running_.load(std::memory_order_acquire)) {
        return Ok();
    }
    if (!loaded_) {
        return Err<void>(Status{StatusCode::NotReady, "replay: no dataset loaded"});
    }
    running_.store(true, std::memory_order_release);
    return Ok();
}

void ReplayHeadPoseSource::Stop() noexcept { running_.store(false, std::memory_order_release); }

void ReplayHeadPoseSource::PublishNext() noexcept {
    if (!running_.load(std::memory_order_acquire) || next_index_ >= rows_.size()) {
        return;
    }
    const Row &row = rows_[next_index_++];
    const HostTime delta_ns = row.host_time_ns - clock_.Now();
    if (delta_ns > 0) {
        clock_.Advance(Duration{delta_ns});
    }
    UpdateRate(row);
    const HeadSample sample{row.host_time_ns, row.pose, row.state, next_seq_++};
    slot_.Publish(sample);
    has_published_.store(true, std::memory_order_release);
}

void ReplayHeadPoseSource::PublishAll() noexcept {
    while (running_.load(std::memory_order_acquire) && next_index_ < rows_.size()) {
        PublishNext();
    }
}

bool ReplayHeadPoseSource::TryGetLatest(HeadSample &out, Duration predict) const noexcept {
    HeadSample newest{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
    if (!slot_.TryRead(newest)) {
        return false;
    }
    out = newest;

    const double yaw_offset_deg = yaw_offset_deg_.load(std::memory_order_relaxed);
    const std::int64_t capped_ns = std::clamp<std::int64_t>(predict.ns, 0, kMaxPredictNs);
    if (capped_ns > 0) {
        // Compose the recentre offset and the extrapolated delta onto the
        // recorded rotation (matching the Viture adapter), so a recorded roll
        // survives and the predicted pose does not snap at the yaw seam.
        const double dt_s = core_math::ToSeconds(capped_ns);
        const double yaw_delta_deg = yaw_offset_deg + yaw_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        const double pitch_delta_deg = pitch_rate_deg_per_s_.load(std::memory_order_relaxed) * dt_s;
        out.pose.rotation =
            core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_delta_deg * kDegreesToRadians) *
            out.pose.rotation *
            core_math::Quat::FromAxisAngle(core_math::Vec3{1.0, 0.0, 0.0}, pitch_delta_deg * kDegreesToRadians);
        out.time = newest.time + capped_ns;
    } else if (yaw_offset_deg != 0.0) {
        out.pose.rotation =
            core_math::Quat::FromAxisAngle(core_math::Vec3{0.0, 1.0, 0.0}, yaw_offset_deg * kDegreesToRadians) *
            out.pose.rotation;
    }
    return true;
}

Result<void> ReplayHeadPoseSource::Recenter() {
    if (!has_published_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "replay: no sample to recentre"});
    }
    yaw_offset_deg_.store(-latest_yaw_deg_.load(std::memory_order_relaxed), std::memory_order_relaxed);
    return Ok();
}

Result<void> ReplayHeadPoseSource::Fail(std::int64_t line_number, std::string reason) {
    error_message_ = "row " + std::to_string(line_number) + ": " + std::move(reason);
    return Err<void>(Status{StatusCode::InvalidArgument, error_message_.c_str()});
}

void ReplayHeadPoseSource::UpdateRate(const Row &row) noexcept {
    double yaw_deg = YawDegrees(row.pose);
    const double pitch_deg = PitchDegrees(row.pose);
    if (has_previous_ && row.host_time_ns > previous_time_) {
        // Keep the absolute yaw continuous across the +/-180 degree seam so
        // the rate never spikes and `latest_yaw_deg_` stays replay-local.
        const double delta_yaw_deg = detail::UnwrapYawDeltaDegrees(yaw_deg - previous_yaw_deg_);
        yaw_deg = previous_yaw_deg_ + delta_yaw_deg;
        const double dt_s = core_math::ToSeconds(row.host_time_ns - previous_time_);
        if (dt_s > 0.0) {
            yaw_rate_deg_per_s_.store(delta_yaw_deg / dt_s, std::memory_order_relaxed);
            pitch_rate_deg_per_s_.store((pitch_deg - previous_pitch_deg_) / dt_s, std::memory_order_relaxed);
        }
    }
    previous_yaw_deg_ = yaw_deg;
    previous_pitch_deg_ = pitch_deg;
    previous_time_ = row.host_time_ns;
    has_previous_ = true;
    latest_yaw_deg_.store(yaw_deg, std::memory_order_relaxed);
}

} // namespace cg::glasses
