// Micro-benchmark for the wait-free pose read path (S5 Task 4a).
// `version_benchmark.cpp` provides BENCHMARK_MAIN for this target.
//
// `BM_TryGetLatest` drives a `VitureHeadPoseSource` over the programmable
// `FakeVitureApi`: the fake publishes one sample, the timed loop then measures
// the reader-side `TryGetLatest`, and the source is stopped first so the
// polling thread cannot perturb the measurement. The read path has no locks,
// no allocation and no blocking; the allocation-counter gate lives in
// `cpp/tests/glasses/allocation_tests.cpp`.

#include <chrono>
#include <thread>

#include <benchmark/benchmark.h>

#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "fake_viture_api.hpp"
#include "ports.hpp"

namespace {

using cg::Duration;
using cg::HeadSample;
using cg::Pose;
using cg::TrackState;
using cg::core_math::Quat;
using cg::core_math::Vec3;
using cg::glasses::ManualHostClock;
using cg::glasses::VitureHeadPoseSource;
using cg::glasses::test::FakeVitureApi;

HeadSample Placeholder() noexcept {
    return HeadSample{0, Pose{Vec3{0.0, 0.0, 0.0}, Quat::kIdentity}, TrackState::Stable, 0};
}

/// Starts `source` over a one-sample fake and waits until that sample is
/// readable. The caller stops the source before measuring.
bool PublishOneSample(VitureHeadPoseSource &source) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    HeadSample sample = Placeholder();
    while (std::chrono::steady_clock::now() < deadline) {
        if (source.TryGetLatest(sample, Duration{0})) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

void BM_TryGetLatest(benchmark::State &state) {
    FakeVitureApi api;
    cg_head_sample sdk{};
    sdk.host_time = 0;
    sdk.pose.q.w = 1.0F;
    sdk.state = CG_TRACK_STABLE;
    sdk.sequence = 1;
    api.samples.push_back(sdk);

    ManualHostClock clock;
    VitureHeadPoseSource source(api, clock);
    if (!source.Start().ok()) {
        state.SkipWithError("FakeVitureApi Start failed");
        return;
    }
    const bool published = PublishOneSample(source);
    source.Stop();
    if (!published) {
        state.SkipWithError("FakeVitureApi never published a sample");
        return;
    }

    HeadSample out = Placeholder();
    for (auto _ : state) {
        benchmark::DoNotOptimize(source.TryGetLatest(out, Duration{0}));
        benchmark::DoNotOptimize(out);
    }
}

} // namespace

BENCHMARK(BM_TryGetLatest)->Unit(benchmark::kNanosecond);
