#include "cg/core_math/clock_mapper.hpp"

#include "cg/core_math/time.hpp"

#include <cmath>
#include <cstddef>
#include <limits>
#include <random>

#include <gtest/gtest.h>

namespace {

using cg::core_math::ClockMapper;
using cg::core_math::HostTime;
using cg::core_math::ToNanoseconds;

// Host time for an SDK instant when the true clock offset is `offset_seconds`.
HostTime HostAt(double sdk_seconds, double offset_seconds) { return ToNanoseconds(sdk_seconds + offset_seconds); }

TEST(ClockMapperTest, ConstantOffsetMapsExactly) {
    ClockMapper mapper;
    for (int i = 1; i <= 3; ++i) {
        const double sdk = static_cast<double>(i);
        mapper.AddSample(sdk, HostAt(sdk, 0.0005));
    }

    EXPECT_EQ(mapper.SampleCount(), 3U);
    EXPECT_NEAR(mapper.OffsetSeconds(), 0.0005, 1e-9);
    EXPECT_EQ(mapper.Map(4.0), 4'000'500'000);
}

TEST(ClockMapperTest, EvenSampleCountUsesMeanOfTwoMiddleOffsets) {
    ClockMapper mapper;
    mapper.AddSample(1.0, 1'000'400'000);
    mapper.AddSample(2.0, 2'000'500'000);
    mapper.AddSample(3.0, 3'000'600'000);
    mapper.AddSample(4.0, 4'000'400'000);

    EXPECT_NEAR(mapper.OffsetSeconds(), 0.00045, 1e-12);
    EXPECT_EQ(mapper.Map(5.0), 5'000'450'000);
}

TEST(ClockMapperTest, IsReadyAtEightSamples) {
    ClockMapper mapper;
    for (int i = 0; i < 7; ++i) {
        const double sdk = static_cast<double>(i);
        mapper.AddSample(sdk, HostAt(sdk, 0.001));
        EXPECT_FALSE(mapper.IsReady()) << "after " << mapper.SampleCount() << " samples";
    }

    constexpr double kSdk = 7.0;
    mapper.AddSample(kSdk, HostAt(kSdk, 0.001));
    EXPECT_TRUE(mapper.IsReady());
    EXPECT_EQ(mapper.SampleCount(), 8U);
}

TEST(ClockMapperTest, MedianTracksJitteredOffsetWithinHalfMillisecond) {
    ClockMapper mapper;
    std::mt19937 generator(20261001U);

    constexpr double kOffset = 0.005;
    constexpr double kJitter = 0.0015;
    constexpr int kSamples = 200;
    for (int i = 0; i < kSamples; ++i) {
        const double sdk = 0.02 * static_cast<double>(i);
        // generate_canonical is exactly specified, so this jitter stream is
        // identical on MSVC and libstdc++ (CI runs both); the distribution
        // classes are not specified to agree across implementations.
        const double unit = std::generate_canonical<double, 53>(generator);
        const double jitter = (unit * 2.0 - 1.0) * kJitter;
        mapper.AddSample(sdk, HostAt(sdk, kOffset + jitter));
    }

    EXPECT_EQ(mapper.SampleCount(), ClockMapper::kWindowSize);
    EXPECT_LT(std::abs(mapper.OffsetSeconds() - kOffset), 0.0005);
}

TEST(ClockMapperTest, MedianTracksDriftWithinOneMillisecond) {
    ClockMapper mapper;
    constexpr double kDriftPerSecond = 0.002;
    constexpr double kStepSeconds = 0.02;
    constexpr int kSamples = 250;

    for (int i = 0; i < kSamples; ++i) {
        const double sdk = kStepSeconds * static_cast<double>(i);
        const double true_offset = kDriftPerSecond * sdk;
        mapper.AddSample(sdk, HostAt(sdk, true_offset));
        if (i >= 100) {
            EXPECT_LT(std::abs(mapper.OffsetSeconds() - true_offset), 0.001) << "at sample " << i;
        }
    }
}

TEST(ClockMapperTest, WindowEvictsOldOffsets) {
    ClockMapper mapper;
    constexpr double kOffsetA = 0.001;
    constexpr double kOffsetB = 0.010;

    for (int i = 0; i < 32; ++i) {
        const double sdk = 0.02 * static_cast<double>(i);
        mapper.AddSample(sdk, HostAt(sdk, kOffsetA));
    }
    EXPECT_EQ(mapper.SampleCount(), ClockMapper::kWindowSize);
    EXPECT_NEAR(mapper.OffsetSeconds(), kOffsetA, 1e-9);

    for (int i = 32; i < 64; ++i) {
        const double sdk = 0.02 * static_cast<double>(i);
        mapper.AddSample(sdk, HostAt(sdk, kOffsetB));
    }

    EXPECT_EQ(mapper.SampleCount(), ClockMapper::kWindowSize);
    EXPECT_NEAR(mapper.OffsetSeconds(), kOffsetB, 1e-9);
    EXPECT_EQ(mapper.Map(100.0), 100'010'000'000);
}

TEST(ClockMapperTest, IgnoresNonFiniteSamples) {
    ClockMapper mapper;
    mapper.AddSample(1.0, HostAt(1.0, 0.5));
    const HostTime mapped_before = mapper.Map(2.0);

    constexpr double kNan = std::numeric_limits<double>::quiet_NaN();
    constexpr double kInf = std::numeric_limits<double>::infinity();
    mapper.AddSample(kNan, 9'000'000'000);
    mapper.AddSample(kInf, 9'000'000'000);
    mapper.AddSample(-kInf, 9'000'000'000);

    EXPECT_EQ(mapper.SampleCount(), 1U);
    EXPECT_EQ(mapper.Map(2.0), mapped_before);
}

TEST(ClockMapperTest, MapBeforeFirstSampleIsPureConversion) {
    const ClockMapper mapper;

    EXPECT_EQ(mapper.SampleCount(), 0U);
    EXPECT_FALSE(mapper.IsReady());
    EXPECT_DOUBLE_EQ(mapper.OffsetSeconds(), 0.0);
    EXPECT_EQ(mapper.Map(4.0), ToNanoseconds(4.0));
    EXPECT_EQ(mapper.Map(-1.25), ToNanoseconds(-1.25));
}

TEST(ClockMapperTest, NonFiniteMapQueryIsZero) {
    ClockMapper mapper;
    mapper.AddSample(1.0, HostAt(1.0, 0.5));

    constexpr double kNan = std::numeric_limits<double>::quiet_NaN();
    constexpr double kInf = std::numeric_limits<double>::infinity();
    EXPECT_EQ(mapper.Map(kNan), 0);
    EXPECT_EQ(mapper.Map(kInf), 0);
    EXPECT_EQ(mapper.Map(-kInf), 0);

    const ClockMapper empty;
    EXPECT_EQ(empty.Map(kNan), 0);
}

TEST(ClockMapperTest, MapSaturatesHostileFiniteInputsAtTheHostTimeRange) {
    ClockMapper mapper;

    // Finite inputs whose product with 1e9 overflows int64 must saturate, not
    // rely on llround's unrepresentable-result behaviour (M-11).
    constexpr double kHuge = 1e300;
    EXPECT_EQ(mapper.Map(kHuge), std::numeric_limits<HostTime>::max());
    EXPECT_EQ(mapper.Map(-kHuge), std::numeric_limits<HostTime>::min());

    // A large offset in the window does not change the saturation either.
    mapper.AddSample(0.0, std::numeric_limits<HostTime>::max());
    mapper.AddSample(0.0, std::numeric_limits<HostTime>::max());
    EXPECT_EQ(mapper.Map(kHuge), std::numeric_limits<HostTime>::max());
    EXPECT_EQ(mapper.Map(-kHuge), std::numeric_limits<HostTime>::min());
}

} // namespace
