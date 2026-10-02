#pragma once

// Shared-memory layout for the 5.6 head-pose and hand-frame region
// (`Local\cubeglass.v1.state`). The region is little-endian with 64-byte
// aligned blocks; the writer is `cg-handservice` and readers are read-only.
//
// Writer protocol: bump `seq_a` to an odd value, write the payload, store
// `seq_b = seq_a + 1`, then store `seq_a = seq_a + 1`. Reader protocol: read
// `seq_a` (retry while odd), copy the payload, read `seq_b`, and accept only
// when `seq_b == seq_a`. All counter operations use release/acquire ordering.
// A reader treats the data as stale when `now - heartbeat_ns > kStaleAfterNs`
// and reports `CG_TRACK_LOST` instead of the built-in tracking state.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <type_traits>

#include "cg_types.h"

namespace cg::bridge {

/// Magic at byte 0: the eight ASCII bytes `CGSHM001` read as a little-endian
/// `uint64_t` (byte 0 is the least significant byte), so the region's first
/// bytes are `43 47 53 48 4D 30 30 31`. Equivalently, the value is the reversed
/// byte order of the string: `1 0 0 M H S G C` as hex digits.
inline constexpr std::uint64_t kShmMagic = 0x3130304D48534743ull;

/// Shared-memory ABI version at byte 8 (the region ABI, not `CG_ABI_VERSION`).
inline constexpr std::uint32_t kShmAbiVersion = 1;

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
