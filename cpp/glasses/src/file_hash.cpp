// Minimal FIPS 180-4 SHA-256 for the loader's optional vendor-DLL hash pin
// (TD-051). The algorithm is self-contained so cg_glasses gains no crypto
// dependency; it is used for accidental-corruption / wrong-build detection and
// explicitly not a substitute for signature verification (the documented
// residual in viture_loader.hpp).
//
// The implementation deliberately uses `std::array`/`std::span` with bounds-
// checked indexing and named constants instead of raw buffers and literals, so
// the module is clang-tidy clean under the repo policy (TD-029). The only
// suppression is the FIPS 180-4 constant table itself, where the literal
// values are the specification.

#include "cg/glasses/file_hash.hpp"

#include <array>
#include <cstddef>
#include <fstream>
#include <span>
#include <string_view>

namespace cg::glasses {

namespace {

constexpr std::size_t kSha256BlockBytes = 64;
constexpr std::size_t kSha256WordBytes = 4;
constexpr std::size_t kSha256ScheduleWords = 64;
constexpr std::size_t kSha256StateWords = 8;
constexpr std::size_t kSha256DigestBytes = 32;
constexpr std::size_t kSha256LengthBytes = 8;
constexpr std::size_t kSha256WordsPerBlock = 16;
constexpr std::size_t kSha256PaddingOffset = 56;
constexpr unsigned kBitsPerByte = 8U;
constexpr unsigned kSha256WordBits = 32U;
constexpr unsigned kNibbleBits = 4U;
constexpr std::uint32_t kNibbleMask = 0xFU;
constexpr std::size_t kHexDigitsPerByte = 2;
constexpr std::string_view kHexDigits{"0123456789abcdef"};

// The FIPS 180-4 constants and the round rotations (6/11/25 and 2/13/22) are
// transcribed from the specification; naming each literal would obscure its
// correspondence with the standard it implements, and the surrounding loops
// bound every index. This is the single justified suppression in this file.
// NOLINTBEGIN(cppcoreguidelines-avoid-magic-numbers)
constexpr std::array<std::uint32_t, kSha256ScheduleWords> kRoundConstants{
    0x428A2F98U, 0x71374491U, 0xB5C0FBCFU, 0xE9B5DBA5U, 0x3956C25BU, 0x59F111F1U, 0x923F82A4U, 0xAB1C5ED5U,
    0xD807AA98U, 0x12835B01U, 0x243185BEU, 0x550C7DC3U, 0x72BE5D74U, 0x80DEB1FEU, 0x9BDC06A7U, 0xC19BF174U,
    0xE49B69C1U, 0xEFBE4786U, 0x0FC19DC6U, 0x240CA1CCU, 0x2DE92C6FU, 0x4A7484AAU, 0x5CB0A9DCU, 0x76F988DAU,
    0x983E5152U, 0xA831C66DU, 0xB00327C8U, 0xBF597FC7U, 0xC6E00BF3U, 0xD5A79147U, 0x06CA6351U, 0x14292967U,
    0x27B70A85U, 0x2E1B2138U, 0x4D2C6DFCU, 0x53380D13U, 0x650A7354U, 0x766A0ABBU, 0x81C2C92EU, 0x92722C85U,
    0xA2BFE8A1U, 0xA81A664BU, 0xC24B8B70U, 0xC76C51A3U, 0xD192E819U, 0xD6990624U, 0xF40E3585U, 0x106AA070U,
    0x19A4C116U, 0x1E376C08U, 0x2748774CU, 0x34B0BCB5U, 0x391C0CB3U, 0x4ED8AA4AU, 0x5B9CCA4FU, 0x682E6FF3U,
    0x748F82EEU, 0x78A5636FU, 0x84C87814U, 0x8CC70208U, 0x90BEFFFAU, 0xA4506CEBU, 0xBEF9A3F7U, 0xC67178F2U};

constexpr std::array<std::uint32_t, kSha256StateWords> kInitialState{
    0x6A09E667U, 0xBB67AE85U, 0x3C6EF372U, 0xA54FF53AU, 0x510E527FU, 0x9B05688CU, 0x1F83D9ABU, 0x5BE0CD19U};

constexpr std::array<std::uint32_t, 6> kRotationA{2U, 13U, 22U};
constexpr std::array<std::uint32_t, 6> kRotationE{6U, 11U, 25U};
// NOLINTEND(cppcoreguidelines-avoid-magic-numbers)

constexpr std::uint32_t RotateRight(std::uint32_t value, unsigned count) noexcept {
    return (value >> count) | (value << (kSha256WordBits - count));
}

/// Reads four big-endian bytes into one word.
constexpr std::uint32_t ReadBigEndian(std::span<const std::byte> bytes) noexcept {
    std::uint32_t value = 0;
    for (const std::byte byte : bytes) {
        value = (value << kBitsPerByte) | std::to_integer<std::uint8_t>(byte);
    }
    return value;
}

class Sha256 {
  public:
    void Update(std::span<const std::byte> data) noexcept {
        total_bytes_ += data.size();
        for (const std::byte byte : data) {
            buffer_.at(buffer_size_) = std::to_integer<std::uint8_t>(byte);
            ++buffer_size_;
            if (buffer_size_ == buffer_.size()) {
                Transform();
                buffer_size_ = 0;
            }
        }
    }

    [[nodiscard]] std::array<std::uint32_t, kSha256StateWords> Final() noexcept {
        const std::uint64_t bit_length = total_bytes_ * kBitsPerByte;
        static constexpr std::array<std::byte, 1> kPadding{std::byte{0x80U}};
        static constexpr std::array<std::byte, 1> kZero{std::byte{0x00U}};
        Update(kPadding);
        while (buffer_size_ != kSha256PaddingOffset) {
            Update(kZero);
        }
        std::array<std::byte, kSha256LengthBytes> length_bytes{};
        for (std::size_t index = 0; index < length_bytes.size(); ++index) {
            const std::size_t shift = (kSha256LengthBytes - 1 - index) * kBitsPerByte;
            length_bytes.at(index) = static_cast<std::byte>(bit_length >> shift);
        }
        Update(length_bytes);
        return state_;
    }

  private:
    // NOLINTBEGIN(cppcoreguidelines-avoid-magic-numbers)
    void Transform() noexcept {
        std::array<std::uint32_t, kSha256ScheduleWords> schedule{};
        for (std::size_t index = 0; index < kSha256WordsPerBlock; ++index) {
            const std::size_t base = index * kSha256WordBytes;
            schedule.at(index) = ReadBigEndian(std::as_bytes(std::span{buffer_}).subspan(base, kSha256WordBytes));
        }
        for (std::size_t index = kSha256WordsPerBlock; index < kSha256ScheduleWords; ++index) {
            const std::uint32_t s0 = RotateRight(schedule.at(index - 15), 7) ^
                                     RotateRight(schedule.at(index - 15), 18) ^ (schedule.at(index - 15) >> 3U);
            const std::uint32_t s1 = RotateRight(schedule.at(index - 2), 17) ^ RotateRight(schedule.at(index - 2), 19) ^
                                     (schedule.at(index - 2) >> 10U);
            schedule.at(index) = schedule.at(index - 16) + s0 + schedule.at(index - 7) + s1;
        }

        std::uint32_t a = state_.at(0);
        std::uint32_t b = state_.at(1);
        std::uint32_t c = state_.at(2);
        std::uint32_t d = state_.at(3);
        std::uint32_t e = state_.at(4);
        std::uint32_t f = state_.at(5);
        std::uint32_t g = state_.at(6);
        std::uint32_t h = state_.at(7);
        for (std::size_t index = 0; index < kSha256ScheduleWords; ++index) {
            const std::uint32_t sum1 =
                RotateRight(e, kRotationE.at(0)) ^ RotateRight(e, kRotationE.at(1)) ^ RotateRight(e, kRotationE.at(2));
            const std::uint32_t choose = (e & f) ^ (~e & g);
            const std::uint32_t temp1 = h + sum1 + choose + kRoundConstants.at(index) + schedule.at(index);
            const std::uint32_t sum0 =
                RotateRight(a, kRotationA.at(0)) ^ RotateRight(a, kRotationA.at(1)) ^ RotateRight(a, kRotationA.at(2));
            const std::uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
            const std::uint32_t temp2 = sum0 + majority;
            h = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }
        state_.at(0) += a;
        state_.at(1) += b;
        state_.at(2) += c;
        state_.at(3) += d;
        state_.at(4) += e;
        state_.at(5) += f;
        state_.at(6) += g;
        state_.at(7) += h;
    }
    // NOLINTEND(cppcoreguidelines-avoid-magic-numbers)

    std::array<std::uint32_t, kSha256StateWords> state_ = kInitialState;
    std::array<std::uint8_t, kSha256BlockBytes> buffer_{};
    std::size_t buffer_size_ = 0;
    std::uint64_t total_bytes_ = 0;
};

std::string ToHex(const std::array<std::uint32_t, kSha256StateWords> &digest) {
    std::string hex;
    hex.reserve(kSha256DigestBytes * kHexDigitsPerByte);
    for (const std::uint32_t word : digest) {
        for (int shift = static_cast<int>(kSha256WordBits) - static_cast<int>(kNibbleBits); shift >= 0;
             shift -= static_cast<int>(kNibbleBits)) {
            const auto nibble = static_cast<std::size_t>((word >> static_cast<unsigned>(shift)) & kNibbleMask);
            hex.push_back(kHexDigits.at(nibble));
        }
    }
    return hex;
}

constexpr std::size_t kReadChunkKiB = 64;
constexpr std::size_t kBytesPerKiB = 1024;
constexpr std::size_t kReadChunkBytes = kReadChunkKiB * kBytesPerKiB;

} // namespace

std::string Sha256Hex(const std::uint8_t *data, std::size_t size) {
    Sha256 hasher;
    if (size > 0) {
        hasher.Update(std::as_bytes(std::span{data, size}));
    }
    return ToHex(hasher.Final());
}

std::optional<std::string> FileSha256Hex(const std::filesystem::path &path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream.is_open()) {
        return std::nullopt;
    }
    Sha256 hasher;
    std::array<char, kReadChunkBytes> buffer{};
    while (stream) {
        stream.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const std::streamsize read = stream.gcount();
        if (read > 0) {
            hasher.Update(std::as_bytes(std::span{buffer}.first(static_cast<std::size_t>(read))));
        }
    }
    if (!stream.eof()) {
        return std::nullopt; // a read error mid-file: never hash a partial file
    }
    return ToHex(hasher.Final());
}

} // namespace cg::glasses
