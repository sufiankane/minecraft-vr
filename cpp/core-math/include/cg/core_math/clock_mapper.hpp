#pragma once

#include <array>
#include <cstddef>

#include "cg/core_math/time.hpp"

namespace cg::core_math {

/// Estimates the offset between SDK seconds and the host timeline (ADR-0004).
///
/// `AddSample` records `offset = host_time / 1e9 - sdk_seconds` for the most
/// recent 32 samples in a fixed window. `OffsetSeconds` is the median of the
/// window's offsets (odd count: the middle value; even count: the mean of the
/// two middle values) computed from a sorted stack copy. `Map` applies that
/// median: `round((sdk_seconds + offset) * 1e9)` with halfway cases away from
/// zero via `std::llround`, the same rule as `ToNanoseconds`.
///
/// Defined behaviours:
/// - before any sample the offset is zero, so `Map(sdk) == ToNanoseconds(sdk)`;
/// - a non-finite `sdk_seconds` passed to `AddSample` is ignored;
/// - `Map` of a non-finite `sdk_seconds` returns 0 (never undefined);
/// - `IsReady` reports true from `kReadySampleCount` samples onward.
///
/// The window deliberately lags a step change in the offset by up to 16
/// samples; the median keeps the estimate robust to outlier callbacks.
/// `HostTime` samples are integral, so `AddSample` never rejects them.
///
/// Not thread-safe: callers must serialise access to one instance. Every
/// operation is `noexcept` and allocates nothing.
class ClockMapper {
  public:
    /// Number of `(sdk_seconds, host_time)` samples retained (ADR-0004).
    static constexpr std::size_t kWindowSize = 32;

    /// Minimum held samples for `IsReady()` (ADR-0004).
    static constexpr std::size_t kReadySampleCount = 8;

    /// Records one `(sdk_seconds, host_time)` pair as its offset in seconds.
    ///
    /// Non-finite `sdk_seconds` values are ignored: they neither enter the
    /// window nor change `SampleCount`. Once the window is full the oldest
    /// sample is evicted. No allocation.
    void AddSample(double sdk_seconds, HostTime host_time) noexcept;

    /// Median offset in seconds of the samples currently held; 0 before any
    /// sample. Odd count takes the middle value of the sorted window, even
    /// count the mean of the two middle values. No allocation.
    [[nodiscard]] double OffsetSeconds() const noexcept;

    /// Maps an SDK instant to the host timeline:
    /// `round((sdk_seconds + OffsetSeconds()) * 1e9)`, rounding halfway cases
    /// away from zero (`std::llround`).
    ///
    /// A non-finite `sdk_seconds` returns 0. A finite input whose result falls
    /// outside `HostTime` saturates at `HostTime::min()/max()` instead of
    /// invoking `llround` on an unrepresentable value (M-11). No allocation.
    [[nodiscard]] HostTime Map(double sdk_seconds) const noexcept;

    /// True once `SampleCount() >= kReadySampleCount`. No allocation.
    [[nodiscard]] bool IsReady() const noexcept;

    /// Number of samples currently held, 0 to `kWindowSize`. No allocation.
    [[nodiscard]] std::size_t SampleCount() const noexcept;

  private:
    std::array<double, kWindowSize> offsets_{};
    std::size_t next_sample_ = 0;
    std::size_t sample_count_ = 0;
};

} // namespace cg::core_math
