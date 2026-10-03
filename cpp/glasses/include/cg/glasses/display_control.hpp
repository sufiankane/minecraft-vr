#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <utility>

#include "cg/glasses/viture_api.hpp"
#include "result.hpp"

namespace cg::glasses {

/// The display configuration selected through `IDisplayControl` (S5
/// deliverable): refresh rate in hertz plus the side-by-side stereo flag.
struct DisplayMode {
    std::uint32_t refresh_hz;
    bool sbs;
};

/// Display-mode control behind one small interface.
///
/// Configuration is only legal while the pose source is stopped: implementations
/// must report `NotReady` instead of touching the seam while a poll could be in
/// flight (the VITURE display calls are not safe concurrently with `PollPose`).
/// The exact VITURE display surface is U-08 (pending HIL, ADR-0009). The seam
/// can set a mode (`IVitureApi::SetDisplayMode`) and read the refresh rate
/// back (`IVitureApi::GetRefreshHz`), but has no SBS getter, so an
/// implementation must cache the SBS flag. `Stop` is idempotent and `noexcept`
/// and never calls the seam; after it returns, later `Get`/`Set` calls report
/// `NotReady` until the answers to U-08 pin real teardown semantics.
class IDisplayControl {
  public:
    virtual ~IDisplayControl() = default;

    /// The current display configuration, or the seam's failure.
    [[nodiscard]] virtual Result<DisplayMode> Get() const = 0;

    /// Applies `mode`.
    virtual Result<void> Set(DisplayMode mode) = 0;

    /// Latches the control stopped. Idempotent, `noexcept`, never calls the seam.
    virtual void Stop() noexcept = 0;
};

/// `IDisplayControl` over the isolated VITURE SDK seam.
///
/// `Set` forwards `{refresh_hz, sbs}` to `IVitureApi::SetDisplayMode` and
/// caches the SBS flag only when the seam succeeds, so a failed set cannot
/// poison the reported state. `Get` reads the refresh rate from
/// `IVitureApi::GetRefreshHz` (authoritative for this implementation) and
/// reports the cached SBS flag, which is explicitly **provisional pending
/// U-08/HIL**: the seam has no SBS getter, so a value set before this process
/// started, or by another controller, cannot be observed. Every seam failure
/// passes through unchanged, including `Unsupported` from a build without a
/// display API (the U-08 placeholder behaviour).
///
/// **Threading rule (enforced):** the seam's display calls must not run while
/// the pose-polling thread could call `PollPose`, so `Get`/`Set` take an
/// is-running predicate (production wires `VitureHeadPoseSource::Running`)
/// and return `NotReady` while it reports true. Configure the display before
/// `Start` or after `Stop`; an empty predicate means "never running" and is
/// for standalone/test use.
///
/// `Stop` latches the control (`stopped_`) but does not synchronise with a
/// concurrent `Set`: a `Set` that already passed the stopped/running checks can
/// still complete its seam call after `Stop` returns. That is deliberate —
/// `Stop` never joins callers and U-08 pins real teardown — and callers that
/// need strict quiescence must serialise `Set` against `Stop` themselves. The
/// cached SBS flag uses release/acquire (M-5) so the pair is not stale under
/// that race.
class VitureDisplayControl final : public IDisplayControl {
  public:
    using IsSourceRunning = std::function<bool()>;

    explicit VitureDisplayControl(IVitureApi &api, IsSourceRunning is_source_running = {}) noexcept
        : api_(api), is_source_running_(std::move(is_source_running)) {}

    [[nodiscard]] Result<DisplayMode> Get() const override;
    Result<void> Set(DisplayMode mode) override;
    void Stop() noexcept override;

  private:
    [[nodiscard]] bool SourceIsRunning() const;

    IVitureApi &api_;
    IsSourceRunning is_source_running_;
    mutable std::atomic<bool> sbs_{false};
    std::atomic<bool> stopped_{false};
};

} // namespace cg::glasses
