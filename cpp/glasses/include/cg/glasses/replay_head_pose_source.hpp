#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "cg/glasses/manual_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "ports.hpp"

namespace cg::glasses {

/// Deterministic `IHeadPoseSource` that replays a recorded CSV dataset.
///
/// The constructor only remembers `csv` and `clock`; file IO happens in
/// `Load()`, which parses the header
/// `host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status` and every data row:
///
/// - `host_time_ns` is the published sample time (the replay clock is advanced
///   to it) and must increase strictly from row to row.
/// - `sdk_time_s` is parsed and checked finite but not published: the CSV is
///   already a recording on the host timeline.
/// - the position must be finite; the quaternion must be finite and is
///   normalised through `core_math::Quat::FromComponents` (a non-unit row is
///   accepted, a degenerate row becomes identity).
/// - `status` is `stable|unstable|lost`, case-insensitively.
///
/// Any malformed row fails `Load()` with `StatusCode::InvalidArgument` and a
/// message naming the 1-based physical line (`row 1` is the header). The
/// message text lives in the source until the next `Load()`.
///
/// Playback preserves CSV order and timing: `PublishNext()` advances the clock
/// to the next row's `host_time_ns` and then publishes that row with the next
/// sequence number; `PublishAll()` publishes every remaining row. Both are
/// no-ops while stopped or when the dataset is exhausted. `Start` requires a
/// successful `Load` (`NotReady` otherwise) and resumes at the next unpublish
/// row, so sequence and time stay strictly increasing across stop and start.
/// `Stop` is idempotent and total; the newest published sample stays readable.
///
/// `TryGetLatest` copies the wait-free slot and applies the same prediction and
/// recentre rules as `FakeHeadPoseSource`: a positive `predict` extrapolates
/// yaw and pitch linearly from the last row-to-row rate (the yaw delta is
/// unwrapped across the +/-180 degree seam), capped at 100 ms, and composes
/// that delta onto the recorded rotation so recorded roll survives; the
/// position is unchanged. `Recenter` records the latest row's yaw as an
/// inverse-yaw offset applied at read time (pitch and roll untouched) and is
/// `NotReady` before the first published row. One writer
/// (`PublishNext`/`PublishAll`) and any number of readers; `Load` must not run
/// concurrently with `Start` or publishing.
class ReplayHeadPoseSource final : public IHeadPoseSource {
  public:
    ReplayHeadPoseSource(std::filesystem::path csv, ManualClock &clock);

    ~ReplayHeadPoseSource() override = default;
    ReplayHeadPoseSource(const ReplayHeadPoseSource &) = delete;
    ReplayHeadPoseSource &operator=(const ReplayHeadPoseSource &) = delete;
    ReplayHeadPoseSource(ReplayHeadPoseSource &&) = delete;
    ReplayHeadPoseSource &operator=(ReplayHeadPoseSource &&) = delete;

    /// Parses the whole CSV into memory, replacing any previous dataset and
    /// clearing its playback state: the slot is empty again (`TryGetLatest`
    /// false), the sequence restarts at 1, and cached prediction rates and the
    /// recentre offset are cleared. Must succeed before `Start`; safe to call
    /// again between playbacks.
    Result<void> Load();

    Result<void> Start() override;
    void Stop() noexcept override;
    [[nodiscard]] bool TryGetLatest(HeadSample &out, Duration predict) const noexcept override;
    Result<void> Recenter() override;

    /// Publishes the next row (clock advanced to its `host_time_ns`). A no-op
    /// while stopped or once every row has been published.
    void PublishNext() noexcept;

    /// Publishes every remaining row in order; a no-op while stopped.
    void PublishAll() noexcept;

    /// Rows parsed by the last successful `Load()`; zero before it.
    [[nodiscard]] std::size_t row_count() const noexcept { return rows_.size(); }

    /// Index of the next row `PublishNext()` will publish.
    [[nodiscard]] std::size_t next_row_index() const noexcept { return next_index_; }

  private:
    struct Row {
        HostTime host_time_ns;
        core_math::Pose pose;
        TrackState state;
    };

    /// Sets `error_message_` to `row <line>: <reason>` and returns the
    /// `InvalidArgument` result carrying that text.
    Result<void> Fail(std::int64_t line_number, std::string reason);

    void UpdateRate(const Row &row) noexcept;

    std::filesystem::path csv_;
    ManualClock &clock_;
    PoseSlot slot_;
    std::atomic<bool> running_{false};
    bool loaded_ = false;
    std::vector<Row> rows_;
    std::size_t next_index_ = 0;
    std::uint32_t next_seq_ = 1;

    // Writer-thread playback state.
    // `Recenter` may be called from a reader thread while the writer emits, so
    // the "a sample exists" signal is an acquire/release atomic.
    std::atomic<bool> has_published_{false};
    bool has_previous_ = false;
    double previous_yaw_deg_ = 0.0;
    double previous_pitch_deg_ = 0.0;
    HostTime previous_time_ = 0;

    // Reader-visible state (atomics, like `FakeHeadPoseSource`).
    std::atomic<double> latest_yaw_deg_{0.0};
    std::atomic<double> yaw_rate_deg_per_s_{0.0};
    std::atomic<double> pitch_rate_deg_per_s_{0.0};
    std::atomic<double> yaw_offset_deg_{0.0};

    std::string error_message_;
};

} // namespace cg::glasses
