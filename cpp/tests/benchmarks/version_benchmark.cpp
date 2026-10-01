#include "cg/core_math/version.hpp"

#include <benchmark/benchmark.h>

namespace {

void BM_AbiVersion(benchmark::State &state) {
    for (auto _ : state) {
        benchmark::DoNotOptimize(cg::core_math::AbiVersion());
    }
}

} // namespace

BENCHMARK(BM_AbiVersion);

BENCHMARK_MAIN();
