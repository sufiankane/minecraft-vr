#include "cg/capture/pgm.hpp"

#include <fstream>
#include <string>

namespace cg::capture {

namespace {

constexpr const char *kOpenForWriteFailed = "pgm: cannot open the file for writing";
constexpr const char *kOpenForReadFailed = "pgm: cannot open the file";
constexpr const char *kWriteFailed = "pgm: write failed";
constexpr const char *kBadMagic = "pgm: the file is not a P5 image";
constexpr const char *kBadDimensions = "pgm: the header has invalid dimensions";
constexpr const char *kBadMaxValue = "pgm: the maxval is not 255";
constexpr const char *kShortPayload = "pgm: the payload is shorter than the header declares";
constexpr const char *kBadArgument = "pgm: the image has no data or an invalid geometry";

/// First non-control byte: header tokens are ASCII graphics.
constexpr unsigned char kFirstPrintable = 0x21U;
/// Dimensions above this cap are treated as a malformed header (the device
/// images are far smaller, and the cap keeps the payload size sane).
constexpr int kMaxDimension = 100'000;
constexpr int kDecimalBase = 10;

[[nodiscard]] Status InvalidArgument(const char *reason) { return Status{StatusCode::InvalidArgument, reason}; }
[[nodiscard]] Status Unsupported(const char *reason) { return Status{StatusCode::Unsupported, reason}; }

/// Reads the next whitespace/comma-separated header token, skipping `#`
/// comments; false on end of file.
[[nodiscard]] bool NextToken(std::istream &input, std::string &token) {
    token.clear();
    char character = 0;
    for (;;) {
        if (!input.get(character)) {
            return false;
        }
        if (character == '#') {
            while (input.get(character) && character != '\n') {
            }
            continue;
        }
        if (static_cast<unsigned char>(character) >= kFirstPrintable) {
            break;
        }
    }
    token.push_back(character);
    while (input.get(character) && static_cast<unsigned char>(character) >= kFirstPrintable && character != '#') {
        token.push_back(character);
    }
    if (character == '#') {
        while (input.get(character) && character != '\n') {
        }
    }
    return true;
}

[[nodiscard]] bool ParsePositiveInt(const std::string &token, int &out) noexcept {
    out = 0;
    if (token.empty()) {
        return false;
    }
    for (const char digit : token) {
        if (digit < '0' || digit > '9') {
            return false;
        }
        out = out * kDecimalBase + (digit - '0');
        if (out > kMaxDimension) {
            return false;
        }
    }
    return out > 0;
}

} // namespace

Result<void> WritePgm(const std::filesystem::path &path, const StereoImage &image) {
    if (image.left == nullptr || image.width <= 0 || image.height <= 0 || image.stride < image.width) {
        return Err<void>(InvalidArgument(kBadArgument));
    }
    // NOLINTNEXTLINE(bugprone-signed-bitwise) — std::ios openmode flags are the implementation's bitmask type.
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        return Err<void>(Unsupported(kOpenForWriteFailed));
    }
    output << "P5\n" << image.width << ' ' << image.height << "\n255\n";
    for (int row = 0; row < image.height; ++row) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — row addressing in the caller's view.
        const std::uint8_t *row_data = image.left + static_cast<std::ptrdiff_t>(row) * image.stride;
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) — the stream writes bytes.
        output.write(reinterpret_cast<const char *>(row_data), image.width);
    }
    if (!output) {
        return Err<void>(Unsupported(kWriteFailed));
    }
    return Ok();
}

Result<PgmImage> ReadPgm(const std::filesystem::path &path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        return Err<PgmImage>(Unsupported(kOpenForReadFailed));
    }
    std::string magic;
    std::string width_token;
    std::string height_token;
    std::string max_token;
    if (!NextToken(input, magic) || !NextToken(input, width_token) || !NextToken(input, height_token) ||
        !NextToken(input, max_token)) {
        return Err<PgmImage>(Unsupported(kBadDimensions));
    }
    if (magic != "P5") {
        return Err<PgmImage>(Unsupported(kBadMagic));
    }
    PgmImage image;
    if (!ParsePositiveInt(width_token, image.width) || !ParsePositiveInt(height_token, image.height)) {
        return Err<PgmImage>(Unsupported(kBadDimensions));
    }
    if (max_token != "255") {
        return Err<PgmImage>(Unsupported(kBadMaxValue));
    }
    const std::size_t bytes = static_cast<std::size_t>(image.width) * static_cast<std::size_t>(image.height);
    image.pixels.resize(bytes);
    // The token scanner consumed exactly one delimiter past "255", so the
    // stream is already positioned at the payload.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) — the stream reads bytes.
    input.read(reinterpret_cast<char *>(image.pixels.data()), static_cast<std::streamsize>(bytes));
    if (input.gcount() != static_cast<std::streamsize>(bytes)) {
        return Err<PgmImage>(Unsupported(kShortPayload));
    }
    return Ok(image);
}

} // namespace cg::capture
