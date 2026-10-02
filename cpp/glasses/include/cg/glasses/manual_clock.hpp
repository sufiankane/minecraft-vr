#pragma once

#include "cg/core_math/time.hpp"
#include "ports.hpp"

namespace cg::glasses {

/// Deterministic, test-driven replacement for the host clock.
///
/// The clock starts at `now` (zero by default) and only moves forward when the
/// test calls `Advance`. No allocation, no exceptions; not thread-safe, callers
/// serialise access.
class ManualClock {
  public:
    explicit ManualClock(core_math::HostTime now = 0) noexcept : now_(now) {}

    /// The current instant on the host timeline. No allocation.
    [[nodiscard]] core_math::HostTime Now() const noexcept { return now_; }

    /// Moves the clock forward by `delta` (a negative delta is caller error).
    void Advance(Duration delta) noexcept { now_ += delta.ns; }

  private:
    core_math::HostTime now_ = 0;
};

} // namespace cg::glasses
