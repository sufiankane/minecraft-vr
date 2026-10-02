#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>

#include "cg/core_math/clock_mapper.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "cg/glasses/viture_api.hpp"
#include "ports.hpp"

namespace cg::glasses {

/// The real `IHeadPoseSource`: one polling thread over the injected `IVitureApi`.
///
/// `Start` creates and starts the device first, so a failed start returns the
/// SDK's error synchronously and leaves the object ready for a clean retry,
/// then launches the polling thread. The thread polls the SDK, feeds the
/// per-sample SDK stamp (nanoseconds on the SDK's monotonic second-based
/// timeline, dossier F-05) through `ClockMapper` together with the injected
/// `IHostClock`, converts the pose with `core_math::PoseFromSdk` and publishes
/// `{mapped host time, pose, state, ++seq}` into a wait-free `PoseSlot`.
///
/// Fault policy:
/// - a quiet feed (no successful poll) is reported as `Unstable` after 500 ms
///   and `Lost` after 1000 ms, carrying the last pose and the host time;
/// - `Device`/`Timeout` (or any other poll failure) tears the device down and
///   reconnects on an interruptible backoff: 100 ms doubling to 2 s per
///   consecutive failure, at most `kMaxReconnectAttempts` recreate attempts,
///   after which the thread probes every 2 s and publishes `Lost` until a poll
///   succeeds.
///
/// `Stop` is idempotent and total: it sets the API stop flag, interrupts a
/// blocked poll and a pending backoff, joins, destroys the device and keeps
/// the newest sample readable. `TryGetLatest` is wait-free: it copies the slot
/// and extrapolates yaw/pitch by the last inter-sample rate, capped at
/// 100 ms, without locks or allocation.
///
/// `Recenter` is serviced by the polling thread (the only thread allowed to
/// call the API): it passes the newest pose to `ResetOriginCarina` and arms an
/// inverse-yaw read-time correction for samples published before the reset, so
/// the newest sample reads back recentred as soon as `Recenter` returns, while
/// later samples come from the already-recentred SDK stream.
class VitureHeadPoseSource final : public IHeadPoseSource {
  public:
    /// Runs on the polling thread right after it is named (Windows
    /// `SetThreadDescription`); production passes a priority raiser, tests a
    /// probe. The default is a no-op and a throwing hook is swallowed.
    using ThreadSetupHook = std::function<void()>;

    /// First reconnect backoff (100 ms).
    static constexpr Duration kInitialBackoff{100'000'000};
    /// Maximum reconnect backoff (2 s).
    static constexpr Duration kMaxBackoff{2'000'000'000};
    /// Consecutive recreate attempts before the thread stops recreating.
    static constexpr int kMaxReconnectAttempts = 10;
    /// Quiet time after which the source publishes `Unstable`.
    static constexpr Duration kUnstableAfter{500'000'000};
    /// Quiet time after which the source publishes `Lost`.
    static constexpr Duration kLostAfter{1'000'000'000};
    /// How long the polling thread waits for a reader after a recentre.
    static constexpr Duration kRecentreReadTimeout{1'000'000'000};
    /// Prediction horizon cap (100 ms).
    static constexpr std::int64_t kMaxPredictNs = 100'000'000;

    VitureHeadPoseSource(IVitureApi &api, IHostClock &clock, ThreadSetupHook setup_hook = {});
    ~VitureHeadPoseSource() override;

    VitureHeadPoseSource(const VitureHeadPoseSource &) = delete;
    VitureHeadPoseSource &operator=(const VitureHeadPoseSource &) = delete;

    Result<void> Start() override;
    void Stop() noexcept override;
    [[nodiscard]] bool TryGetLatest(HeadSample &out, Duration predict) const noexcept override;
    Result<void> Recenter() override;

  private:
    void PollLoop(std::stop_token stop) noexcept;
    void SetupThread() noexcept;
    void ServiceRecentre(std::stop_token stop) noexcept;
    void HandleRecentre() noexcept;
    void WaitForRecentreRead(std::stop_token stop) noexcept;
    [[nodiscard]] bool WaitBackoff(Duration duration, std::stop_token stop) noexcept;
    [[nodiscard]] bool StopRequested(const std::stop_token &stop) const noexcept;
    void PublishQuiet(HostTime now) noexcept;
    void UpdateRate(const HeadSample &sample) noexcept;

    IVitureApi &api_;
    IHostClock &clock_;
    ThreadSetupHook setup_hook_;
    core_math::ClockMapper mapper_{};
    PoseSlot slot_{};

    mutable std::mutex lifecycle_mutex_;
    std::jthread thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> stop_requested_{false};

    std::mutex recentre_call_mutex_;
    std::mutex recentre_mutex_;
    std::condition_variable recentre_cv_;
    std::atomic<bool> recentre_requested_{false};
    bool recentre_done_ = false;
    Result<void> recentre_result_{};

    // Polling-thread state (never touched by readers).
    std::optional<HeadSample> last_published_{};
    std::uint32_t seq_ = 0;
    HostTime last_success_ns_ = 0;
    TrackState quiet_state_ = TrackState::Stable;
    bool have_previous_ = false;
    double previous_yaw_deg_ = 0.0;
    double previous_pitch_deg_ = 0.0;
    HostTime previous_time_ = 0;

    // Reader-visible state. The hold is cleared by `TryGetLatest` (const), so
    // it is mutable.
    std::atomic<double> yaw_rate_deg_per_s_{0.0};
    std::atomic<double> pitch_rate_deg_per_s_{0.0};
    std::atomic<double> yaw_offset_deg_{0.0};
    std::atomic<std::uint32_t> recentre_until_seq_{0};
    mutable std::atomic<bool> recentre_hold_{false};
};

} // namespace cg::glasses
