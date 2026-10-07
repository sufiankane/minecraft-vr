// Callback-budget benchmark for the newest-wins slot (S8 Task 2): the producer
// path must copy and return, so the recorded number is the per-frame publish
// cost with a stalled consumer (the worst case: every frame is overwritten).

#include <cstddef>
#include <cstdint>
#include <vector>

#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/newest_frame_slot.hpp"

#include <benchmark/benchmark.h>

namespace {

using cg::HostTime;
using cg::StereoFrame;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::FrameSlotGeometry;
using cg::capture::NewestFrameSlot;

constexpr int kWidth = 640;
constexpr int kHeight = 480;

FrameSlotGeometry BenchmarkGeometry() { return FrameSlotGeometry{kWidth, kHeight, kWidth}; }

/// Publisher-only cost: the frame views point at static buffers, so the
/// measurement is the slot's copy, not the fake's pattern fill.
void BM_SlotPublishCallbackCopy(benchmark::State &state) {
    const std::size_t bytes = static_cast<std::size_t>(kWidth) * kHeight;
    std::vector<std::uint8_t> l0(bytes);
    std::vector<std::uint8_t> r0(bytes);
    std::vector<std::uint8_t> l1(bytes);
    std::vector<std::uint8_t> r1(bytes);
    NewestFrameSlot slot(BenchmarkGeometry());
    std::uint64_t seq = 1;
    for (auto _ : state) {
        const StereoFrame frame{HostTime{0}, seq++, cg::StereoImage{l0.data(), r0.data(), kWidth, kHeight, kWidth},
                                cg::StereoImage{l1.data(), r1.data(), kWidth, kHeight, kWidth}};
        slot.OnFrame(frame);
    }
    state.counters["published"] = static_cast<double>(slot.FramesPublished());
    state.counters["overwritten"] = static_cast<double>(slot.Overwritten());
}

/// End-to-end producer cost through the fake source (pattern fill + publish).
void BM_SlotPublishEndToEnd(benchmark::State &state) {
    FakeStereoConfig config;
    config.width = kWidth;
    config.height = kHeight;
    config.stride = kWidth;
    FakeStereoSource source(config);
    NewestFrameSlot slot(BenchmarkGeometry());
    if (!source.Start(&slot).ok()) {
        state.SkipWithError("the fake source refused to start");
        return;
    }
    for (auto _ : state) {
        source.EmitFrame();
    }
    state.counters["published"] = static_cast<double>(slot.FramesPublished());
    state.counters["overwritten"] = static_cast<double>(slot.Overwritten());
    source.Stop();
}

BENCHMARK(BM_SlotPublishCallbackCopy)->Unit(benchmark::kMicrosecond)->MinTime(1.0);
BENCHMARK(BM_SlotPublishEndToEnd)->Unit(benchmark::kMicrosecond)->MinTime(1.0);

} // namespace
