#pragma once

#include <atomic>
#include <cstdint>

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
class VitureDisplayControl final : public IDisplayControl {
  public:
    explicit VitureDisplayControl(IVitureApi &api) noexcept : api_(api) {}

    [[nodiscard]] Result<DisplayMode> Get() const override;
    Result<void> Set(DisplayMode mode) override;
    void Stop() noexcept override;

  private:
    IVitureApi &api_;
    mutable std::atomic<bool> sbs_{false};
    std::atomic<bool> stopped_{false};
};

} // namespace cg::glasses
