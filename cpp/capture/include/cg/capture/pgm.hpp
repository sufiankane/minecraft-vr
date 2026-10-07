#pragma once

#include <cstdint>
#include <filesystem>
#include <vector>

#include "ports.hpp"
#include "result.hpp"

namespace cg::capture {

/// A decoded P5 PGM image with packed rows (no stride padding).
struct PgmImage {
    int width = 0;
    int height = 0;
    std::vector<std::uint8_t> pixels;
};

/// Writes `image` as a binary P5 PGM (maxval 255). Row padding in
/// `image.stride` is skipped, so the file is always `width * height` bytes:
/// PGM cannot represent padded rows.
[[nodiscard]] Result<void> WritePgm(const std::filesystem::path &path, const StereoImage &image);

/// Reads a P5 PGM (maxval 255). Fails with a named reason for a missing file,
/// a malformed header, a non-P5 magic, a non-255 maxval or a short payload.
[[nodiscard]] Result<PgmImage> ReadPgm(const std::filesystem::path &path);

} // namespace cg::capture
