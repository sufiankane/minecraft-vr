#pragma once

#include <cmath>
#include <cstdint>

namespace cg::core_math {

/// Nanoseconds on the one monotonic host timeline (ADR-0004).
using HostTime = std::int64_t;

/// Nanoseconds in one second.
inline constexpr HostTime kNanosecondsPerSecond = 1'000'000'000;

/// Converts `seconds` to nanoseconds, rounding to the nearest nanosecond with
/// halfway cases away from zero (`llround`), the same rule `ClockMapper` uses.
///
/// Precondition: `seconds` is finite. No allocation.
[[nodiscard]] inline HostTime ToNanoseconds(double seconds) noexcept {
    return static_cast<HostTime>(std::llround(seconds * static_cast<double>(kNanosecondsPerSecond)));
}

/// Converts nanoseconds on the host timeline to seconds. No allocation.
[[nodiscard]] inline double ToSeconds(HostTime ns) noexcept {
    return static_cast<double>(ns) / static_cast<double>(kNanosecondsPerSecond);
}

} // namespace cg::core_math
