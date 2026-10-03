#include "cg/glasses/display_control.hpp"

#include <cstdint>

namespace cg::glasses {

namespace {

constexpr const char *kRunningMessage = "display control: the pose source is running";

} // namespace

bool VitureDisplayControl::SourceIsRunning() const {
    // `std::function::operator()` is const; the predicate is set once at
    // construction and read by whichever thread calls Get/Set.
    return is_source_running_ && is_source_running_();
}

Result<DisplayMode> VitureDisplayControl::Get() const {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<DisplayMode>(Status{StatusCode::NotReady, "display control stopped"});
    }
    if (SourceIsRunning()) {
        return Err<DisplayMode>(Status{StatusCode::NotReady, kRunningMessage});
    }
    const Result<std::uint32_t> refresh_hz = api_.GetRefreshHz();
    if (!refresh_hz.ok()) {
        return Err<DisplayMode>(refresh_hz.status());
    }
    // Acquire pairs with the release store in `Set`: a reader that observes a
    // cached SBS value ordered after the `stopped_` acquire in a later `Get`
    // cannot observe a pre-`Stop` SBS store out of order (M-5).
    return Ok(DisplayMode{*refresh_hz, sbs_.load(std::memory_order_acquire)});
}

Result<void> VitureDisplayControl::Set(DisplayMode mode) {
    if (stopped_.load(std::memory_order_acquire)) {
        return Err<void>(Status{StatusCode::NotReady, "display control stopped"});
    }
    if (SourceIsRunning()) {
        return Err<void>(Status{StatusCode::NotReady, kRunningMessage});
    }
    const Result<void> result = api_.SetDisplayMode(mode.refresh_hz, mode.sbs);
    if (result.ok()) {
        sbs_.store(mode.sbs, std::memory_order_release);
    }
    return result;
}

void VitureDisplayControl::Stop() noexcept { stopped_.store(true, std::memory_order_release); }

} // namespace cg::glasses
