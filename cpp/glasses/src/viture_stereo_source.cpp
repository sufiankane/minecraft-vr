#include "cg/glasses/viture_stereo_source.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>

namespace cg::glasses {

namespace {

constexpr double kNanosecondsPerSecond = 1e9;

[[nodiscard]] const std::uint8_t *AsBytes(const char *data) noexcept {
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) — the vendor callback hands char buffers.
    return reinterpret_cast<const std::uint8_t *>(data);
}

} // namespace

// NOLINTNEXTLINE(bugprone-easily-swappable-parameters) — stride vs sequence.
StereoFrame MakeVendorFrame(const VendorFrameArgs &args, int stride_override, std::uint64_t seq) noexcept {
    const int stride = stride_override > 0 ? stride_override : args.width;
    StereoFrame frame{};
    frame.time = static_cast<HostTime>(std::llround(args.sdk_timestamp_s * kNanosecondsPerSecond));
    frame.seq = seq;
    frame.f0 = StereoImage{AsBytes(args.left0), AsBytes(args.right0), args.width, args.height, stride};
    frame.f1 = StereoImage{AsBytes(args.left1), AsBytes(args.right1), args.width, args.height, stride};
    return frame;
}

VitureStereoSource::VitureStereoSource(IVitureApi &api, VitureStereoConfig config) : api_(&api), config_(config) {}

VitureStereoSource::~VitureStereoSource() { Stop(); }

Result<void> VitureStereoSource::Start(IStereoFrameSink *sink) {
    if (sink == nullptr) {
        return Err<void>(Status{StatusCode::InvalidArgument, "viture_stereo_source: Start requires a sink"});
    }
    VitureFrameSinkConfig config;
    config.stride = config_.stride;
    const Result<void> registered = api_->SetFrameSink(sink, config);
    if (!registered.ok()) {
        return registered;
    }
    started_ = true;
    return Ok();
}

void VitureStereoSource::Stop() noexcept {
    if (!started_) {
        return;
    }
    started_ = false;
    (void)api_->SetFrameSink(nullptr, VitureFrameSinkConfig{});
}

} // namespace cg::glasses
