#pragma once

#include <cstdint>
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

} // namespace cg::soak
