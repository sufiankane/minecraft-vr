// Minimal FIPS 180-4 SHA-256 for the loader's optional vendor-DLL hash pin
// (TD-051). The algorithm is self-contained so cg_glasses gains no crypto
// dependency; it is used for accidental-corruption / wrong-build detection and
// explicitly not a substitute for signature verification (the documented
// residual in viture_loader.hpp).

#include "cg/glasses/file_hash.hpp"

#include <array>
#include <cstring>
#include <fstream>

namespace cg::glasses {

namespace {

constexpr std::array<std::uint32_t, 64> kRoundConstants{
    0x428A2F98U, 0x71374491U, 0xB5C0FBCFU, 0xE9B5DBA5U, 0x3956C25BU, 0x59F111F1U, 0x923F82A4U, 0xAB1C5ED5U,
    0xD807AA98U, 0x12835B01U, 0x243185BEU, 0x550C7DC3U, 0x72BE5D74U, 0x80DEB1FEU, 0x9BDC06A7U, 0xC19BF174U,
    0xE49B69C1U, 0xEFBE4786U, 0x0FC19DC6U, 0x240CA1CCU, 0x2DE92C6FU, 0x4A7484AAU, 0x5CB0A9DCU, 0x76F988DAU,
    0x983E5152U, 0xA831C66DU, 0xB00327C8U, 0xBF597FC7U, 0xC6E00BF3U, 0xD5A79147U, 0x06CA6351U, 0x14292967U,
    0x27B70A85U, 0x2E1B2138U, 0x4D2C6DFCU, 0x53380D13U, 0x650A7354U, 0x766A0ABBU, 0x81C2C92EU, 0x92722C85U,
    0xA2BFE8A1U, 0xA81A664BU, 0xC24B8B70U, 0xC76C51A3U, 0xD192E819U, 0xD6990624U, 0xF40E3585U, 0x106AA070U,
    0x19A4C116U, 0x1E376C08U, 0x2748774CU, 0x34B0BCB5U, 0x391C0CB3U, 0x4ED8AA4AU, 0x5B9CCA4FU, 0x682E6FF3U,
    0x748F82EEU, 0x78A5636FU, 0x84C87814U, 0x8CC70208U, 0x90BEFFFAU, 0xA4506CEBU, 0xBEF9A3F7U, 0xC67178F2U};

constexpr std::array<std::uint32_t, 8> kInitialState{0x6A09E667U, 0xBB67AE85U, 0x3C6EF372U, 0xA54FF53AU,
                                                     0x510E527FU, 0x9B05688CU, 0x1F83D9ABU, 0x5BE0CD19U};

constexpr std::uint32_t RotateRight(std::uint32_t value, unsigned count) noexcept {
    return (value >> count) | (value << (32U - count));
}

class Sha256 {
  public:
    void Update(const std::uint8_t *data, std::size_t size) noexcept {
        total_bytes_ += size;
        for (std::size_t index = 0; index < size; ++index) {
            buffer_[buffer_size_++] = data[index];
            if (buffer_size_ == buffer_.size()) {
                Transform(buffer_.data());
                buffer_size_ = 0;
            }
        }
    }

    [[nodiscard]] std::array<std::uint32_t, 8> Final() noexcept {
        const std::uint64_t bit_length = total_bytes_ * 8U;
        const std::uint8_t padding = 0x80U;
        Update(&padding, 1);
        const std::uint8_t zero = 0x00U;
        while (buffer_size_ != 56U) {
            Update(&zero, 1);
        }
        std::uint8_t length_bytes[8];
        for (std::size_t index = 0; index < 8; ++index) {
            length_bytes[index] = static_cast<std::uint8_t>(bit_length >> (56U - 8U * index));
        }
        Update(length_bytes, sizeof(length_bytes));
        return state_;
    }

  private:
    void Transform(const std::uint8_t *block) noexcept {
        std::uint32_t schedule[64];
        for (std::size_t index = 0; index < 16; ++index) {
            schedule[index] = (static_cast<std::uint32_t>(block[index * 4]) << 24U) |
                              (static_cast<std::uint32_t>(block[index * 4 + 1]) << 16U) |
                              (static_cast<std::uint32_t>(block[index * 4 + 2]) << 8U) |
                              static_cast<std::uint32_t>(block[index * 4 + 3]);
        }
        for (std::size_t index = 16; index < 64; ++index) {
            const std::uint32_t s0 = RotateRight(schedule[index - 15], 7) ^ RotateRight(schedule[index - 15], 18) ^
                                     (schedule[index - 15] >> 3U);
            const std::uint32_t s1 = RotateRight(schedule[index - 2], 17) ^ RotateRight(schedule[index - 2], 19) ^
                                     (schedule[index - 2] >> 10U);
            schedule[index] = schedule[index - 16] + s0 + schedule[index - 7] + s1;
        }

        std::uint32_t a = state_[0];
        std::uint32_t b = state_[1];
        std::uint32_t c = state_[2];
        std::uint32_t d = state_[3];
        std::uint32_t e = state_[4];
        std::uint32_t f = state_[5];
        std::uint32_t g = state_[6];
        std::uint32_t h = state_[7];
        for (std::size_t index = 0; index < 64; ++index) {
            const std::uint32_t sum1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
            const std::uint32_t choose = (e & f) ^ (~e & g);
            const std::uint32_t temp1 = h + sum1 + choose + kRoundConstants[index] + schedule[index];
            const std::uint32_t sum0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
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
        state_[0] += a;
        state_[1] += b;
        state_[2] += c;
        state_[3] += d;
        state_[4] += e;
        state_[5] += f;
        state_[6] += g;
        state_[7] += h;
    }

    std::array<std::uint32_t, 8> state_ = kInitialState;
    std::array<std::uint8_t, 64> buffer_{};
    std::size_t buffer_size_ = 0;
    std::uint64_t total_bytes_ = 0;
};

std::string ToHex(const std::array<std::uint32_t, 8> &digest) {
    constexpr char kHexDigits[] = "0123456789abcdef";
    std::string hex;
    hex.reserve(64);
    for (const std::uint32_t word : digest) {
        for (int shift = 28; shift >= 0; shift -= 4) {
            hex.push_back(kHexDigits[(word >> static_cast<unsigned>(shift)) & 0xFU]);
        }
    }
    return hex;
}

} // namespace

std::string Sha256Hex(const std::uint8_t *data, std::size_t size) {
    Sha256 hasher;
    if (size > 0) {
        hasher.Update(data, size);
    }
    return ToHex(hasher.Final());
}

std::optional<std::string> FileSha256Hex(const std::filesystem::path &path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream.is_open()) {
        return std::nullopt;
    }
    Sha256 hasher;
    std::array<char, 64 * 1024> buffer{};
    while (stream) {
        stream.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const std::streamsize read = stream.gcount();
        if (read > 0) {
            hasher.Update(reinterpret_cast<const std::uint8_t *>(buffer.data()), static_cast<std::size_t>(read));
        }
    }
    if (!stream.eof()) {
        return std::nullopt; // a read error mid-file: never hash a partial file
    }
    return ToHex(hasher.Final());
}

} // namespace cg::glasses
