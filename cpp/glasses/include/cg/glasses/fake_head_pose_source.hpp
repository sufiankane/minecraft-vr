#pragma once

#include <atomic>
#include <cstdint>
#include <random>

#include "cg/glasses/manual_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "ports.hpp"

namespace cg::glasses {

/// A scripted trajectory for `FakeHeadPoseSource` (S5 Task 1).
///
/// One pattern per source; patterns that name a window count samples from the
/// first emitted sample. `Dropout` and `Unstable` publish with the matching
/// `TrackState` while keeping the pose static; `AppearAfter` publishes nothing
/// (clock still advances) until the delay has elapsed.
struct FakeScript {
    enum class Pattern { Static, YawSweep, PitchSweep, Dropout, Unstable, Jitter, AppearAfter };

    Pattern pattern = Pattern::Static;
    /// Sweep speed in degrees/second, or jitter amplitude in degrees.
    double value = 0.0;
    /// First sample index of a `Dropout`/`Unstable` window.
    std::uint64_t after = 0;
    /// Window length in samples, or the `AppearAfter` delay in samples.
    std::uint64_t count = 0;
    /// Deterministic RNG seed for `Jitter`.
    std::uint32_t seed = 0;

    [[nodiscard]] static FakeScript Static() noexcept;
    [[nodiscard]] static FakeScript YawSweep(double degrees_per_second) noexcept;
    [[nodiscard]] static FakeScript PitchSweep(double degrees_per_second) noexcept;
    [[nodiscard]] static FakeScript Dropout(std::uint64_t after, std::uint64_t count) noexcept;
    [[nodiscard]] static FakeScript Unstable(std::uint64_t after, std::uint64_t count) noexcept;
    [[nodiscard]] static FakeScript Jitter(double amplitude_degrees, std::uint32_t seed) noexcept;
    [[nodiscard]] static FakeScript AppearAfter(std::uint64_t count) noexcept;
};

struct FakeHeadPoseSourceConfig {
    /// Sample rate in hertz; must be finite and positive for `Start` to succeed.
    double rate_hz = 100.0;
    /// Clock the source reads and advances; null selects an owned clock at zero.
    ManualClock *clock = nullptr;
    FakeScript script{};
};

/// Deterministic `IHeadPoseSource` driven by a `ManualClock`.
///
/// `AdvanceSamples(n)` emits the next `n` scripted samples; nothing is emitted
/// before `Start` or after `Stop`, and `Stop`/`Start` are idempotent and resume
/// the sequence where it stopped. `seq` is strictly increasing and the sample
/// time is the clock instant at emission, then the clock advances by one
/// period.
///
/// `Recenter` composes an inverse-yaw offset that is applied at read time, so
/// the newest sample is recentred immediately; pitch and roll are untouched,
/// and it is `NotReady` before the first sample (there is no pose to make the
/// origin). `TryGetLatest` extrapolates yaw and pitch by the last inter-sample
/// rate for `predict` nanoseconds, capped at 100 ms, and leaves the position
/// unchanged. A `predict` of zero returns the newest sample verbatim. Wait-free
/// reads; one writer (the test thread driving `AdvanceSamples`) and many
/// readers.
class FakeHeadPoseSource final : public IHeadPoseSource {
  public:
    explicit FakeHeadPoseSource(FakeHeadPoseSourceConfig config);

    ~FakeHeadPoseSource() override = default;
    FakeHeadPoseSource(const FakeHeadPoseSource &) = delete;
    FakeHeadPoseSource &operator=(const FakeHeadPoseSource &) = delete;

    Result<void> Start() override;
    void Stop() noexcept override;
    [[nodiscard]] bool TryGetLatest(HeadSample &out, Duration predict) const noexcept override;
    Result<void> Recenter() override;

    /// Emits `count` scripted samples; a no-op while stopped.
    void AdvanceSamples(std::uint32_t count) noexcept;

    /// The clock the source reads and advances.
    [[nodiscard]] ManualClock &clock() noexcept { return *clock_; }
    [[nodiscard]] const ManualClock &clock() const noexcept { return *clock_; }

  private:
    void EmitOne() noexcept;
    [[nodiscard]] TrackState StateAt(std::uint64_t index) const noexcept;
    [[nodiscard]] core_math::Quat RawRotation() const noexcept;
    [[nodiscard]] double RandomSymmetric(double amplitude) noexcept;

    FakeHeadPoseSourceConfig config_{};
    ManualClock owned_clock_{};
    ManualClock *clock_ = &owned_clock_;
    PoseSlot slot_{};
    std::atomic<bool> running_{false};

    std::uint32_t next_seq_ = 1;
    std::uint64_t sample_index_ = 0;
    double yaw_deg_ = 0.0;
    double pitch_deg_ = 0.0;
    // `Recenter` may be called from a reader thread while the writer emits, so
    // the "a sample exists" signal is an acquire/release atomic.
    std::atomic<bool> has_published_{false};
    bool has_previous_ = false;
    double previous_yaw_deg_ = 0.0;
    double previous_pitch_deg_ = 0.0;

    std::atomic<double> latest_yaw_deg_{0.0};
    std::atomic<double> latest_pitch_deg_{0.0};
    std::atomic<double> yaw_rate_deg_per_s_{0.0};
    std::atomic<double> pitch_rate_deg_per_s_{0.0};
    std::atomic<double> yaw_offset_deg_{0.0};

    std::mt19937 rng_;
};

} // namespace cg::glasses
