#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

#include "ports.hpp"

namespace cg::capture {

/// Configuration for the deterministic synthetic frame source.
struct FakeStereoConfig {
    int width = 640;
    int height = 480;
    /// Row stride in bytes; 0 means `width` (packed rows).
    int stride = 0;
    std::uint64_t first_seq = 1;
    HostTime first_time{0};
    Duration time_step{1'000'000}; // 1 ms per frame
};

/// A deterministic synthetic `IStereoFrameSource` (dossier 5.3: "FakeStereoSource
/// (synthetic patterns)"). Frames are emitted only when a test driver calls
/// `EmitFrame`; the source itself has no clock or thread, so every caller
/// (tests, the recorder round trip) observes the exact sequence it drove.
///
/// The two pairs share one set of four buffers, which is the strongest form of
/// the contract's "views are valid only during the callback": a consumer that
/// keeps a pointer past `OnFrame` sees the next frame's bytes. `Left0()` etc.
/// expose the buffers so tests can compare what was delivered.
class FakeStereoSource final : public IStereoFrameSource {
  public:
    explicit FakeStereoSource(FakeStereoConfig config = {});

    Result<void> Start(IStereoFrameSink *sink) override;
    void Stop() noexcept override;

    /// Emits one frame to the sink when started. While stopped it delivers
    /// nothing and increments `EmittedAfterStop` instead.
    void EmitFrame() noexcept;

    /// Calls to `EmitFrame` that were suppressed because the source is stopped.
    std::uint64_t EmittedAfterStop() const noexcept;

    /// Whether a sink is currently registered.
    bool Started() const noexcept;

    /// The sequence number the next emitted frame will carry.
    std::uint64_t NextSeq() const noexcept;

    /// The host time the next emitted frame will carry.
    HostTime NextTime() const noexcept;

    int Width() const noexcept;
    int Height() const noexcept;
    int Stride() const noexcept;
    /// Bytes per buffer including stride padding.
    std::size_t BufferSize() const noexcept;

    const std::uint8_t *Left0() const noexcept;
    const std::uint8_t *Right0() const noexcept;
    const std::uint8_t *Left1() const noexcept;
    const std::uint8_t *Right1() const noexcept;

  private:
    void Fill(std::vector<std::uint8_t> &buffer, std::uint8_t bias) noexcept;
    StereoImage Image(const std::vector<std::uint8_t> &left, const std::vector<std::uint8_t> &right) const noexcept;

    FakeStereoConfig config_;
    std::vector<std::uint8_t> left0_;
    std::vector<std::uint8_t> right0_;
    std::vector<std::uint8_t> left1_;
    std::vector<std::uint8_t> right1_;
    IStereoFrameSink *sink_ = nullptr;
    std::uint64_t next_seq_;
    HostTime next_time_;
    std::uint64_t emitted_after_stop_ = 0;
};

} // namespace cg::capture
