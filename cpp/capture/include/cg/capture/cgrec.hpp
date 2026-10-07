#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "ports.hpp"
#include "result.hpp"

namespace cg::capture {

/// `.cgrec` schema this build writes and understands (dossier 5.8).
inline constexpr int kCgrecSchema = 2;

enum class FrameStorage : std::uint8_t {
    /// Four P5 PGM files per frame (`NNNNNNNN_l0.pgm`, `_r0`, `_l1`, `_r1`),
    /// packed rows.
    Pgm,
    /// One binary file per frame (`NNNNNNNN.bin`): four images concatenated,
    /// each `stride * height` bytes with row padding preserved.
    PackedBin,
};

/// Parsed `manifest.json`.
struct CgrecManifest {
    int schema = kCgrecSchema;
    int width = 0;
    int height = 0;
    int stride = 0;
    std::uint64_t frame_count = 0;
    std::uint64_t dropped = 0;
    FrameStorage storage = FrameStorage::Pgm;
    std::string dof = "3dof";
    std::string sdk_version;
    std::string created_utc;
};

/// What `ValidateSession` returns once a session passes every check.
struct SessionInfo {
    int schema = 0;
    int width = 0;
    int height = 0;
    int stride = 0;
    std::uint64_t frame_count = 0;
    std::uint64_t dropped = 0;
    FrameStorage storage = FrameStorage::Pgm;
    std::string dof;
    std::string sdk_version;
};

/// Writes `manifest.json` into `session_dir` (the directory must exist).
[[nodiscard]] Result<void> WriteManifest(const std::filesystem::path &session_dir, const CgrecManifest &manifest);

/// The zero-padded frame file stem (`00000001` …) shared by the recorder and
/// the validator.
[[nodiscard]] std::string FrameFileStem(std::uint64_t seq);

/// A row of `stereo.csv`.
struct StereoCsvRow {
    std::uint64_t seq = 0;
    HostTime time = 0;
    double sdk_time_s = 0.0;
    int width = 0;
    int height = 0;
};

/// Reads `stereo.csv`; fails on a missing file, a wrong header, a malformed
/// row or a non-monotonic sequence.
[[nodiscard]] Result<std::vector<StereoCsvRow>> ReadStereoCsv(const std::filesystem::path &session_dir);

/// Reads one frame as four concatenated images (each `width * height` for PGM
/// sessions, `stride * height` for packed-bin sessions). `seq` is the
/// manifest sequence number (the first recorded frame is 1).
[[nodiscard]] Result<std::vector<std::uint8_t>> ReadFrameImages(const std::filesystem::path &session_dir,
                                                                const SessionInfo &info, std::uint64_t seq);

/// Validates a `.cgrec` session directory: manifest schema, geometry,
/// frame-file presence and sizes, CSV header/monotonicity, and the recorded
/// count. Every failure carries a named reason.
[[nodiscard]] Result<SessionInfo> ValidateSession(const std::filesystem::path &session_dir);

} // namespace cg::capture
