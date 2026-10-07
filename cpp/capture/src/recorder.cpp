#include "cg/capture/recorder.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <ctime>
#include <string>
#include <utility>

#include "cg/capture/pgm.hpp"
#include "cg/core_math/time.hpp"

namespace cg::capture {

namespace {

constexpr const char *kOpenFailed = "recorder: cannot create the session directory or stereo.csv";
constexpr const char *kClosedError = "recorder: the session is already closed";
constexpr const char *kCsvHeader = "seq,host_time_ns,sdk_time_s,width,height";
constexpr std::array<const char *, 4> kPgmSuffixes = {"_l0", "_r0", "_l1", "_r1"};
constexpr std::size_t kUtcBufferSize = 32;

[[nodiscard]] std::string UtcNow() {
    const std::time_t now = std::time(nullptr);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::array<char, kUtcBufferSize> buffer{};
    (void)std::strftime(buffer.data(), buffer.size(), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return buffer.data();
}

} // namespace

Recorder::Recorder(RecorderConfig config) : config_(std::move(config)) {
    if (config_.queue_capacity == 0) {
        config_.queue_capacity = 1;
    }
    ring_.resize(config_.queue_capacity);
}

Recorder::~Recorder() { Close(); }

Result<void> Recorder::Open() {
    if (closed_) {
        return Err<void>(Status{StatusCode::NotReady, kClosedError});
    }
    if (open_) {
        return Ok();
    }
    std::error_code error;
    std::filesystem::create_directories(config_.session_dir / "frames", error);
    if (error) {
        return Err<void>(Status{StatusCode::Unsupported, kOpenFailed});
    }
    stereo_csv_.open(config_.session_dir / "stereo.csv", std::ios::trunc);
    if (!stereo_csv_) {
        return Err<void>(Status{StatusCode::Unsupported, kOpenFailed});
    }
    stereo_csv_ << kCsvHeader << '\n';
    open_ = true;
    writer_ = std::thread([this] { WriterLoop(); });
    return Ok();
}

void Recorder::Close() noexcept {
    if (closed_) {
        return;
    }
    closed_ = true;
    if (!open_) {
        return;
    }
    // The writer thread join and the manifest's string/JSON allocations can
    // throw; teardown must not, so the whole body is guarded below.
    try {
        {
            const std::lock_guard<std::mutex> lock(mutex_);
            stopping_ = true;
        }
        ready_.notify_all();
        if (writer_.joinable()) {
            writer_.join();
        }
        stereo_csv_.flush();
        stereo_csv_.close();

        CgrecManifest manifest;
        manifest.width = width_;
        manifest.height = height_;
        manifest.stride = stride_;
        manifest.frame_count = written_.load(std::memory_order_relaxed);
        manifest.dropped = dropped_.load(std::memory_order_relaxed);
        manifest.storage = config_.storage;
        manifest.dof = config_.dof;
        manifest.sdk_version = config_.sdk_version;
        manifest.created_utc = UtcNow();
        if (geometry_known_) {
            (void)WriteManifest(config_.session_dir, manifest);
        }
    } catch (...) {
        // Teardown must not throw; the manifest is diagnostic.
        open_ = false;
    }
    open_ = false;
}

void Recorder::OnFrame(const StereoFrame &frame) noexcept {
    if (!open_ || closed_) {
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    submitted_.fetch_add(1, std::memory_order_relaxed);
    if (!geometry_known_) {
        try {
            AdoptGeometry(frame);
        } catch (...) {
            geometry_known_ = false;
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
    }
    if (!MatchesGeometry(frame)) {
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    const std::size_t write = write_.load(std::memory_order_relaxed);
    const std::size_t read = read_.load(std::memory_order_acquire);
    if (QueueFull(write, read)) {
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    QueueSlot &slot = ring_.at(write % config_.queue_capacity);
    CopyFrameImages(frame, slot);
    slot.seq = frame.seq;
    slot.time = frame.time;
    write_.store(write + 1, std::memory_order_release);
    ready_.notify_one();
}

RecorderStats Recorder::Stats() const noexcept {
    RecorderStats stats;
    stats.submitted = submitted_.load(std::memory_order_relaxed);
    stats.written = written_.load(std::memory_order_relaxed);
    stats.dropped = dropped_.load(std::memory_order_relaxed);
    return stats;
}

bool Recorder::QueueFull(std::size_t write, std::size_t read) const noexcept {
    return write - read >= config_.queue_capacity;
}

void Recorder::AdoptGeometry(const StereoFrame &frame) {
    width_ = frame.f0.width;
    height_ = frame.f0.height;
    stride_ = frame.f0.stride;
    image_bytes_ = static_cast<std::size_t>(stride_) * static_cast<std::size_t>(height_);
    image_budget_ = image_bytes_ * 4U;
    for (QueueSlot &slot : ring_) {
        slot.images.resize(image_budget_);
    }
    geometry_known_ = width_ > 0 && height_ > 0 && stride_ >= width_;
}

bool Recorder::MatchesGeometry(const StereoFrame &frame) const noexcept {
    return frame.f0.width == width_ && frame.f0.height == height_ && frame.f0.stride == stride_ &&
           frame.f1.width == width_ && frame.f1.height == height_ && frame.f1.stride == stride_;
}

void Recorder::CopyFrameImages(const StereoFrame &frame, QueueSlot &slot) noexcept {
    const std::array<const std::uint8_t *, 4> sources = {frame.f0.left, frame.f0.right, frame.f1.left, frame.f1.right};
    for (std::size_t image = 0; image < sources.size(); ++image) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — the slot owns four packed images.
        std::uint8_t *destination = slot.images.data() + image * image_bytes_;
        if (sources.at(image) != nullptr) {
            std::memcpy(destination, sources.at(image), image_bytes_);
        } else {
            // A null stream is absent on this device/mode (U-03); record zeros
            // so the frame layout stays homogeneous.
            std::memset(destination, 0, image_bytes_);
        }
    }
}

void Recorder::WriterLoop() {
    for (;;) {
        QueueSlot *slot = nullptr;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            ready_.wait(lock, [this] {
                return stopping_ || read_.load(std::memory_order_acquire) != write_.load(std::memory_order_acquire);
            });
            const std::size_t write = write_.load(std::memory_order_acquire);
            const std::size_t read = read_.load(std::memory_order_relaxed);
            if (read == write) {
                if (stopping_) {
                    break;
                }
                continue;
            }
            slot = &ring_.at(read % config_.queue_capacity);
        }
        if (config_.writer_hook) {
            config_.writer_hook();
        }
        const bool written = WriteFrame(*slot);
        if (written) {
            written_.fetch_add(1, std::memory_order_relaxed);
        } else {
            dropped_.fetch_add(1, std::memory_order_relaxed);
        }
        const std::size_t read = read_.load(std::memory_order_relaxed);
        read_.store(read + 1, std::memory_order_release);
    }
}

bool Recorder::WriteFrame(const QueueSlot &slot) {
    const bool ok = config_.storage == FrameStorage::PackedBin ? WritePackedBinFrame(slot) : WritePgmFrame(slot);
    if (!ok) {
        return false;
    }
    stereo_csv_ << slot.seq << ',' << slot.time << ',' << cg::core_math::ToSeconds(slot.time) << ',' << width_ << ','
                << height_ << '\n';
    return static_cast<bool>(stereo_csv_);
}

bool Recorder::WritePgmFrame(const QueueSlot &slot) {
    for (std::size_t image = 0; image < 4U; ++image) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — the slot owns four packed images.
        const std::uint8_t *data = slot.images.data() + image * image_bytes_;
        const StereoImage view{data, data, width_, height_, stride_};
        const std::string path =
            (config_.session_dir / "frames" / (FrameFileStem(slot.seq) + std::string{kPgmSuffixes.at(image)} + ".pgm"))
                .string();
        if (!WritePgm(path, view).ok()) {
            return false;
        }
    }
    return true;
}

bool Recorder::WritePackedBinFrame(const QueueSlot &slot) {
    const std::filesystem::path path = config_.session_dir / "frames" / (FrameFileStem(slot.seq) + ".bin");
    // NOLINTNEXTLINE(bugprone-signed-bitwise) — std::ios openmode flags are the implementation's bitmask type.
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        return false;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) — the stream writes bytes.
    output.write(reinterpret_cast<const char *>(slot.images.data()), static_cast<std::streamsize>(image_budget_));
    return static_cast<bool>(output);
}

} // namespace cg::capture
