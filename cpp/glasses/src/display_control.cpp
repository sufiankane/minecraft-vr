#include "cg/glasses/display_control.hpp"

#include <cstdint>

namespace cg::glasses {

Result<DisplayMode> VitureDisplayControl::Get() const {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<DisplayMode>(Status{StatusCode::NotReady, "display control stopped"});
    }
    const Result<std::uint32_t> refresh_hz = api_.GetRefreshHz();
    if (!refresh_hz.ok()) {
        return Err<DisplayMode>(refresh_hz.status());
    }
    return Ok(DisplayMode{*refresh_hz, sbs_.load(std::memory_order_relaxed)});
}

Result<void> VitureDisplayControl::Set(DisplayMode mode) {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "display control stopped"});
    }
    const Result<void> result = api_.SetDisplayMode(mode.refresh_hz, mode.sbs);
    if (result.ok()) {
        sbs_.store(mode.sbs, std::memory_order_relaxed);
    }
    return result;
}

void VitureDisplayControl::Stop() noexcept { stopped_.store(true, std::memory_order_release); }

} // namespace cg::glasses
