// Concurrency stress for the 5.12 reader against the test-only writer
// (S6 Task 1b). One writer thread publishes 200k samples while reader threads
// hammer `cg_bridge_read_head` (8 readers) or `cg_bridge_read_hands` (4
// readers) until it finishes. The writer derives every payload field from the
// iteration counter; the reader recomputes them from `sequence`, so a torn or
// unvalidated copy would show up as a mismatch. Reader retries are bounded by
// construction (64 attempts per call), so a reader can observe a timeout, but
// never a torn sample and never an unbounded loop.
//
// CI scheduling: a reader can be starved for the whole writer run on a small
// runner, so each reader must observe at least one valid sample regardless.
// A start barrier puts every reader inside its loop before the writer begins,
// and after the writer stops each reader performs one bounded post-writer
// read of the (now stable) final sample; that read is validated and counted
// like any other. The per-reader successful count pins `> 0` without relying
// on the scheduler giving any reader a turn during the writer's run.

#include "cg/bridge/shm_layout.hpp"
#include "cg/bridge/test_writer.h"
#include "cg_unity_bridge.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <string>
#include <thread>
#include <vector>

#include <gtest/gtest.h>

namespace cg::bridge {
namespace {

constexpr std::uint32_t kSampleCount = 200'000;

std::int64_t now_ns() {
    const auto elapsed = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
}

/// Blocks until one more reader has arrived; the main thread waits with
/// `WaitForReaders`. Yielding keeps a starved runner from spinning forever.
void JoinStartBarrier(std::atomic<int> &ready, int reader_count) {
    ready.fetch_add(1, std::memory_order_release);
    while (ready.load(std::memory_order_acquire) < reader_count) {
        std::this_thread::yield();
    }
}

void WaitForReaders(const std::atomic<int> &ready, int reader_count) {
    while (ready.load(std::memory_order_acquire) < reader_count) {
        std::this_thread::yield();
    }
}

void FillHead(cg_head_sample &sample, std::uint32_t sequence) {
    const float value = static_cast<float>(sequence);
    sample.host_time = static_cast<cg_time_ns>(sequence) * 1'000'000;
    sample.pose.p = cg_vec3{value, -value, value};
    sample.pose.q = cg_quat{1.0f, value, -value, value};
    sample.state = CG_TRACK_STABLE;
    sample.sequence = sequence;
}

bool HeadMatchesSequence(const cg_head_sample &sample) {
    if (sample.sequence == 0) {
        return false;
    }
    const float value = static_cast<float>(sample.sequence);
    return sample.host_time == static_cast<cg_time_ns>(sample.sequence) * 1'000'000 && sample.pose.p.x == value &&
           sample.pose.p.y == -value && sample.pose.p.z == value && sample.pose.q.w == 1.0f &&
           sample.pose.q.x == value && sample.pose.q.y == -value && sample.pose.q.z == value &&
           (sample.state == CG_TRACK_STABLE || sample.state == CG_TRACK_LOST);
}

void FillHands(cg_hand_frame &frame, std::uint32_t sequence) {
    const float value = static_cast<float>(sequence);
    frame.capture_time = static_cast<cg_time_ns>(sequence);
    frame.publish_time = static_cast<cg_time_ns>(sequence) * 2;
    frame.predicted_for = static_cast<cg_time_ns>(sequence) * 3;
    frame.sequence = sequence;
    for (int hand = 0; hand < 2; ++hand) {
        const float handed = static_cast<float>(hand);
        cg_hand &current = frame.hands[hand];
        current.present = 1;
        current.handedness = static_cast<std::uint8_t>(hand);
        current.confidence = value;
        for (int joint = 0; joint < 21; ++joint) {
            current.joints[joint] = cg_vec3{value + static_cast<float>(joint), -value, handed};
        }
        current.velocity = cg_vec3{value, handed, -value};
    }
}

bool HandsMatchSequence(const cg_hand_frame &frame) {
    if (frame.sequence == 0) {
        return false;
    }
    const float value = static_cast<float>(frame.sequence);
    if (frame.capture_time != static_cast<cg_time_ns>(frame.sequence) ||
        frame.publish_time != static_cast<cg_time_ns>(frame.sequence) * 2 ||
        frame.predicted_for != static_cast<cg_time_ns>(frame.sequence) * 3) {
        return false;
    }
    for (int hand = 0; hand < 2; ++hand) {
        const float handed = static_cast<float>(hand);
        const cg_hand &current = frame.hands[hand];
        if (current.present != 1 || current.handedness != static_cast<std::uint8_t>(hand) ||
            current.confidence != value || current.velocity.x != value || current.velocity.y != handed ||
            current.velocity.z != -value) {
            return false;
        }
        for (int joint = 0; joint < 21; ++joint) {
            const cg_vec3 expected{value + static_cast<float>(joint), -value, handed};
            if (current.joints[joint].x != expected.x || current.joints[joint].y != expected.y ||
                current.joints[joint].z != expected.z) {
                return false;
            }
        }
    }
    return true;
}

struct ReadCounters {
    std::atomic<std::uint64_t> ok{0};
    std::atomic<std::uint64_t> not_ready{0};
    std::atomic<std::uint64_t> timeout{0};
    std::atomic<std::uint64_t> mismatches{0};
    std::atomic<std::uint64_t> unexpected{0};
    std::atomic<std::uint64_t> post_read_failures{0};

    void Record() const {
        ::testing::Test::RecordProperty("ok_reads", std::to_string(ok.load()));
        ::testing::Test::RecordProperty("not_ready_reads", std::to_string(not_ready.load()));
        ::testing::Test::RecordProperty("timeout_reads", std::to_string(timeout.load()));
        ::testing::Test::RecordProperty("mismatched_reads", std::to_string(mismatches.load()));
        ::testing::Test::RecordProperty("unexpected_reads", std::to_string(unexpected.load()));
        ::testing::Test::RecordProperty("post_read_failures", std::to_string(post_read_failures.load()));
    }

    void ExpectConsistent() const {
        EXPECT_EQ(mismatches.load(), 0U);
        EXPECT_EQ(unexpected.load(), 0U);
        EXPECT_GT(ok.load(), 0U);
    }
};

void ExpectEachReaderObservedASample(const std::vector<std::uint64_t> &per_reader_ok) {
    for (std::size_t reader = 0; reader < per_reader_ok.size(); ++reader) {
        EXPECT_GT(per_reader_ok[reader], 0U) << "reader " << reader << " observed no valid sample";
    }
}

TEST(ShmStress, HeadReadsStayConsistentUnderEightReaders) {
    ASSERT_EQ(cg_test_writer_create(), CG_OK);
    void *handle = nullptr;
    ASSERT_EQ(cg_bridge_open(&handle), CG_OK);

    constexpr int kReaders = 8;
    ReadCounters counters;
    std::atomic<bool> finished{false};
    std::atomic<int> ready{0};
    std::vector<std::uint64_t> per_reader_ok(static_cast<std::size_t>(kReaders), 0);
    std::vector<std::thread> readers;
    readers.reserve(kReaders);
    for (int reader = 0; reader < kReaders; ++reader) {
        readers.emplace_back([&, reader] {
            JoinStartBarrier(ready, kReaders);
            std::uint64_t local_ok = 0;
            while (!finished.load(std::memory_order_acquire)) {
                cg_head_sample sample{};
                const cg_status status = cg_bridge_read_head(handle, &sample);
                if (status == CG_OK) {
                    local_ok += 1;
                    counters.ok.fetch_add(1, std::memory_order_relaxed);
                    if (!HeadMatchesSequence(sample)) {
                        counters.mismatches.fetch_add(1, std::memory_order_relaxed);
                    }
                } else if (status == CG_ERR_NOT_READY) {
                    counters.not_ready.fetch_add(1, std::memory_order_relaxed);
                } else if (status == CG_ERR_TIMEOUT) {
                    counters.timeout.fetch_add(1, std::memory_order_relaxed);
                } else {
                    counters.unexpected.fetch_add(1, std::memory_order_relaxed);
                }
            }
            // The writer has stopped publishing, so the final sample is stable
            // and this bounded read must return it for every reader.
            cg_head_sample sample{};
            const cg_status status = cg_bridge_read_head(handle, &sample);
            if (status == CG_OK) {
                if (HeadMatchesSequence(sample)) {
                    local_ok += 1;
                    counters.ok.fetch_add(1, std::memory_order_relaxed);
                } else {
                    counters.mismatches.fetch_add(1, std::memory_order_relaxed);
                }
            } else {
                counters.post_read_failures.fetch_add(1, std::memory_order_relaxed);
            }
            per_reader_ok[static_cast<std::size_t>(reader)] = local_ok;
        });
    }

    // Every reader is inside its loop before the writer starts, so a starved
    // reader cannot miss the run entirely; the post-writer read is the
    // guaranteed floor.
    WaitForReaders(ready, kReaders);

    bool published = true;
    for (std::uint32_t sequence = 1; sequence <= kSampleCount; ++sequence) {
        cg_head_sample sample{};
        FillHead(sample, sequence);
        if (cg_test_writer_set_heartbeat(now_ns()) != CG_OK || cg_test_writer_publish_head(&sample) != CG_OK) {
            published = false;
            break;
        }
    }
    // The guaranteed post-writer reads must see fresh data; refresh the
    // heartbeat after the last publish. Head staleness still returns CG_OK
    // (with state Lost), but hands staleness returns CG_ERR_NOT_READY, so both
    // paths refresh to keep the post-read floor consistent.
    if (cg_test_writer_set_heartbeat(now_ns()) != CG_OK) {
        published = false;
    }
    finished.store(true, std::memory_order_release);
    for (std::thread &reader : readers) {
        reader.join();
    }

    EXPECT_TRUE(published);
    counters.Record();
    counters.ExpectConsistent();
    EXPECT_EQ(counters.post_read_failures.load(), 0U);
    ExpectEachReaderObservedASample(per_reader_ok);

    cg_bridge_close(handle);
    cg_test_writer_close();
}

TEST(ShmStress, HandReadsStayConsistentUnderFourReaders) {
    ASSERT_EQ(cg_test_writer_create(), CG_OK);
    void *handle = nullptr;
    ASSERT_EQ(cg_bridge_open(&handle), CG_OK);
    // A far-future heartbeat keeps the mid-run reads fresh for the first
    // second of the run; the heartbeat is refreshed after the last publish so
    // the guaranteed post-writer read never sees stale hands (stale hands
    // return CG_ERR_NOT_READY, not a sample).
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() + 1'000'000'000), CG_OK);

    constexpr int kReaders = 4;
    ReadCounters counters;
    std::atomic<bool> finished{false};
    std::atomic<int> ready{0};
    std::vector<std::uint64_t> per_reader_ok(static_cast<std::size_t>(kReaders), 0);
    std::vector<std::thread> readers;
    readers.reserve(kReaders);
    for (int reader = 0; reader < kReaders; ++reader) {
        readers.emplace_back([&, reader] {
            JoinStartBarrier(ready, kReaders);
            std::uint64_t local_ok = 0;
            while (!finished.load(std::memory_order_acquire)) {
                cg_hand_frame frame{};
                const cg_status status = cg_bridge_read_hands(handle, &frame);
                if (status == CG_OK) {
                    local_ok += 1;
                    counters.ok.fetch_add(1, std::memory_order_relaxed);
                    if (!HandsMatchSequence(frame)) {
                        counters.mismatches.fetch_add(1, std::memory_order_relaxed);
                    }
                } else if (status == CG_ERR_NOT_READY) {
                    counters.not_ready.fetch_add(1, std::memory_order_relaxed);
                } else if (status == CG_ERR_TIMEOUT) {
                    counters.timeout.fetch_add(1, std::memory_order_relaxed);
                } else {
                    counters.unexpected.fetch_add(1, std::memory_order_relaxed);
                }
            }
            // Same post-writer floor as the head test: the last frame is stable.
            cg_hand_frame frame{};
            const cg_status status = cg_bridge_read_hands(handle, &frame);
            if (status == CG_OK) {
                if (HandsMatchSequence(frame)) {
                    local_ok += 1;
                    counters.ok.fetch_add(1, std::memory_order_relaxed);
                } else {
                    counters.mismatches.fetch_add(1, std::memory_order_relaxed);
                }
            } else {
                counters.post_read_failures.fetch_add(1, std::memory_order_relaxed);
            }
            per_reader_ok[static_cast<std::size_t>(reader)] = local_ok;
        });
    }

    WaitForReaders(ready, kReaders);

    bool published = true;
    for (std::uint32_t sequence = 1; sequence <= kSampleCount; ++sequence) {
        cg_hand_frame frame{};
        FillHands(frame, sequence);
        if (cg_test_writer_publish_hands(&frame) != CG_OK) {
            published = false;
            break;
        }
    }
    // Same post-publish heartbeat refresh as the head test: the guaranteed
    // post-writer read must not be rejected as stale.
    if (cg_test_writer_set_heartbeat(now_ns()) != CG_OK) {
        published = false;
    }
    finished.store(true, std::memory_order_release);
    for (std::thread &reader : readers) {
        reader.join();
    }

    EXPECT_TRUE(published);
    counters.Record();
    counters.ExpectConsistent();
    EXPECT_EQ(counters.post_read_failures.load(), 0U);
    ExpectEachReaderObservedASample(per_reader_ok);

    cg_bridge_close(handle);
    cg_test_writer_close();
}

} // namespace
} // namespace cg::bridge
