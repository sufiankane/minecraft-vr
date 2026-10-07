#include "cg/capture/replay_stereo_source.hpp"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <string>
#include <thread>
#include <utility>

namespace cg::capture {

namespace {

/// How often the real-time wait checks the injected clock and the stop flag.
constexpr auto kWaitSlice = std::chrono::milliseconds(1);

} // namespace

HostTime SteadyFrameClock::Now() const noexcept {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

ReplayStereoSource::ReplayStereoSource(std::filesystem::path session_dir, ReplayMode mode, IFrameClock &clock)
    : session_dir_(std::move(session_dir)), mode_(mode), clock_(&clock) {}

ReplayStereoSource::~ReplayStereoSource() { Stop(); }

Result<void> ReplayStereoSource::Start(IStereoFrameSink *sink) {
    if (sink == nullptr) {
        return Err<void>(Status{StatusCode::InvalidArgument, "replay: Start requires a sink"});
    }
    if (started_) {
        return Ok(); // idempotent: a running replay keeps its sink
    }
    const Result<SessionInfo> info = ValidateSession(session_dir_);
    if (!info.ok()) {
        return Err<void>(info.status());
    }
    const Result<std::vector<StereoCsvRow>> rows = ReadStereoCsv(session_dir_);
    if (!rows.ok()) {
        return Err<void>(rows.status());
    }
    if ((*rows).empty()) {
        return Err<void>(Status{StatusCode::Unsupported, "replay: the session records no frames"});
    }
    info_ = *info;
    rows_ = *rows;
    sink_ = sink;
    delivered_.store(0, std::memory_order_relaxed);
    finished_.store(false, std::memory_order_relaxed);
    stop_requested_.store(false, std::memory_order_relaxed);
    started_ = true;
    worker_ = std::thread([this] { Run(); });
    return Ok();
}

void ReplayStereoSource::Stop() noexcept {
    if (!started_) {
        return;
    }
    stop_requested_.store(true, std::memory_order_release);
    wake_.notify_all();
    if (worker_.joinable()) {
        worker_.join();
    }
    started_ = false;
    sink_ = nullptr;
}

std::uint64_t ReplayStereoSource::FramesDelivered() const noexcept {
    return delivered_.load(std::memory_order_relaxed);
}

bool ReplayStereoSource::Finished() const noexcept { return finished_.load(std::memory_order_acquire); }

int ReplayStereoSource::Width() const noexcept { return info_.width; }

int ReplayStereoSource::Height() const noexcept { return info_.height; }

void ReplayStereoSource::Run() noexcept {
    // A worker thread must never let an exception escape (std::terminate); a
    // mid-run read failure simply ends delivery.
    try {
        const HostTime first_recorded = rows_.front().time;
        const HostTime start = clock_->Now();
        for (const StereoCsvRow &row : rows_) {
            if (stop_requested_.load(std::memory_order_acquire)) {
                break;
            }
            if (mode_ == ReplayMode::RealTime) {
                const HostTime target = start + (row.time - first_recorded);
                std::unique_lock<std::mutex> lock(mutex_);
                while (!stop_requested_.load(std::memory_order_acquire) && clock_->Now() < target) {
                    wake_.wait_for(lock, kWaitSlice);
                }
                if (stop_requested_.load(std::memory_order_acquire)) {
                    break;
                }
            }
            const Result<std::vector<std::uint8_t>> frame = ReadFrameImages(session_dir_, info_, row.seq);
            if (!frame.ok()) {
                break;
            }
            buffer_ = *frame;
            const std::size_t image_bytes =
                static_cast<std::size_t>(info_.storage == FrameStorage::PackedBin ? info_.stride : info_.width) *
                static_cast<std::size_t>(info_.height);
            const int stride = info_.storage == FrameStorage::PackedBin ? info_.stride : info_.width;
            // The views point into the worker-owned buffer and stay valid for
            // the callback, per the 5.3 contract.
            const std::uint8_t *base = buffer_.data();
            const StereoFrame out{
                row.time, row.seq,
                // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — four packed images.
                StereoImage{base, base + image_bytes, info_.width, info_.height, stride},
                // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — four packed images.
                StereoImage{base + 2U * image_bytes, base + 3U * image_bytes, info_.width, info_.height, stride}};
            sink_->OnFrame(out);
            delivered_.fetch_add(1, std::memory_order_relaxed);
        }
    } catch (...) {
        // Delivery ends on a mid-run failure (allocation or IO); the counters
        // show what was delivered and no exception escapes the worker.
        delivery_failed_.store(true, std::memory_order_relaxed);
    }
    finished_.store(true, std::memory_order_release);
}

} // namespace cg::capture
