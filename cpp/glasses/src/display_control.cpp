#include "cg/glasses/display_control.hpp"

#include <cstdint>

namespace cg::glasses {

namespace {

constexpr const char *kStoppedMessage = "display control stopped";
constexpr const char *kGateDidNotRunMessage = "display control: the device gate did not run the action";

} // namespace

Result<void> VitureDisplayControl::RunGated(const DisplaySeamAction &action) const {
    // An unbound gate is standalone/test use: the caller owns the exclusivity
    // of the seam. A bound gate (production: `WithDeviceStopped`) either runs
    // the action while the device is provably stopped or refuses it.
    if (!device_gate_) {
        return action();
    }
    return device_gate_(action);
}

Result<DisplayMode> VitureDisplayControl::Get() const {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<DisplayMode>(Status{StatusCode::NotReady, kStoppedMessage});
    }
    // The refresh rate is read inside the gate; the cached SBS flag is read
    // outside it (it is this class's own state, not seam state). Acquire pairs
    // with the release store in `Set`: a reader that observes a cached SBS
    // value ordered after the `stopped_` acquire in a later `Get` cannot
    // observe a pre-`Stop` SBS store out of order (M-5).
    Result<std::uint32_t> refresh_hz = Err<std::uint32_t>(Status{StatusCode::Internal, kGateDidNotRunMessage});
    const Result<void> gated = RunGated([this, &refresh_hz]() -> Result<void> {
        refresh_hz = api_.GetRefreshHz();
        return refresh_hz.ok() ? Ok() : Err<void>(refresh_hz.status());
    });
    if (!gated.ok()) {
        return Err<DisplayMode>(gated.status());
    }
    if (!refresh_hz.ok()) {
        return Err<DisplayMode>(refresh_hz.status());
    }
    return Ok(DisplayMode{*refresh_hz, sbs_.load(std::memory_order_acquire)});
}

Result<void> VitureDisplayControl::Set(DisplayMode mode) {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, kStoppedMessage});
    }
    Result<void> result = Err<void>(Status{StatusCode::Internal, kGateDidNotRunMessage});
    const Result<void> gated = RunGated([this, mode, &result]() -> Result<void> {
        result = api_.SetDisplayMode(mode.refresh_hz, mode.sbs);
        return result;
    });
    if (!gated.ok()) {
        return gated;
    }
    if (result.ok()) {
        sbs_.store(mode.sbs, std::memory_order_release);
    }
    return result;
}

void VitureDisplayControl::Stop() noexcept { stopped_.store(true, std::memory_order_release); }

} // namespace cg::glasses
