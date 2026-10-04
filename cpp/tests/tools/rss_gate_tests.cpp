// Unit tests for the soak RSS accounting (CXX-04): the gate must fail on a
// missing reading, pass at or below the budget, and never wrap on large
// hostile values.
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

} // namespace
} // namespace cg::soak
