// Micro-benchmarks for the core-math hot paths (S1-WI2f). `version_benchmark.cpp`
// provides BENCHMARK_MAIN for this target.

#include "cg/core_math/clock_mapper.hpp"
#include "cg/core_math/convert.hpp"
#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <cstddef>

#include <benchmark/benchmark.h>

namespace {

using cg::core_math::ClockMapper;
using cg::core_math::Compose;
using cg::core_math::HostTime;
using cg::core_math::Pose;
using cg::core_math::PoseFromSdk;
using cg::core_math::Quat;
using cg::core_math::Slerp;
using cg::core_math::Vec3;

constexpr double kHalfPi = 1.5707963267948966;

Pose SamplePose(double x, double y, double z, double radians) {
    return Pose{Vec3{x, y, z}, Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, radians)};
}

void BM_QuatRotate(benchmark::State &state) {
    const Quat rotation = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kHalfPi);
    const Vec3 point{1.0, 2.0, 3.0};
    for (auto _ : state) {
        benchmark::DoNotOptimize(rotation.Rotate(point));
    }
}

void BM_QuatSlerp(benchmark::State &state) {
    const Quat a = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kHalfPi);
    const Quat b = Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, kHalfPi);
    constexpr double t = 0.5;
    for (auto _ : state) {
        benchmark::DoNotOptimize(Slerp(a, b, t));
    }
}

void BM_PoseCompose(benchmark::State &state) {
    const Pose parent = SamplePose(1.0, 2.0, 3.0, kHalfPi);
    const Pose child = SamplePose(-0.5, 0.25, 1.5, 0.3);
    for (auto _ : state) {
        benchmark::DoNotOptimize(Compose(parent, child));
    }
}

void BM_ClockMap(benchmark::State &state) {
    ClockMapper mapper;
    constexpr double kSdkStart = 10.0;
    for (std::size_t i = 0; i < ClockMapper::kWindowSize; ++i) {
        const double sdk = kSdkStart + 0.01 * static_cast<double>(i);
        mapper.AddSample(sdk, static_cast<HostTime>((sdk + 0.0005) * 1e9));
    }

    const double sdk = kSdkStart + 1.0;
    for (auto _ : state) {
        benchmark::DoNotOptimize(mapper.Map(sdk));
    }
}

void BM_PoseFromSdk(benchmark::State &state) {
    const float sdk[7] = {0.1F, 0.2F, 0.3F, 0.0F, 0.0F, 0.0F, 1.0F};
    for (auto _ : state) {
        benchmark::DoNotOptimize(PoseFromSdk(sdk));
    }
}

} // namespace

BENCHMARK(BM_QuatRotate)->Unit(benchmark::kNanosecond);
BENCHMARK(BM_QuatSlerp)->Unit(benchmark::kNanosecond);
BENCHMARK(BM_PoseCompose)->Unit(benchmark::kNanosecond);
BENCHMARK(BM_ClockMap)->Unit(benchmark::kNanosecond);
BENCHMARK(BM_PoseFromSdk)->Unit(benchmark::kNanosecond);
