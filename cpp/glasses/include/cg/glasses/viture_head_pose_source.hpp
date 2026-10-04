#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>

#include "cg/core_math/clock_mapper.hpp"
#include "cg/glasses/device_gate.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "cg/glasses/recentre_state.hpp"
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
/// Each sample is recorded with `AddSample` before it is mapped, so the first
/// published sample is anchored to its host arrival instant and the SDK's own
/// epoch never reaches a consumer (CXX-06); the same seeding re-anchors after
/// a mapped-time regression reset.
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
/// 100 ms, without locks or allocation. Prediction applies only while the
/// newest sample is `Stable`: a quiet `Unstable`/`Lost` synthetic has no new
/// samples to extrapolate, so it is returned unchanged (still carrying an
/// armed recentre correction, CXX-12).
///
/// `Recenter` is serviced by the polling thread (the only thread allowed to
/// call the API): it captures the newest slot sample, posts the request and
/// returns `Ok` as soon as the request is posted, without waiting for the
/// polling thread. `Ok` therefore means "posted", not "applied": a `Stop`
/// that races or follows the post withdraws an unresolved request before the
/// seam is called (CXX-14), and the stream then stays in its never-reset
/// frame; only a request the polling thread resolved before `Stop` keeps its
/// correction. It additionally arms the inverse-yaw read-time correction
/// for the captured sample immediately, so the newest pre-reset sample reads
/// recentred as soon as `Recenter` returns; the polling thread applies
/// `ResetOriginCarina` between polls on its next pass, and later samples come
/// from the already-recentred SDK stream. It is `NotReady` before the first
/// sample and while no device is alive (during a backoff or a failed
/// recreate); the destroyed-device state never calls the seam. A request that
/// finds the device dead (or a recreated device that has not published yet)
/// stays pending with its correction armed and is applied exactly once on the
/// next successful recreate+publish; a request that can never be applied is
/// withdrawn by `Stop`, so the seam is never called after `Stop` and the
/// stream then reflects the never-reset SDK frame. A post that arrives while
/// another is in flight supersedes it and owns the correction. A failed
/// `ResetOriginCarina` withdraws the correction in the polling thread.
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

    /// Runs `action` while the source is stopped, holding `lifecycle_mutex_`
    /// for its duration: no `Start`/`Stop` transition can interleave and no
    /// poll can be in flight (the polling thread exists only while `Running()`).
    /// Returns `NotReady` while `Running()`. This is the production gate the
    /// display control binds (`DeviceGate`), closing the is-running TOCTOU
    /// against `Start` (CXX-01).
    [[nodiscard]] Result<void> WithDeviceStopped(const DisplaySeamAction &action);

    /// True while the polling thread is alive (between a successful `Start`
    /// and the join in `Stop`). This remains the display control's fast
    /// "refuse now" predicate, but correctness comes from
    /// `WithDeviceStopped`: a bare predicate check would race `Start` between
    /// the check and the seam call, so the display control no longer uses
    /// `Running()` as its guard.
    [[nodiscard]] bool Running() const noexcept { return running_.load(std::memory_order_acquire); }

    /// Diagnostics only (HIL recordings), NOT part of `IHeadPoseSource`: the
    /// SDK seconds stamp of the newest published sample, or `std::nullopt`
    /// before the first successful poll. The polling thread stores it just
    /// before the matching `PoseSlot::Publish`, so a reader that observes a
    /// sample can rely on the stamp being at least as new. Lock-free (the
    /// `std::atomic<double>` is lock-free on every supported platform), no
    /// allocation, no exceptions. Quiet `Unstable`/`Lost` synthetics carry the
    /// last real stamp forward rather than clearing it.
    [[nodiscard]] std::optional<double> LastSdkSeconds() const noexcept;

    /// Test-only seam invoked by `Recenter` after the target capture and before
    /// the arming transaction. It deliberately runs *outside*
    /// `recentre_mutex_`: a test parks the poster there while a failing
    /// resolution completes — the exact window CXX-02 closed. On the fixed
    /// code the arm is wholly inside the mutex afterwards, so the fresh post
    /// survives; on the old code the arm stores had already happened outside,
    /// and the failing resolution clobbered them. Arm it only while no
    /// `Recenter` is in flight and clear it afterwards; production leaves it
    /// unset and pays one predictable null check.
    using TestRecentreArmHook = void (*)(void *) noexcept;
    static void SetTestRecentreArmHook(TestRecentreArmHook hook, void *context) noexcept {
        test_recentre_arm_hook_ = hook;
        test_recentre_arm_context_ = context;
    }

  private:
    void PollLoop(std::stop_token stop) noexcept;
    void SetupThread() noexcept;

    /// Polling-thread half of `Recenter`: consumes a posted request and calls
    /// `ResetOriginCarina` with the newest raw slot pose, then extends the
    /// read-time correction over every sample published before this pass. Only
    /// resolves `generation` if no newer post superseded it; a superseded
    /// request is dropped without a seam call, so every request is resolved or
    /// withdrawn exactly once and a successful request calls the seam exactly
    /// once (TD-005, pinned by `VitureFault.RecentreWorksAfterSuccessfulRecreate`).
    void ServiceRecentre() noexcept;
    void HandleRecentre(std::uint64_t generation) noexcept;

    /// Drops an unresolved request and its armed correction (`Stop` only).
    void WithdrawPendingRecentre() noexcept;

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

    // Recentre hand-off from any caller thread to the polling thread. The
    // poster captures a pose from the slot, arms the read-time correction and
    // then posts a generation under `recentre_mutex_`; the polling thread
    // claims the post under the same mutex (so a claim and a post cannot
    // interleave) and resolves that generation unless a newer post superseded
    // it. `recentre_call_mutex_` serialises callers.
    std::mutex recentre_call_mutex_;
    std::mutex recentre_mutex_;
    std::atomic<bool> recentre_requested_{false};
    std::uint64_t recentre_generation_ = 0; // Guarded by `recentre_mutex_`.

    // Device lifetime (create success sets it, before every destroy it is
    // cleared) and "a publish has started" (readable by `Recenter` from any
    // caller thread, so both are atomics). The publish flag distinguishes
    // "no sample yet" from a saturated slot writer, whose bounded read can
    // fail while a sample is being written.
    std::atomic<bool> device_alive_{false};
    std::atomic<bool> has_published_{false};

    // Polling-thread state (never touched by readers).
    std::optional<HeadSample> last_published_{};
    // Strictly increasing across reconnects, restarts and recentres: it is
    // never reset, so a consumer that keeps the newest seq cannot see it go
    // backwards or repeat within a wrap period (TD-005, pinned by
    // `VitureFault.SequenceStaysMonotonicAcrossReconnect`). The contract type
    // is `std::uint32_t`, so it wraps after 2^32 samples (~99 days at 500 Hz).
    std::uint32_t seq_ = 0;
    HostTime last_success_ns_ = 0;
    TrackState quiet_state_ = TrackState::Stable;
    bool have_previous_ = false;
    double previous_yaw_deg_ = 0.0;
    double previous_pitch_deg_ = 0.0;
    HostTime previous_time_ = 0;
    // True once the device session current at this instant has published. A
    // recreate clears it, so a pending recentre waits for a fresh sample of the
    // recreated session instead of applying to the pre-loss pose.
    bool session_published_ = false;

    // Reader-visible state. The correction applies to samples up to
    // `RecentreState::Value::until_seq`, or to every sample while `pending` is
    // set (a posted request is armed but not yet resolved against the SDK).
    // All three fields are published as one seqlock snapshot (CXX-13), so a
    // reader can never combine a newer offset with an older `until_seq` (the
    // old loose reads let two overlapping recentres glitch one frame).
    std::atomic<double> yaw_rate_deg_per_s_{0.0};
    std::atomic<double> pitch_rate_deg_per_s_{0.0};
    RecentreState recentre_state_{};

    // Diagnostics-only SDK stamp (see `LastSdkSeconds`). Written by the polling
    // thread before the matching publish; `has_sdk_seconds_` is the release
    // flag that makes it readable.
    std::atomic<double> last_sdk_seconds_{0.0};
    std::atomic<bool> has_sdk_seconds_{false};

    // Test-only arming seam (see `SetTestRecentreArmHook`); null unless armed.
    // The poster reads it while holding `recentre_mutex_`.
    inline static TestRecentreArmHook test_recentre_arm_hook_ = nullptr;
    inline static void *test_recentre_arm_context_ = nullptr;
};

} // namespace cg::glasses
