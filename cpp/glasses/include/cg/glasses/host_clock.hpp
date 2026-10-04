#pragma once

#include <chrono>

#include "cg/glasses/manual_clock.hpp"
#include "ports.hpp"

namespace cg::glasses {

/// The wrapper's view of time: monotonic nanoseconds on the host timeline
/// (ADR-0004). Injected so tests can drive the polling loop deterministically.
class IHostClock {
  public:
    virtual ~IHostClock() = default;
    IHostClock(const IHostClock &) = delete;
    IHostClock &operator=(const IHostClock &) = delete;
    IHostClock(IHostClock &&) = delete;
    IHostClock &operator=(IHostClock &&) = delete;

  protected:
    IHostClock() = default;

  public:
    /// The current instant. No allocation, no exceptions.
    [[nodiscard]] virtual HostTime Now() const noexcept = 0;
};

/// Production clock over `std::chrono::steady_clock`.
class SteadyHostClock final : public IHostClock {
  public:
    [[nodiscard]] HostTime Now() const noexcept override {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count();
    }
};

/// Test clock that only moves when the test calls `Advance`, so the polling
/// loop sees deterministic time.
class ManualHostClock final : public IHostClock {
  public:
    explicit ManualHostClock(HostTime now = 0) noexcept : clock_(now) {}

    [[nodiscard]] HostTime Now() const noexcept override { return clock_.Now(); }

    /// Moves the clock forward by `delta` (a negative delta is caller error).
    void Advance(Duration delta) noexcept { clock_.Advance(delta); }

  private:
    ManualClock clock_;
};

} // namespace cg::glasses
