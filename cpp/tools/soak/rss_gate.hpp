#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <optional>

namespace cg::soak {

/// Why the soak RSS gate passed or failed.
enum class RssGateReason {
    WithinBudget,
    MissingBaseline,
    MissingFinal,
    OverBudget,
};

/// The soak RSS gate verdict: growth is `final - baseline` (zero when the
/// final reading is at or below the baseline, i.e. no growth) and the gate
/// passes when it stays within `budget_bytes`.
struct RssGateResult {
    bool pass = false;
    std::uint64_t growth_bytes = 0;
    RssGateReason reason = RssGateReason::MissingBaseline;
};

/// Evaluates the soak RSS gate (CXX-04).
///
/// A missing reading **fails** the gate: a run whose resident-set query failed
/// cannot claim the budget was respected. This deliberately replaces the old
/// `0`-on-error accounting, under which a failed `GetProcessMemoryInfo` or
/// `getrusage` looked like "zero RSS growth" and always passed.
[[nodiscard]] inline RssGateResult EvaluateRssGate(std::optional<std::uint64_t> baseline,
                                                   std::optional<std::uint64_t> final_rss,
                                                   std::uint64_t budget_bytes) noexcept {
    if (!baseline.has_value()) {
        return RssGateResult{false, 0, RssGateReason::MissingBaseline};
    }
    if (!final_rss.has_value()) {
        return RssGateResult{false, 0, RssGateReason::MissingFinal};
    }
    const std::uint64_t growth = *final_rss > *baseline ? *final_rss - *baseline : 0;
    if (growth > budget_bytes) {
        return RssGateResult{false, growth, RssGateReason::OverBudget};
    }
    return RssGateResult{true, growth, RssGateReason::WithinBudget};
}

/// Formats one RSS reading into the caller's buffer: `"x.xx MiB"`, or
/// `"unavailable"` when the query failed.
///
/// The caller supplies the storage (CXX-04 follow-up): formatting two readings
/// in one `printf` through a shared buffer would print the same value twice
/// because the evaluation order of the arguments is unspecified. A recipient
/// that needs both texts holds two buffers (or formats twice), so the two
/// readings can never alias.
inline void FormatRssBytes(char *buffer, std::size_t size, std::optional<std::uint64_t> rss) noexcept {
    if (buffer == nullptr || size == 0) {
        return;
    }
    if (!rss.has_value()) {
        std::snprintf(buffer, size, "unavailable");
        return;
    }
    std::snprintf(buffer, size, "%.2f MiB", static_cast<double>(*rss) / (1024.0 * 1024.0));
}

} // namespace cg::soak
