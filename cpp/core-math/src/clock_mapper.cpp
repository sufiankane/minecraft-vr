#include "cg/core_math/clock_mapper.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>

namespace cg::core_math {

namespace {

/// Median of an even sample count averages the two middle samples.
constexpr double kEvenMedianDivisor = 2.0;

} // namespace

void ClockMapper::AddSample(double sdk_seconds, HostTime host_time) noexcept {
    if (!std::isfinite(sdk_seconds)) {
        return;
    }

    // `next_sample_` is maintained modulo kWindowSize by the line below, so
    // the checked access never throws.
    offsets_.at(next_sample_) =
        static_cast<double>(host_time) / static_cast<double>(kNanosecondsPerSecond) - sdk_seconds;
    next_sample_ = (next_sample_ + 1) % kWindowSize;
    if (sample_count_ < kWindowSize) {
        ++sample_count_;
    }
}

double ClockMapper::OffsetSeconds() const noexcept {
    if (sample_count_ == 0) {
        return 0.0;
    }

    std::array<double, kWindowSize> sorted = offsets_;
    std::sort(sorted.begin(), sorted.begin() + static_cast<std::ptrdiff_t>(sample_count_));

    const std::size_t middle = sample_count_ / 2;
    if (sample_count_ % 2 != 0) {
        // `middle` is in [0, sample_count_) and sample_count_ <= kWindowSize,
        // so the checked access never throws.
        return sorted.at(middle);
    }
    // `middle - 1` and `middle` are in [0, sample_count_) because an even
    // count of at least two is guaranteed here, so the checked accesses never
    // throw.
    return (sorted.at(middle - 1) + sorted.at(middle)) / kEvenMedianDivisor;
}

HostTime ClockMapper::Map(double sdk_seconds) const noexcept {
    if (!std::isfinite(sdk_seconds)) {
        return 0;
    }

    // The saturating conversion (M-11) clamps a hostile finite stamp or a
    // corrupt offset at the HostTime range instead of leaving the rounding of
    // an unrepresentable value to llround.
    return ToNanoseconds(sdk_seconds + OffsetSeconds());
}

bool ClockMapper::IsReady() const noexcept { return sample_count_ >= kReadySampleCount; }

std::size_t ClockMapper::SampleCount() const noexcept { return sample_count_; }

} // namespace cg::core_math
