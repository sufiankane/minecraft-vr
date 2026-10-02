#pragma once

#include <cstdint>

#include "cg/glasses/display_control.hpp"

namespace cg::glasses::test {

/// Programmable `IDisplayControl` double for adapter and integration tests.
///
/// Units: `state` is the accepted display mode, `get_result`/`set_result`
/// inject seam failures, and the call counters expose how often the interface
/// was exercised. `Stop` latches like `VitureDisplayControl`; a latched,
/// stopped fake reports `NotReady` for later calls.
class FakeDisplayControl final : public IDisplayControl {
  public:
    DisplayMode state{90, false};
    Result<void> get_result = Ok();
    Result<void> set_result = Ok();
    mutable std::uint64_t get_calls = 0;
    std::uint64_t set_calls = 0;
    std::uint64_t stop_calls = 0;

    [[nodiscard]] Result<DisplayMode> Get() const override {
        ++get_calls;
        if (!get_result.ok()) {
            return Err<DisplayMode>(get_result.status());
        }
        if (stopped_) {
            return Err<DisplayMode>(Status{StatusCode::NotReady, "fake display control stopped"});
        }
        return Ok(state);
    }

    Result<void> Set(DisplayMode mode) override {
        ++set_calls;
        if (!set_result.ok()) {
            return Err<void>(set_result.status());
        }
        if (stopped_) {
            return Err<void>(Status{StatusCode::NotReady, "fake display control stopped"});
        }
        state = mode;
        return Ok();
    }

    void Stop() noexcept override {
        ++stop_calls;
        stopped_ = true;
    }

    [[nodiscard]] bool stopped() const noexcept { return stopped_; }

  private:
    bool stopped_ = false;
};

} // namespace cg::glasses::test
