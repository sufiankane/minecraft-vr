#pragma once

// Shared-memory layout for the 5.6 head-pose and hand-frame region
// (`Local\cubeglass.v1.state`; the name keeps its v1 form per ADR-0010 even
// though the region ABI is 2). The region is little-endian with 64-byte
// aligned blocks; the writer is `cg-handservice` and readers are read-only.
//
// Writer protocol: bump `seq_a` to an odd value, write the payload, store
// `seq_b = seq_a + 1`, then store `seq_a = seq_a + 1`. Reader protocol: read
// `seq_a` (retry while odd), copy the payload, read `seq_b`, and accept only
// when `seq_b == seq_a`. All counter operations use release/acquire ordering.
// A reader treats the data as stale when `now - heartbeat_ns > kStaleAfterNs`:
// a stale head read keeps the sample but reports `CG_TRACK_LOST` instead of
// the built-in tracking state, and a stale hand read reports
// `CG_ERR_NOT_READY` (a lost hand frame is no frame).
//
// Header publication protocol (I-3): the writer initialises the header by
// zeroing it, storing `writer_pid`, `abi_version`, `header_size`, heartbeat
// and command/ack with relaxed stores, and only then publishing `magic` with
// a release store. `magic` is always the last field a reader can observe, so
// an acquire load of `magic` that reads `kShmMagic` happens-after every other
// field store, and the reader can read the remaining fields with relaxed
// loads. A reader that opens during initialisation never needs a fence of its
// own. The short retry window in `cg_bridge_open` is belt-and-braces for a
// reader that observed the region before the writer began initialising (or a
// writer that died mid-init); it is not the synchronisation primitive.
//
// A mixed-version pair is detected through `abi_version` alone: the region ABI
// is bumped whenever the payload shape changes (S6: the hand slot grew with
// `cg_hand_frame`), and the reader rejects a mismatch with
// `CG_ERR_UNSUPPORTED` naming both versions in `LastHeaderError()`.

#include <atomic>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <type_traits>

#include "cg_types.h"

namespace cg::bridge {

/// The region is little-endian by construction (CXX-20): the `CGSHM001` magic
/// is the reversed-byte-order bit pattern below, and the seqlock payloads are
/// copied as raw 8-byte words of native floats. A big-endian target needs a
/// new layout/ABI rather than silently misreading the region.
static_assert(std::endian::native == std::endian::little,
              "the shared-memory region is little-endian by construction (magic bit pattern and raw 8-byte word "
              "payloads); a big-endian target needs a new layout and ABI");

/// Magic at byte 0: the eight ASCII bytes `CGSHM001` read as a little-endian
/// `uint64_t` (byte 0 is the least significant byte), so the region's first
/// bytes are `43 47 53 48 4D 30 30 31`. Equivalently, the value is the reversed
/// byte order of the string: `1 0 0 M H S G C` as hex digits.
inline constexpr std::uint64_t kShmMagic = 0x3130304D48534743ull;

/// Shared-memory ABI version at byte 8 (the region ABI, not `CG_ABI_VERSION`).
///
/// Version 2: the hand slot carries `cg_hand_frame` (`cg_hand` in `CG_ABI_VERSION`
/// 2), so a pre-S6 writer/reader pair has a different slot payload shape. A
/// version-1 reader or writer must be rejected rather than silently reading
/// `seq_b` from the wrong offset (I-2).
inline constexpr std::uint32_t kShmAbiVersion = 2;

/// Size of the fixed header block, bytes 0..63.
inline constexpr std::uint32_t kHeaderSize = 64;

/// Windows shared-memory region name (dossier 5.6).
inline constexpr char kStateName[] = "Local\\cubeglass.v1.state";

/// Header offsets from the 5.6 table.
inline constexpr std::size_t kHeartbeatOffset = 24;
inline constexpr std::size_t kCommandOffset = 32;
inline constexpr std::size_t kAckOffset = 36;
inline constexpr std::size_t kHeadSlotOffset = 64;
inline constexpr std::size_t kHandSlotOffset = 256;

/// A sample whose writer heartbeat is older than this is stale (250 ms).
inline constexpr std::int64_t kStaleAfterNs = 250'000'000;

/// The 5.6 staleness rule, exclusive at the boundary: data is stale only when
/// `now - heartbeat` is strictly greater than 250 ms, so exactly 250 ms is
/// still fresh. `now` and `heartbeat` are nanoseconds from the same monotonic
/// clock (`std::chrono::steady_clock`).
///
/// A heartbeat ahead of `now` (clock skew, or a writer on a second clock) is
/// treated as fresh no matter how far ahead: a future stamp is not evidence of
/// a dead writer. The comparison is done in unsigned arithmetic so an extreme
/// pair (for example `now = INT64_MIN`, `heartbeat = INT64_MAX`) cannot
/// overflow (M-6).
inline bool is_stale(std::int64_t now, std::int64_t heartbeat) noexcept {
    if (heartbeat >= now) {
        return false; // future (or same-instant) heartbeat: fresh
    }
    const auto age = static_cast<std::uint64_t>(now) - static_cast<std::uint64_t>(heartbeat);
    return age > static_cast<std::uint64_t>(kStaleAfterNs);
}

/// Human-readable diagnostic for the last header rejected by `cg_bridge_open`
/// on the calling thread: names the observed and expected `abi_version` (or
/// header-size) values, empty when the last open succeeded. This is bridge
/// diagnostics, not part of the frozen 5.12 surface; tests and the C# wrapper
/// can surface it for support logs.
[[nodiscard]] const char *LastHeaderError() noexcept;

/// Fixed 64-byte region header. Bytes 32..39 were reserved in 5.6; ADR-0010
/// (R43) extends the reserved window with a command word and its ack, which
/// prior readers ignore. The tail of the window, bytes 40..63, stays reserved.
struct ShmHeader {
    std::uint64_t magic;
    std::uint32_t abi_version;
    std::uint32_t header_size;
    std::uint64_t writer_pid;
    std::int64_t heartbeat_ns;
    std::uint32_t command;
    std::uint32_t ack;
    std::uint8_t reserved[24];
};

static_assert(sizeof(ShmHeader) == kHeaderSize);
static_assert(offsetof(ShmHeader, magic) == 0);
static_assert(offsetof(ShmHeader, abi_version) == 8);
static_assert(offsetof(ShmHeader, header_size) == 12);
static_assert(offsetof(ShmHeader, writer_pid) == 16);
static_assert(offsetof(ShmHeader, heartbeat_ns) == kHeartbeatOffset);
static_assert(offsetof(ShmHeader, command) == kCommandOffset);
static_assert(offsetof(ShmHeader, ack) == kAckOffset);
static_assert(offsetof(ShmHeader, reserved) == 40);

/// HeadSlot seqlock at `kHeadSlotOffset`: counter, payload, counter.
struct HeadSlot {
    std::atomic<std::uint64_t> seq_a;
    cg_head_sample sample;
    std::atomic<std::uint64_t> seq_b;
};

/// HandSlot seqlock at `kHandSlotOffset`: counter, payload, counter.
struct HandSlot {
    std::atomic<std::uint64_t> seq_a;
    cg_hand_frame frame;
    std::atomic<std::uint64_t> seq_b;
};

static_assert(std::is_trivially_copyable_v<cg_head_sample> && std::is_trivially_copyable_v<cg_hand> &&
              std::is_trivially_copyable_v<cg_hand_frame>);
static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
static_assert(offsetof(HeadSlot, seq_a) == 0);
static_assert(offsetof(HeadSlot, sample) == 8);
static_assert(offsetof(HeadSlot, seq_b) == 8 + sizeof(cg_head_sample));
static_assert(sizeof(HeadSlot) <= kHandSlotOffset - kHeadSlotOffset);
static_assert(offsetof(HandSlot, seq_a) == 0);
static_assert(offsetof(HandSlot, frame) == 8);
static_assert(offsetof(HandSlot, seq_b) == 8 + sizeof(cg_hand_frame));

} // namespace cg::bridge
