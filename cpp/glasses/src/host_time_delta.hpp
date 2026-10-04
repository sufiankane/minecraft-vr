#pragma once

#include <cstdint>
#include <limits>
#include <optional>

#include "cg/core_math/time.hpp"

namespace cg::glasses::detail {

/// The non-negative difference `later - earlier` when `later > earlier` and
/// the mathematical difference is representable as `HostTime`; `std::nullopt`
/// otherwise (a reversed/equal pair, or a pair whose delta overflows `int64`,
/// e.g. a hostile CSV row pair `INT64_MIN` then `INT64_MAX`).
///
/// The comparison is signed and therefore itself overflow-free; the
/// subtraction is done in `uint64` so it is well-defined for every input
/// (CXX-05). Callers use the result to advance a clock or derive a rate;
/// a `std::nullopt` means "no valid interval here", never a wrapped value.
[[nodiscard]] inline std::optional<core_math::HostTime>
RepresentablePositiveDelta(core_math::HostTime later, core_math::HostTime earlier) noexcept {
    if (later <= earlier) {
        return std::nullopt;
    }
    const std::uint64_t delta = static_cast<std::uint64_t>(later) - static_cast<std::uint64_t>(earlier);
    if (delta > static_cast<std::uint64_t>(std::numeric_limits<core_math::HostTime>::max())) {
        return std::nullopt;
    }
    return static_cast<core_math::HostTime>(delta);
}

} // namespace cg::glasses::detail
