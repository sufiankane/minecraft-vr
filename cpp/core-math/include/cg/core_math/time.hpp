#pragma once

#include <cmath>
#include <cstdint>
#include <limits>

namespace cg::core_math {

/// Nanoseconds on the one monotonic host timeline (ADR-0004).
using HostTime = std::int64_t;

/// Nanoseconds in one second.
inline constexpr HostTime kNanosecondsPerSecond = 1'000'000'000;

/// Converts `seconds` to nanoseconds, rounding to the nearest nanosecond with
/// halfway cases away from zero (`llround`), the same rule `ClockMapper` uses.
///
/// Total for every `double`: NaN maps to 0, and a value whose nanosecond
/// result is outside the `HostTime` range (a hostile finite input or an
/// infinity) saturates at `HostTime::min()/max()` instead of relying on
/// `std::llround`'s unrepresentable-result behaviour (M-11). No allocation.
[[nodiscard]] inline HostTime ToNanoseconds(double seconds) noexcept {
    if (std::isnan(seconds)) {
        return 0;
    }
    // 2^63: the smallest magnitude a double cannot round into HostTime.
    constexpr double kMaxNanos = static_cast<double>(std::numeric_limits<HostTime>::max());
    constexpr double kMinNanos = static_cast<double>(std::numeric_limits<HostTime>::min());
    const double nanos = seconds * static_cast<double>(kNanosecondsPerSecond);
    if (nanos >= kMaxNanos) {
        return std::numeric_limits<HostTime>::max();
    }
    if (nanos <= kMinNanos) {
        return std::numeric_limits<HostTime>::min();
    }
    return static_cast<HostTime>(std::llround(nanos));
}

/// Converts nanoseconds on the host timeline to seconds. No allocation.
[[nodiscard]] inline double ToSeconds(HostTime ns) noexcept {
    return static_cast<double>(ns) / static_cast<double>(kNanosecondsPerSecond);
}

} // namespace cg::core_math
