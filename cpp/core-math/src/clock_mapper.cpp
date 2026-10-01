#include "cg/core_math/clock_mapper.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>

namespace cg::core_math {

void ClockMapper::AddSample(double sdk_seconds, HostTime host_time) noexcept {
    if (!std::isfinite(sdk_seconds)) {
        return;
    }

    offsets_[next_sample_] = static_cast<double>(host_time) / static_cast<double>(kNanosecondsPerSecond) - sdk_seconds;
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
        return sorted[middle];
    }
    return (sorted[middle - 1] + sorted[middle]) / 2.0;
}

HostTime ClockMapper::Map(double sdk_seconds) const noexcept {
    if (!std::isfinite(sdk_seconds)) {
        return 0;
    }

    const double offset_seconds = OffsetSeconds();
    return static_cast<HostTime>(
        std::llround((sdk_seconds + offset_seconds) * static_cast<double>(kNanosecondsPerSecond)));
}

bool ClockMapper::IsReady() const noexcept { return sample_count_ >= kReadySampleCount; }

std::size_t ClockMapper::SampleCount() const noexcept { return sample_count_; }

} // namespace cg::core_math
