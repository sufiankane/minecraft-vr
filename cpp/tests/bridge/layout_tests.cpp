// Layout contract tests for the dossier 5.6 shared-memory region (S6 Task 1a).
//
// Every offset in the 5.6 table is pinned at compile time. The runtime cases
// re-check the table values and the little-endian ASCII magic bytes. Where the
// compiler inserts padding (the table's shorthand sizes omit it), the test
// documents the inserted bytes and asserts the resulting totals.

#include "cg/bridge/shm_layout.hpp"
#include "cg_types.h"

#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <type_traits>

#include <gtest/gtest.h>

namespace cg::bridge {
namespace {

static_assert(CG_ABI_VERSION == 2, "R42: the hand frame addition bumps the contract version");

static_assert(std::is_trivially_copyable_v<cg_head_sample>);
static_assert(std::is_trivially_copyable_v<cg_hand>);
static_assert(std::is_trivially_copyable_v<cg_hand_frame>);

// 5.6 header table: magic at 0, abi_version at 8, header_size at 12,
// writer_pid at 16, heartbeat_ns at 24 and a 32-byte reserved window at 32.
static_assert(offsetof(ShmHeader, magic) == 0);
static_assert(offsetof(ShmHeader, abi_version) == 8);
static_assert(offsetof(ShmHeader, header_size) == 12);
static_assert(offsetof(ShmHeader, writer_pid) == 16);
static_assert(offsetof(ShmHeader, heartbeat_ns) == 24);
static_assert(sizeof(ShmHeader) == 64);
static_assert(kHeaderSize == 64);

// R43: the reserved window (bytes 32..63) now carries the command word at 32
// and its ack at 36; the remaining 24 bytes stay reserved. Prior readers skip
// the whole window, so the extension needs no shm ABI bump.
static_assert(offsetof(ShmHeader, command) == 32);
static_assert(offsetof(ShmHeader, ack) == 36);
static_assert(offsetof(ShmHeader, reserved) == 40);

// 5.6 lists "36 cg_head_sample": 36 is where `state` starts (8 bytes of
// host_time plus 28 bytes of pose), not the struct size. The C struct also
// carries `state` and `sequence`, so the field bytes total 44; the 8-byte
// alignment of `cg_time_ns` rounds the struct to 48 bytes (4 bytes of tail
// padding that the dossier table did not account for).
static_assert(offsetof(cg_head_sample, host_time) == 0);
static_assert(offsetof(cg_head_sample, pose) == 8);
static_assert(offsetof(cg_head_sample, state) == 36);
static_assert(offsetof(cg_head_sample, sequence) == 40);
static_assert(sizeof(cg_head_sample) == 44 + 4);
static_assert(alignof(cg_head_sample) == 8);

// 5.6 hand frame: capture_time 0, publish_time 8, predicted_for 16,
// sequence 24, hands 28. `hands` is 2 * 272 bytes, so the field bytes total
// 572; the struct's 8-byte alignment rounds that to 576 (4 bytes of tail
// padding inserted by the compiler).
static_assert(offsetof(cg_hand_frame, capture_time) == 0);
static_assert(offsetof(cg_hand_frame, publish_time) == 8);
static_assert(offsetof(cg_hand_frame, predicted_for) == 16);
static_assert(offsetof(cg_hand_frame, sequence) == 24);
static_assert(offsetof(cg_hand_frame, hands) == 28);
static_assert(sizeof(cg_hand_frame) == 572 + 4);
static_assert(alignof(cg_hand_frame) == 8);

// cg_hand: 4 header bytes, then 21 joints and a wrist velocity, all float3.
static_assert(offsetof(cg_hand, present) == 0);
static_assert(offsetof(cg_hand, handedness) == 1);
static_assert(offsetof(cg_hand, reserved) == 2);
static_assert(offsetof(cg_hand, confidence) == 4);
static_assert(offsetof(cg_hand, joints) == 8);
static_assert(offsetof(cg_hand, velocity) == 8 + 21 * 12);
static_assert(sizeof(cg_hand) == 8 + 21 * 12 + 12);

// Slot boundaries: HeadSlot at 64 and HandSlot at 256, each opening with an
// 8-byte seqlock counter, then the payload, then the second counter.
static_assert(kHeadSlotOffset == 64);
static_assert(kHandSlotOffset == 256);
static_assert(sizeof(HeadSlot) == 8 + sizeof(cg_head_sample) + 8);
static_assert(sizeof(HeadSlot) <= kHandSlotOffset - kHeadSlotOffset);
static_assert(offsetof(HeadSlot, seq_a) == 0);
static_assert(offsetof(HeadSlot, sample) == 8);
static_assert(offsetof(HeadSlot, seq_b) == 8 + sizeof(cg_head_sample));
static_assert(sizeof(HandSlot) == 8 + sizeof(cg_hand_frame) + 8);
static_assert(offsetof(HandSlot, seq_a) == 0);
static_assert(offsetof(HandSlot, frame) == 8);
static_assert(offsetof(HandSlot, seq_b) == 8 + sizeof(cg_hand_frame));

// The seqlock counters must be lock-free on the render path.
static_assert(std::atomic<std::uint64_t>::is_always_lock_free);

TEST(ShmLayout, MagicBytesAreCgshm001) {
    constexpr auto bytes = std::bit_cast<std::array<char, 8>>(kShmMagic);
    EXPECT_EQ(std::string(bytes.data(), bytes.size()), "CGSHM001");
}

TEST(ShmLayout, HeaderOffsetsMatchDossier56) {
    EXPECT_EQ(offsetof(ShmHeader, magic), 0U);
    EXPECT_EQ(offsetof(ShmHeader, abi_version), 8U);
    EXPECT_EQ(offsetof(ShmHeader, header_size), 12U);
    EXPECT_EQ(offsetof(ShmHeader, writer_pid), 16U);
    EXPECT_EQ(offsetof(ShmHeader, heartbeat_ns), 24U);
    EXPECT_EQ(offsetof(ShmHeader, command), 32U);
    EXPECT_EQ(offsetof(ShmHeader, ack), 36U);
    EXPECT_EQ(offsetof(ShmHeader, reserved) + 24, 64U);
    EXPECT_EQ(sizeof(ShmHeader), 64U);
}

TEST(ShmLayout, SlotOffsetsMatchDossier56) {
    EXPECT_EQ(kHeadSlotOffset, 64U);
    EXPECT_EQ(kHandSlotOffset, 256U);
    EXPECT_EQ(kHeadSlotOffset + sizeof(HeadSlot), 128U);
    EXPECT_LE(sizeof(HeadSlot), 192U);
    EXPECT_LE(kHandSlotOffset + sizeof(HandSlot), 1024U);
}

TEST(ShmLayout, ProtocolConstantsMatchDossier56) {
    EXPECT_EQ(kShmAbiVersion, 1U);
    EXPECT_EQ(kStaleAfterNs, 250'000'000);
    EXPECT_STREQ(kStateName, "Local\\cubeglass.v1.state");
}

TEST(ShmLayout, PayloadSizesDocumentTheCompilerPadding) {
    EXPECT_EQ(sizeof(cg_head_sample), 48U);
    EXPECT_EQ(sizeof(cg_hand), 272U);
    EXPECT_EQ(sizeof(cg_hand_frame), 576U);
    EXPECT_EQ(sizeof(HeadSlot), 64U);
    EXPECT_EQ(sizeof(HandSlot), 592U);
}

} // namespace
} // namespace cg::bridge
