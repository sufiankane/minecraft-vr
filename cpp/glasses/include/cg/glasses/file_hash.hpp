#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>

namespace cg::glasses {

/// Lower-case hex SHA-256 (64 characters) of `size` bytes at `data`.
///
/// Self-contained (no OpenSSL dependency): the loader's optional
/// `CG_VITURE_DLL_SHA256` pin only needs a plain digest. `data` may be null
/// when `size` is zero.
[[nodiscard]] std::string Sha256Hex(const std::uint8_t *data, std::size_t size);

/// Lower-case hex SHA-256 of the file at `path`, or `std::nullopt` when the
/// file cannot be opened or read (the caller reports the I/O failure).
[[nodiscard]] std::optional<std::string> FileSha256Hex(const std::filesystem::path &path);

} // namespace cg::glasses
