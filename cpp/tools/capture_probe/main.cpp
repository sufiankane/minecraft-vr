// cg-capture-probe — runs the VITURE stereo frame source for a bounded window
// and logs the S8 U-02/U-03 evidence: frame rate, sequence gaps, geometry and
// stride, f0-vs-f1 equality, and first-frame PGM snapshots for the HIL
// artefacts. It loads the vendor library through the shared loader (path
// policy included) and drives a real create/start/attach session.

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "args.hpp"
#include "cg/capture/pgm.hpp"
#include "cg/glasses/viture_loader.hpp"
#include "cg/glasses/viture_stereo_source.hpp"

namespace {

using cg::HostTime;
using cg::IStereoFrameSink;
using cg::StereoFrame;
using cg::StereoImage;
using cg::capture_probe::FormatObservations;
using cg::capture_probe::Observations;
using cg::capture_probe::Options;
using cg::capture_probe::ParseOptions;
using cg::capture_probe::ParseResult;
using cg::capture_probe::UsageText;
using cg::glasses::VitureStereoSource;

/// Collects the run's observations and copies the snapshot frame's four
/// streams. `OnFrame` runs on the SDK thread; the tool reads the fields only
/// after `VitureStereoSource::Stop` returned, which the loader's clear
/// synchronises.
class ProbeSink final : public IStereoFrameSink {
  public:
    explicit ProbeSink(std::uint64_t snapshot_frame) noexcept : snapshot_frame_(snapshot_frame) {}

    void OnFrame(const StereoFrame &frame) noexcept override {
        try {
            const std::size_t bytes =
                static_cast<std::size_t>(frame.f0.stride) * static_cast<std::size_t>(frame.f0.height);
            if (observations_.frames == 0) {
                observations_.first_time = frame.time;
                observations_.width = frame.f0.width;
                observations_.height = frame.f0.height;
                observations_.stride = frame.f0.stride;
            }
            observations_.last_time = frame.time;
            if (observations_.frames > 0 && frame.seq != last_seq_ + 1) {
                ++observations_.sequence_gaps;
            }
            last_seq_ = frame.seq;
            ++observations_.frames;
            if (std::memcmp(frame.f0.left, frame.f1.left, bytes) == 0) {
                observations_.f0_equals_f1 = true;
            }
            if (std::memcmp(frame.f0.left, frame.f0.right, bytes) == 0) {
                observations_.left0_equals_right0 = true;
            }
            if (observations_.frames == snapshot_frame_) {
                snapshot_l0_.assign(frame.f0.left, frame.f0.left + bytes);
                snapshot_r0_.assign(frame.f0.right, frame.f0.right + bytes);
                snapshot_l1_.assign(frame.f1.left, frame.f1.left + bytes);
                snapshot_r1_.assign(frame.f1.right, frame.f1.right + bytes);
                has_snapshot_ = true;
            }
        } catch (...) {
            // A probe must never throw out of the SDK callback.
            failed_ = true;
        }
    }

    [[nodiscard]] Observations TakeObservations() noexcept { return std::move(observations_); }

    [[nodiscard]] bool HasSnapshot() const noexcept { return has_snapshot_; }

    [[nodiscard]] const std::vector<std::uint8_t> &SnapshotL0() const noexcept { return snapshot_l0_; }
    [[nodiscard]] const std::vector<std::uint8_t> &SnapshotR0() const noexcept { return snapshot_r0_; }
    [[nodiscard]] const std::vector<std::uint8_t> &SnapshotL1() const noexcept { return snapshot_l1_; }
    [[nodiscard]] const std::vector<std::uint8_t> &SnapshotR1() const noexcept { return snapshot_r1_; }

  private:
    std::uint64_t snapshot_frame_ = 1;
    std::uint64_t last_seq_ = 0;
    Observations observations_{};
    bool has_snapshot_ = false;
    bool failed_ = false;
    std::vector<std::uint8_t> snapshot_l0_;
    std::vector<std::uint8_t> snapshot_r0_;
    std::vector<std::uint8_t> snapshot_l1_;
    std::vector<std::uint8_t> snapshot_r1_;
};

int ExitWith(const char *message) {
    std::fprintf(stderr, "cg-capture-probe: %s\n", message);
    return 2;
}

int ExitWithStatus(const cg::Status &status) { return ExitWith(status.message()); }

/// Writes the four snapshot streams as PGMs into `directory` and returns how
/// many landed.
std::uint64_t WriteSnapshots(const Options &options, const Observations &observations, ProbeSink &sink) {
    std::error_code error;
    std::filesystem::create_directories(options.out, error);
    if (error) {
        return 0;
    }
    const std::uint64_t frame = options.snapshot_frame;
    const std::string stem = "frame" + std::to_string(frame);
    const std::vector<std::uint8_t> *const streams[4] = {&sink.SnapshotL0(), &sink.SnapshotR0(), &sink.SnapshotL1(),
                                                         &sink.SnapshotR1()};
    const char *const names[4] = {"_l0", "_r0", "_l1", "_r1"};
    std::uint64_t written = 0;
    for (std::size_t index = 0; index < 4; ++index) {
        const std::vector<std::uint8_t> &data = *streams[index];
        const StereoImage view{data.data(), data.data(), observations.width, observations.height, observations.stride};
        const std::filesystem::path path =
            std::filesystem::path{options.out} / (stem + std::string{names[index]} + ".pgm");
        if (cg::capture::WritePgm(path, view).ok()) {
            ++written;
        }
    }
    return written;
}

} // namespace

int main(int argc, char **argv) {
    const std::vector<std::string> arguments(argv + 1, argv + argc);
    const ParseResult parsed = ParseOptions(arguments);
    if (!parsed.ok) {
        std::fprintf(stderr, "cg-capture-probe: %s\n", parsed.error.c_str());
        std::fputs(UsageText().c_str(), stderr);
        return 1;
    }
    const Options &options = parsed.options;
    if (options.help) {
        std::fputs(UsageText().c_str(), stdout);
        return 0;
    }

    // Export the DOF selection before the loader creates the device: the
    // loader reads CG_VITURE_DOF per CreateDevice (U-01 instrumentation).
#if defined(_WIN32)
    (void)_putenv_s("CG_VITURE_DOF", options.dof.c_str());
#else
    (void)setenv("CG_VITURE_DOF", options.dof.c_str(), 1);
#endif

    cg::Result<std::unique_ptr<cg::glasses::IVitureApi>> loaded = cg::glasses::LoadVitureApi(options.dll);
    if (!loaded.ok()) {
        return ExitWithStatus(loaded.status());
    }
    cg::glasses::IVitureApi &api = **loaded;

    const cg::Result<void> created = api.CreateDevice();
    if (!created.ok()) {
        return ExitWithStatus(created.status());
    }
    const cg::Result<void> started = api.StartPose();
    if (!started.ok()) {
        api.DestroyDevice();
        return ExitWithStatus(started.status());
    }

    ProbeSink sink(options.snapshot_frame);
    VitureStereoSource source(api);
    const cg::Result<void> attached = source.Start(&sink);
    if (!attached.ok()) {
        api.RequestStop();
        api.DestroyDevice();
        return ExitWithStatus(attached.status());
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::duration<double>(options.seconds);
    while (std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    source.Stop();
    api.RequestStop();
    api.DestroyDevice();

    Observations observations = sink.TakeObservations();
    if (!options.out.empty() && sink.HasSnapshot()) {
        observations.snapshot_dir = options.out;
        observations.snapshots_written = WriteSnapshots(options, observations, sink);
    }
    std::printf("%s\n", FormatObservations(observations).c_str());
    return observations.frames > 0 ? 0 : 1;
}
