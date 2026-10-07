#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <thread>
#include <vector>

#include "cgrec.hpp"
#include "ports.hpp"
#include "result.hpp"

namespace cg::capture {

/// Timebase the real-time replay schedules against; production uses
/// `SteadyFrameClock`, tests inject a manual clock.
class IFrameClock {
  public:
    IFrameClock() = default;
    virtual ~IFrameClock() = default;
    IFrameClock(const IFrameClock &) = delete;
    IFrameClock &operator=(const IFrameClock &) = delete;
    IFrameClock(IFrameClock &&) = delete;
    IFrameClock &operator=(IFrameClock &&) = delete;
    [[nodiscard]] virtual HostTime Now() const noexcept = 0;
};

/// Monotonic host clock (`std::chrono::steady_clock` on the HostTime scale).
class SteadyFrameClock final : public IFrameClock {
  public:
    [[nodiscard]] HostTime Now() const noexcept override;
};

enum class ReplayMode : std::uint8_t {
    /// Deliver frames as fast as the sink accepts them (offline tools/tests).
    Fast,
    /// Deliver frames on the recorded timeline relative to the start instant,
    /// waiting through the injected clock (dossier 5.8: within 1 ms of the
    /// original timing; the HIL records the wall-clock fidelity).
    RealTime,
};

/// Plays a `.cgrec` session back into an `IStereoFrameSink` (dossier 5.3:
/// `ReplayStereoSource`). `Start` validates the session first and fails with
/// the validator's named reason; delivery runs on a worker thread, so `Stop`
/// can join promptly even mid-wait, and no callback can follow `Stop`.
/// Frames are read from disk on demand, so a session is never loaded whole.
class ReplayStereoSource final : public IStereoFrameSource {
  public:
    ReplayStereoSource(std::filesystem::path session_dir, ReplayMode mode, IFrameClock &clock);
    ~ReplayStereoSource() override;

    ReplayStereoSource(const ReplayStereoSource &) = delete;
    ReplayStereoSource &operator=(const ReplayStereoSource &) = delete;
    ReplayStereoSource(ReplayStereoSource &&) = delete;
    ReplayStereoSource &operator=(ReplayStereoSource &&) = delete;

    Result<void> Start(IStereoFrameSink *sink) override;
    void Stop() noexcept override;

    /// Frames delivered since the last `Start`.
    [[nodiscard]] std::uint64_t FramesDelivered() const noexcept;
    /// True once the worker finished (all frames played or delivery ended).
    [[nodiscard]] bool Finished() const noexcept;
    /// The session geometry once `Start` succeeded (0 before).
    [[nodiscard]] int Width() const noexcept;
    [[nodiscard]] int Height() const noexcept;

  private:
    void Run() noexcept;

    std::filesystem::path session_dir_;
    ReplayMode mode_;
    IFrameClock *clock_;
    IStereoFrameSink *sink_ = nullptr;
    SessionInfo info_{};
    std::vector<StereoCsvRow> rows_;
    std::vector<std::uint8_t> buffer_;
    std::atomic<std::uint64_t> delivered_{0};
    std::atomic<bool> stop_requested_{false};
    std::atomic<bool> finished_{false};
    std::atomic<bool> delivery_failed_{false};
    std::mutex mutex_;
    std::condition_variable wake_;
    std::thread worker_;
    bool started_ = false;
};

} // namespace cg::capture
