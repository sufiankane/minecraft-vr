#pragma once

#include <cstdint>

#include "cg/glasses/viture_api.hpp"
#include "ports.hpp"
#include "result.hpp"

namespace cg::glasses {

/// Configuration for the vendor-backed stereo frame source (S8 Task 5).
struct VitureStereoConfig {
    /// Row stride in bytes applied to every delivered view; `0` means packed
    /// rows. U-03 decides the real value from the capture probe's evidence.
    int stride = 0;
};

/// The vendor camera callback's arguments (S8 Task 5): four buffer pointers,
/// the SDK timestamp in seconds, and the frame size the SDK reports.
struct VendorFrameArgs {
    const char *left0 = nullptr;
    const char *right0 = nullptr;
    const char *left1 = nullptr;
    const char *right1 = nullptr;
    double sdk_timestamp_s = 0.0;
    int width = 0;
    int height = 0;
};

/// Maps the vendor camera callback arguments to the dossier 5.3 `StereoFrame`:
/// `time` carries the SDK monotonic timestamp in nanoseconds (the same
/// encoding `IVitureApi::PollPose` uses for `cg_head_sample.host_time`), `seq`
/// is the caller's counter, `f0` is the `0` pair and `f1` the `1` pair, and
/// the stride is `stride_override > 0 ? stride_override : width`. The image
/// views point at the vendor buffers and are valid only during the callback.
[[nodiscard]] StereoFrame MakeVendorFrame(const VendorFrameArgs &args, int stride_override, std::uint64_t seq) noexcept;

/// Attaches an `IStereoFrameSink` to a live vendor device (S8 Task 5). The S5
/// pose source owns the device lifecycle (create/start/destroy); this source
/// only registers and clears the frame sink on the same device, so the probe
/// and the recorder receive camera frames without a second device session.
///
/// `Start` reports `NotReady` when no device is created; `Stop` clears the
/// sink, after which no callback can follow (the loader waits for an
/// in-flight callback before clearing).
class VitureStereoSource final : public IStereoFrameSource {
  public:
    explicit VitureStereoSource(IVitureApi &api, VitureStereoConfig config = {});
    ~VitureStereoSource() override;

    VitureStereoSource(const VitureStereoSource &) = delete;
    VitureStereoSource &operator=(const VitureStereoSource &) = delete;
    VitureStereoSource(VitureStereoSource &&) = delete;
    VitureStereoSource &operator=(VitureStereoSource &&) = delete;

    /// Registers `sink`; idempotent while running, and a later call replaces
    /// the sink (the seam's `SetFrameSink` owns the swap).
    Result<void> Start(IStereoFrameSink *sink) override;

    /// Clears the sink; safe before `Start` and on repeat, `noexcept`.
    void Stop() noexcept override;

  private:
    IVitureApi *api_;
    VitureStereoConfig config_;
    bool started_ = false;
};

} // namespace cg::glasses
