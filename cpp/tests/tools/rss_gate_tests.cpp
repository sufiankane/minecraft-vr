// Unit tests for the soak gate accounting (CXX-04, TD-007): the RSS and
// thread/handle gates must fail on a missing reading, pass at or below the
// budget, and never wrap on large hostile values; the latency gate compares
// the p95 against its threshold.
#include "rss_gate.hpp"

#include <cstdint>
#include <limits>
#include <optional>

#include <gtest/gtest.h>

namespace cg::soak {
namespace {

constexpr std::uint64_t kMib = 1024ULL * 1024ULL;

TEST(RssGateTest, GrowthWithinBudgetPasses) {
    const RssGateResult result = EvaluateRssGate(4 * kMib, 4 * kMib + 512 * 1024, kMib);
    EXPECT_TRUE(result.pass);
    EXPECT_EQ(result.reason, RssGateReason::WithinBudget);
    EXPECT_EQ(result.growth_bytes, 512U * 1024U);
}

TEST(RssGateTest, ExactBudgetPassesAndOneByteOverFails) {
    const RssGateResult at_budget = EvaluateRssGate(1'000, 1'000 + kMib, kMib);
    EXPECT_TRUE(at_budget.pass);
    EXPECT_EQ(at_budget.growth_bytes, kMib);

    const RssGateResult over = EvaluateRssGate(1'000, 1'000 + kMib + 1, kMib);
    EXPECT_FALSE(over.pass);
    EXPECT_EQ(over.reason, RssGateReason::OverBudget);
    EXPECT_EQ(over.growth_bytes, kMib + 1);
}

TEST(RssGateTest, ShrinkingResidentSetIsZeroGrowthAndPasses) {
    const RssGateResult result = EvaluateRssGate(9 * kMib, 3 * kMib, kMib);
    EXPECT_TRUE(result.pass);
    EXPECT_EQ(result.reason, RssGateReason::WithinBudget);
    EXPECT_EQ(result.growth_bytes, 0U);
}

TEST(RssGateTest, MissingReadingsFailTheGate) {
    const RssGateResult no_baseline = EvaluateRssGate(std::nullopt, 5 * kMib, kMib);
    EXPECT_FALSE(no_baseline.pass);
    EXPECT_EQ(no_baseline.reason, RssGateReason::MissingBaseline);

    const RssGateResult no_final = EvaluateRssGate(5 * kMib, std::nullopt, kMib);
    EXPECT_FALSE(no_final.pass);
    EXPECT_EQ(no_final.reason, RssGateReason::MissingFinal);

    const RssGateResult neither = EvaluateRssGate(std::nullopt, std::nullopt, kMib);
    EXPECT_FALSE(neither.pass);
    EXPECT_EQ(neither.reason, RssGateReason::MissingBaseline);
}

TEST(RssGateTest, LargeValuesDoNotWrap) {
    // A final value near UINT64_MAX with a small genuine growth reports that
    // growth (unsigned subtraction), rather than wrapping past the budget.
    const RssGateResult near_max = EvaluateRssGate(std::numeric_limits<std::uint64_t>::max() - 10,
                                                   std::numeric_limits<std::uint64_t>::max() - 5, 1024);
    EXPECT_TRUE(near_max.pass);
    EXPECT_EQ(near_max.growth_bytes, 5U);

    const RssGateResult huge = EvaluateRssGate(0, std::numeric_limits<std::uint64_t>::max(), kMib);
    EXPECT_FALSE(huge.pass);
    EXPECT_EQ(huge.reason, RssGateReason::OverBudget);
    EXPECT_EQ(huge.growth_bytes, std::numeric_limits<std::uint64_t>::max());
}

TEST(RssGateTest, FormattingTwoReadingsIntoSeparateBuffersKeepsBothTexts) {
    // Regression for the soak summary printing baseline and final through one
    // shared thread-local buffer: both arguments evaluated to the same text.
    char baseline[32] = {};
    char final_rss[32] = {};
    FormatRssBytes(baseline, sizeof(baseline), 5 * kMib);
    FormatRssBytes(final_rss, sizeof(final_rss), 6 * kMib);
    EXPECT_STREQ(baseline, "5.00 MiB");
    EXPECT_STREQ(final_rss, "6.00 MiB");
    EXPECT_STRNE(baseline, final_rss);

    char unavailable[32] = {};
    FormatRssBytes(unavailable, sizeof(unavailable), std::nullopt);
    EXPECT_STREQ(unavailable, "unavailable");
}

// --- TD-007: thread/handle count gate and latency gate ---------------------

TEST(CountGateTest, EqualOrShrinkingCountsPass) {
    const CountGateResult equal = EvaluateCountGate(7, 7);
    EXPECT_TRUE(equal.pass);
    EXPECT_EQ(equal.reason, CountGateReason::WithinBaseline);
    EXPECT_EQ(equal.baseline, 7U);
    EXPECT_EQ(equal.final_count, 7U);

    const CountGateResult shrunk = EvaluateCountGate(7, 5);
    EXPECT_TRUE(shrunk.pass);
    EXPECT_EQ(shrunk.reason, CountGateReason::WithinBaseline);
}

TEST(CountGateTest, GrowthFailsAndReportsBothCounts) {
    const CountGateResult grown = EvaluateCountGate(3, 4);
    EXPECT_FALSE(grown.pass);
    EXPECT_EQ(grown.reason, CountGateReason::Grew);
    EXPECT_EQ(grown.baseline, 3U);
    EXPECT_EQ(grown.final_count, 4U);
}

TEST(CountGateTest, MissingReadingsFailTheGate) {
    const CountGateResult no_baseline = EvaluateCountGate(std::nullopt, 2);
    EXPECT_FALSE(no_baseline.pass);
    EXPECT_EQ(no_baseline.reason, CountGateReason::MissingBaseline);

    const CountGateResult no_final = EvaluateCountGate(2, std::nullopt);
    EXPECT_FALSE(no_final.pass);
    EXPECT_EQ(no_final.reason, CountGateReason::MissingFinal);
}

TEST(LatencyGateTest, AtOrBelowThresholdPassesAndAboveFails) {
    EXPECT_TRUE(EvaluateLatencyGate(1'000.0, 1'000.0).pass);
    EXPECT_TRUE(EvaluateLatencyGate(999.0, 1'000.0).pass);

    const LatencyGateResult over = EvaluateLatencyGate(1'001.0, 1'000.0);
    EXPECT_FALSE(over.pass);
    EXPECT_DOUBLE_EQ(over.p95_ns, 1'001.0);
    EXPECT_DOUBLE_EQ(over.threshold_ns, 1'000.0);
}

} // namespace
} // namespace cg::soak
