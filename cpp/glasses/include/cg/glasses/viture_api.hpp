#pragma once

#include <cstdint>
#include <string>

#include "cg_types.h"
#include "result.hpp"

namespace cg::glasses {

/// Upper bound on how long a conforming `IVitureApi::PollPose` blocks before
/// returning `StatusCode::Timeout` when no sample is available. A poll must
/// return sooner when `RequestStop()` was called. 100 ms spans four 25 Hz
/// sample periods (dossier F-05), so a healthy poll never reaches the cap.
inline constexpr std::int64_t kViturePollTimeoutNs = 100'000'000;

/// The seam between `cg-glasses` and the VITURE SDK (ADR-0001, ADR-0009).
///
/// Every vendor call in the repository goes through this interface; no other
/// translation unit knows a vendor symbol or includes a vendor header. The
/// real implementation is constructed by `viture_loader.cpp`; tests drive
/// `FakeVitureApi`.
///
/// Threading contract:
/// - `CreateDevice`, `StartPose` and `DestroyDevice` are lifecycle calls. The
///   wrapper calls them on its caller thread in `Start`/`Stop` and on the
///   polling thread while reconnecting; they are serialised with the polling
///   loop and with each other, never concurrent with a poll.
/// - `PollPose` and `ResetOriginCarina` are called on the polling thread only,
///   one at a time in the loop. `ResetOriginCarina` requires a live device
///   (after a successful `CreateDevice`/`StartPose`) and reports `NotReady`
///   otherwise.
/// - `SetDisplayMode`/`GetRefreshHz` are display calls. They only run while
///   the pose source is stopped (configure before `Start`/after `Stop`):
///   `VitureDisplayControl` enforces this with an is-running predicate and
///   reports `NotReady` while a poll could be in flight, so they are never
///   concurrent with `PollPose`.
/// - `SdkVersion` is diagnostic and may be called from any thread.
/// - `RequestStop()` is the one cross-thread call: the thread asking the
///   polling thread to finish (the wrapper's `Stop()`) sets it before
///   joining. It is thread-safe, idempotent and `noexcept`, and a blocked
///   `PollPose` must return promptly once it is set, reporting
///   `StatusCode::Timeout` for the interrupted poll.
/// - `StartPose()` clears a previously requested stop so a stopped session
///   can be restarted.
/// - No method throws; every failure is a `Status`.
class IVitureApi {
  public:
    virtual ~IVitureApi() = default;

    /// Creates and initialises the device, selecting 3DoF (dossier F-03).
    /// A second call without an intervening `DestroyDevice` is caller error.
    virtual Result<void> CreateDevice() = 0;

    /// Releases the device. Idempotent, `noexcept`, and safe after a failed
    /// `CreateDevice`.
    virtual void DestroyDevice() noexcept = 0;

    /// Starts pose delivery and clears a stop requested earlier. Idempotent
    /// while running.
    virtual Result<void> StartPose() = 0;

    /// Returns the newest pose sample.
    ///
    /// May block for up to `kViturePollTimeoutNs` while no sample is
    /// available, and returns `StatusCode::Timeout` when the wait elapses or
    /// a stop was requested.
    ///
    /// `sample.host_time` is a legacy C field name inherited from the dossier
    /// 5.2 struct; it is **not** an instant on the host timeline. It carries
    /// the SDK's monotonic timestamp encoded in nanoseconds (F-05 counts SDK
    /// timestamps in seconds). The wrapper converts it once with `ToSeconds`
    /// and feeds the seconds value to `ClockMapper.AddSample(seconds,
    /// clock.Now())`, publishing `ClockMapper.Map(seconds)`; treating the raw
    /// field as an existing HostTime would double-map it. `sample.sequence` is
    /// the implementation's monotonic counter; the wrapper owns the sequence
    /// it publishes.
    virtual Result<cg_head_sample> PollPose() = 0;

    /// Makes `pose`, layout `[px, py, pz, qw, qx, qy, qz]`, the new origin:
    /// position and yaw take the given pose, pitch and roll stay
    /// gravity-anchored (dossier F-04). Requires a live device and reports
    /// `NotReady` when the device was destroyed or never created.
    virtual Result<void> ResetOriginCarina(const float pose[7]) = 0;

    /// Selects side-by-side display at `refresh_hz`. The exact vendor
    /// semantics are U-08 (pending HIL, ADR-0009).
    virtual Result<void> SetDisplayMode(std::uint32_t refresh_hz, bool sbs) = 0;

    /// The current display refresh rate in hertz. The exact vendor semantics
    /// are U-08 (pending HIL, ADR-0009).
    virtual Result<std::uint32_t> GetRefreshHz() = 0;

    /// The vendor SDK version string, for diagnostics.
    [[nodiscard]] virtual std::string SdkVersion() const = 0;

    /// Asks a blocked `PollPose` to return promptly and marks the session for
    /// shutdown. Thread-safe, idempotent, `noexcept`; cleared by `StartPose`.
    virtual void RequestStop() noexcept = 0;
};

} // namespace cg::glasses
