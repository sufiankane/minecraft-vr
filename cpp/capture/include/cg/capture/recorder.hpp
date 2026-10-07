#pragma once

#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "cgrec.hpp"
#include "ports.hpp"
#include "result.hpp"

namespace cg::capture {

/// Default bounded writer-queue capacity in frames.
inline constexpr std::size_t kDefaultQueueCapacity = 8;

struct RecorderConfig {
    /// Session directory; created by `Open` (parents included).
    std::filesystem::path session_dir;
    FrameStorage storage = FrameStorage::Pgm;
    /// Bounded writer-queue capacity in frames. A full queue drops the frame
    /// (newest submissions are dropped, the oldest queued frame survives) and
    /// counts it; the callback never blocks.
    std::size_t queue_capacity = kDefaultQueueCapacity;
    std::string dof = "3dof";
    std::string sdk_version;
    /// Test seam: invoked on the writer thread before each frame is written,
    /// so a test can stall the disk without touching real IO policies.
    std::function<void()> writer_hook;
};

struct RecorderStats {
    std::uint64_t submitted = 0;
    std::uint64_t written = 0;
    std::uint64_t dropped = 0;
};

/// Writes `.cgrec` sessions (dossier 5.8) behind a bounded writer queue. The
/// producer side (`OnFrame`) copies into the ring and returns; the writer
/// thread drains it to files. A full queue drops and counts, so the SDK
/// camera callback is never blocked by disk.
class Recorder final : public IStereoFrameSink {
  public:
    explicit Recorder(RecorderConfig config);
    ~Recorder() override;

    Recorder(const Recorder &) = delete;
    Recorder &operator=(const Recorder &) = delete;
    Recorder(Recorder &&) = delete;
    Recorder &operator=(Recorder &&) = delete;

    /// Creates the session directory, opens `stereo.csv` and starts the writer
    /// thread. Fails with a named reason when the directory or the CSV cannot
    /// be created.
    [[nodiscard]] Result<void> Open();

    /// Stops the writer, drains the queue and writes the manifest. Idempotent;
    /// safe to call without a successful `Open`.
    void Close() noexcept;

    /// Producer path (the SDK callback). O(frame bytes); never blocks; drops
    /// and counts when the queue is full.
    void OnFrame(const StereoFrame &frame) noexcept override;

    [[nodiscard]] RecorderStats Stats() const noexcept;

  private:
    struct QueueSlot {
        std::vector<std::uint8_t> images; // four concatenated images
        HostTime time = 0;
        std::uint64_t seq = 0;
    };

    [[nodiscard]] bool QueueFull(std::size_t write, std::size_t read) const noexcept;
    void AdoptGeometry(const StereoFrame &frame);
    [[nodiscard]] bool MatchesGeometry(const StereoFrame &frame) const noexcept;
    void CopyFrameImages(const StereoFrame &frame, QueueSlot &slot) noexcept;
    void WriterLoop();
    /// Writes one queued frame; false when the files could not be written (the
    /// frame is then counted as dropped).
    [[nodiscard]] bool WriteFrame(const QueueSlot &slot);
    [[nodiscard]] bool WritePgmFrame(const QueueSlot &slot);
    [[nodiscard]] bool WritePackedBinFrame(const QueueSlot &slot);

    RecorderConfig config_;
    bool open_ = false;
    bool closed_ = false;
    std::size_t image_bytes_ = 0;
    std::size_t image_budget_ = 0; // per frame: 4 images, packed or padded
    int width_ = 0;
    int height_ = 0;
    int stride_ = 0;
    bool geometry_known_ = false;

    std::vector<QueueSlot> ring_;
    std::atomic<std::size_t> write_{0};
    std::atomic<std::size_t> read_{0};
    std::atomic<std::uint64_t> submitted_{0};
    std::atomic<std::uint64_t> written_{0};
    std::atomic<std::uint64_t> dropped_{0};

    std::mutex mutex_;
    std::condition_variable ready_;
    bool stopping_ = false;
    std::thread writer_;
    std::ofstream stereo_csv_;
};

} // namespace cg::capture
