#include "cg/core_math/time.hpp"

#include <cstdlib>

#include <gtest/gtest.h>

namespace {

using cg::core_math::HostTime;
using cg::core_math::kNanosecondsPerSecond;
using cg::core_math::ToNanoseconds;
using cg::core_math::ToSeconds;

TEST(Time, NanosecondsPerSecondIsOneBillion) { EXPECT_EQ(kNanosecondsPerSecond, 1'000'000'000); }

TEST(Time, ToSecondsHandValue) { EXPECT_DOUBLE_EQ(ToSeconds(1'500'000'000), 1.5); }

TEST(Time, ToNanosecondsHandValue) { EXPECT_EQ(ToNanoseconds(1.25), 1'250'000'000); }

TEST(Time, ToNanosecondsRoundsToNearestNanosecond) {
    EXPECT_EQ(ToNanoseconds(0.25), 250'000'000);
    EXPECT_EQ(ToNanoseconds(-0.25), -250'000'000);
    EXPECT_EQ(ToNanoseconds(1.0000000004), 1'000'000'000);
    EXPECT_EQ(ToNanoseconds(1.0000000006), 1'000'000'001);
}

TEST(TimeTest, ToNanosecondsRoundsHalfwayAwayFromZero) {
    EXPECT_EQ(ToNanoseconds(0.5e-9), 1);
    EXPECT_EQ(ToNanoseconds(-0.5e-9), -1);
}

TEST(Time, RoundTripStaysWithinOneNanosecond) {
    constexpr HostTime kSamples[] = {
        0,
        1,
        -1,
        999,
        -999,
        1'000'000,
        -1'500'000,
        1'500'000,
        999'999'999,
        1'234'567'890,
        123'456'789,
        -98'765'432,
        4'000'500'000,
        -2'500'000'000,
    };

    for (const HostTime sample : kSamples) {
        EXPECT_LE(std::abs(ToNanoseconds(ToSeconds(sample)) - sample), 1) << "sample: " << sample;
    }
}

} // namespace
