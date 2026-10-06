#include "cg/capture/fake_stereo_source.hpp"

#include <algorithm>
#include <utility>

namespace cg::capture {

namespace {

[[nodiscard]] std::size_t BufferBytes(const FakeStereoConfig &config) noexcept {
    const int stride = config.stride > 0 ? config.stride : config.width;
    return static_cast<std::size_t>(stride) * static_cast<std::size_t>(std::max(config.height, 0));
}

/// Per-stream pattern biases; distinct values make a crossed pair visible to
/// the round-trip and pattern tests.
constexpr std::uint8_t kRight0Bias = 0x40;
constexpr std::uint8_t kLeft1Bias = 0x80;
constexpr std::uint8_t kRight1Bias = 0xC0;

} // namespace

FakeStereoSource::FakeStereoSource(FakeStereoConfig config)
    : config_(config), next_seq_(config_.first_seq), next_time_(config_.first_time) {
    if (config_.stride <= 0) {
        config_.stride = config_.width;
    }
    const std::size_t size = BufferBytes(config_);
    left0_.resize(size);
    right0_.resize(size);
    left1_.resize(size);
    right1_.resize(size);
}

Result<void> FakeStereoSource::Start(IStereoFrameSink *sink) {
    if (sink == nullptr) {
        return Err<void>(Status{StatusCode::InvalidArgument, "fake_stereo_source: Start requires a sink"});
    }
    // Idempotent: a repeated Start keeps the first sink, so a caller cannot
    // silently redirect a running stream.
    if (sink_ == nullptr) {
        sink_ = sink;
    }
    return Ok();
}

void FakeStereoSource::Stop() noexcept { sink_ = nullptr; }

void FakeStereoSource::EmitFrame() noexcept {
    if (sink_ == nullptr) {
        ++emitted_after_stop_;
        return;
    }

    Fill(left0_, static_cast<std::uint8_t>(next_seq_));
    Fill(right0_, static_cast<std::uint8_t>(next_seq_ + kRight0Bias));
    Fill(left1_, static_cast<std::uint8_t>(next_seq_ + kLeft1Bias));
    Fill(right1_, static_cast<std::uint8_t>(next_seq_ + kRight1Bias));

    const StereoFrame frame{next_time_, next_seq_, Image(left0_, right0_), Image(left1_, right1_)};
    sink_->OnFrame(frame);

    ++next_seq_;
    next_time_ += config_.time_step.ns;
}

std::uint64_t FakeStereoSource::EmittedAfterStop() const noexcept { return emitted_after_stop_; }

bool FakeStereoSource::Started() const noexcept { return sink_ != nullptr; }

std::uint64_t FakeStereoSource::NextSeq() const noexcept { return next_seq_; }

HostTime FakeStereoSource::NextTime() const noexcept { return next_time_; }

int FakeStereoSource::Width() const noexcept { return config_.width; }

int FakeStereoSource::Height() const noexcept { return config_.height; }

int FakeStereoSource::Stride() const noexcept { return config_.stride; }

std::size_t FakeStereoSource::BufferSize() const noexcept { return left0_.size(); }

const std::uint8_t *FakeStereoSource::Left0() const noexcept { return left0_.data(); }

const std::uint8_t *FakeStereoSource::Right0() const noexcept { return right0_.data(); }

const std::uint8_t *FakeStereoSource::Left1() const noexcept { return left1_.data(); }

const std::uint8_t *FakeStereoSource::Right1() const noexcept { return right1_.data(); }

void FakeStereoSource::Fill(std::vector<std::uint8_t> &buffer, std::uint8_t bias) noexcept {
    // A running byte counter reproduces the byte-wise `(index + bias) & 0xFF`
    // pattern without indexing: `std::uint8_t` arithmetic wraps by definition.
    std::uint8_t value = bias;
    for (std::uint8_t &byte : buffer) {
        byte = value;
        ++value;
    }
}

StereoImage FakeStereoSource::Image(const std::vector<std::uint8_t> &left,
                                    const std::vector<std::uint8_t> &right) const noexcept {
    return StereoImage{left.data(), right.data(), config_.width, config_.height, config_.stride};
}

} // namespace cg::capture
