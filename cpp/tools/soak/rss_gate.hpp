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

/// Why the soak thread/handle count gate passed or failed.
enum class CountGateReason {
    /// The final count is at or below the baseline (a shrink is fine).
    WithinBaseline,
    /// The final count is above the baseline: a leaked thread or handle.
    Grew,
    MissingBaseline,
    MissingFinal,
};

/// The thread/handle count gate verdict (TD-007). `baseline` is taken right
/// after the consumer thread starts, `final_count` after it is joined, so the
/// expected consumer thread is part of both readings.
struct CountGateResult {
    bool pass = false;
    std::uint64_t baseline = 0;
    std::uint64_t final_count = 0;
    CountGateReason reason = CountGateReason::MissingBaseline;
};

/// Evaluates one thread-or-handle count gate (TD-007, NFR-07): the final count
/// must not exceed the baseline. Missing readings fail, consistently with the
/// RSS gate, so a query that stopped working cannot mask a leak.
[[nodiscard]] inline CountGateResult EvaluateCountGate(std::optional<std::uint64_t> baseline,
                                                       std::optional<std::uint64_t> final_count) noexcept {
    if (!baseline.has_value()) {
        return CountGateResult{false, 0, 0, CountGateReason::MissingBaseline};
    }
    if (!final_count.has_value()) {
        return CountGateResult{false, *baseline, 0, CountGateReason::MissingFinal};
    }
    if (*final_count > *baseline) {
        return CountGateResult{false, *baseline, *final_count, CountGateReason::Grew};
    }
    return CountGateResult{true, *baseline, *final_count, CountGateReason::WithinBaseline};
}

/// The read-to-read gap p95 gate verdict (TD-007): the p95 must stay at or
/// below `threshold_ns`. The threshold is a wall-clock budget for the render
/// consumer's read loop, normally supplied from `--latency-p95-ms`.
struct LatencyGateResult {
    bool pass = false;
    double p95_ns = 0.0;
    double threshold_ns = 0.0;
};

[[nodiscard]] inline LatencyGateResult EvaluateLatencyGate(double p95_ns, double threshold_ns) noexcept {
    return LatencyGateResult{p95_ns <= threshold_ns, p95_ns, threshold_ns};
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
