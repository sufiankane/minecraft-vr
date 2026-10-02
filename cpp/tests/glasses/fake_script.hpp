#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

#include "cg/glasses/fake_head_pose_source.hpp"
#include "cg/glasses/manual_clock.hpp"
#include "ports.hpp"

namespace cg::glasses::test {

/// One implementation under contract test, plus the deterministic controls the
/// suite needs. `advance(count)` publishes (fake, replay) or waits for
/// `count` further samples (free-running sources); it is empty when a source
/// cannot be driven, in which case the suite only exercises the static rules.
struct SourceUnderTest {
    std::unique_ptr<IHeadPoseSource> source;
    ManualClock *clock = nullptr;
    std::function<void(std::uint32_t count)> advance;
};

/// A named factory the parameterised contract suite instantiates its cases
/// over. Later stages (Viture over the SDK seam, replay) append their own.
struct ContractFactory {
    std::string name;
    std::function<SourceUnderTest()> make;
};

/// The process-wide registry behind the contract suite. Registration happens
/// during static initialisation of each test translation unit; the suite
/// snapshots the registry from `main`, after every static initialiser has run
/// (avoiding gtest's static-initialisation-order trap).
std::vector<ContractFactory> &ContractFactories();

/// Appends a factory to `ContractFactories()`; safe to call during static init.
void AddContractFactory(ContractFactory factory);

/// Registers one test per (case, factory) pair. Must run before
/// `RUN_ALL_TESTS()`.
void RegisterContractSuite();

/// Builds a scripted `FakeHeadPoseSource` at `rate_hz` with its own
/// `ManualClock`; the returned `advance` publishes scripted samples.
SourceUnderTest MakeFakeSource(FakeScript script, double rate_hz = 100.0);

} // namespace cg::glasses::test
