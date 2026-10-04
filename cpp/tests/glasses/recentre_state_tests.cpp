// CXX-13: the reader-visible recentre correction (offset + until_seq +
// pending + generation) must be read as one snapshot. The deterministic test
// interleaves a publish between the two halves of a read; the threaded test
// checks that every concurrent read is a whole snapshot.
#include "cg/glasses/recentre_state.hpp"

#include <atomic>
#include <cstdint>
#include <thread>
#include <utility>

#include <gtest/gtest.h>

namespace cg::glasses::test {
namespace {

TEST(RecentreStateTest, UnchangedStateRoundTripsWhole) {
    RecentreState state;
    state.Store({-10.0, 5, true, 1});
    EXPECT_EQ(state.Load(), (RecentreState::Value{-10.0, 5, true, 1}));
}

/// The old adapter read `until_seq`, then `pending`, then `offset` as separate
/// atomics: a publish between the reads paired the new offset with the old
/// until_seq (a one-frame yaw glitch). Here the publish lands exactly between
/// the two halves of a read: the attempt must be rejected, and the production
/// reader must return the whole new snapshot, never the mixed pair.
TEST(RecentreStateTest, PublishBetweenTheFieldReadsIsDetectedAndNeverPaired) {
    RecentreState state;
    state.Store({-10.0, 5, true, 1});

    const RecentreState::LoadCursor cursor = state.BeginLoad();
    ASSERT_EQ(cursor.until_seq, 5); // first half read epoch 1's until_seq

    state.Store({-20.0, 9, false, 2}); // a second recentre lands mid-read

    RecentreState::Value value{};
    EXPECT_FALSE(state.FinishLoad(cursor, value)) << "a publish inside the read window must invalidate the attempt";
    EXPECT_EQ(value, (RecentreState::Value{})) << "a rejected attempt must not leak fields";

    const RecentreState::Value loaded = state.Load();
    EXPECT_EQ(loaded, (RecentreState::Value{-20.0, 9, false, 2}));
    EXPECT_NE(std::make_pair(loaded.yaw_offset_deg, loaded.until_seq), std::make_pair(-20.0, 5.0))
        << "the reader must never pair the new offset with the old until_seq";
}

TEST(RecentreStateTest, ConcurrentPublishesAreAlwaysWholeSnapshots) {
    RecentreState state;
    std::atomic<bool> stop{false};
    std::atomic<int> torn{0};

    std::thread writer([&] {
        for (std::uint64_t generation = 1; generation <= 20000; ++generation) {
            // Encoded relation: offset = -generation, until = generation,
            // pending = generation is odd. Every accepted read must preserve it.
            state.Store({-static_cast<double>(generation), static_cast<std::int64_t>(generation),
                         (generation % 2U) != 0U, generation});
        }
        stop.store(true, std::memory_order_release);
    });

    while (!stop.load(std::memory_order_acquire)) {
        const RecentreState::Value value = state.Load();
        if (value.generation == 0) {
            continue; // pre-first-publish (or an exhausted retry) is legal
        }
        const bool consistent = value.until_seq == static_cast<std::int64_t>(value.generation) &&
                                value.yaw_offset_deg == -static_cast<double>(value.generation) &&
                                value.pending == ((value.generation % 2U) != 0U);
        if (!consistent) {
            torn.fetch_add(1, std::memory_order_relaxed);
        }
    }
    writer.join();
    EXPECT_EQ(torn.load(), 0) << "a read paired fields from different publishes";
}

} // namespace
} // namespace cg::glasses::test
