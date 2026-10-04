#pragma once

#include <atomic>
#include <cstdint>

namespace cg::glasses {

/// The reader-visible read-time recentre correction, published as one
/// generation-tagged snapshot (CXX-13).
///
/// The correction applies to samples up to `until_seq`, or to every sample
/// while `pending` is set (a posted request is armed but not yet resolved
/// against the SDK). Publishing all fields through one seqlock means a reader
/// can never pair a newer offset with an older `until_seq` (or the reverse)
/// when two recentres overlap a read: it observes either the whole previous
/// snapshot or the whole new one.
///
/// Single writer (the adapter's `recentre_mutex_` holder). Readers are
/// wait-free with a bounded retry; if every attempt races a publish, `Load`
/// returns a no-correction snapshot rather than a torn one. Publishes are rare
/// and only a few relaxed stores long, so the retry is exhausted in practice
/// only under adversarial scheduling.
///
/// Protocol (mirrors `PoseSlot`):
/// - writer: relaxed odd sequence bump, release fence, relaxed payload stores,
///   release fence, release even sequence bump;
/// - reader: acquire sequence load, relaxed payload loads, acquire fence,
///   relaxed sequence re-load; accept only when the sequence is unchanged.
///
/// `BeginLoad`/`FinishLoad` split the read so a test can deterministically
/// interleave a publish between the first field read and the rest (the exact
/// window the old offset/until/sequence-loose reads left open).
class RecentreState {
  public:
    struct Value {
        double yaw_offset_deg = 0.0;
        std::int64_t until_seq = 0;
        bool pending = false;
        std::uint64_t generation = 0;

        friend bool operator==(const Value &, const Value &) = default;
    };

    /// Publishes `value` atomically with respect to `Load`. Single writer:
    /// callers hold the adapter's `recentre_mutex_`.
    void Store(const Value &value) noexcept {
        sequence_.fetch_add(1, std::memory_order_relaxed); // odd: write in progress
        std::atomic_thread_fence(std::memory_order_release);
        yaw_offset_deg_.store(value.yaw_offset_deg, std::memory_order_relaxed);
        until_seq_.store(value.until_seq, std::memory_order_relaxed);
        pending_.store(value.pending, std::memory_order_relaxed);
        generation_.store(value.generation, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        sequence_.fetch_add(1, std::memory_order_release); // even: published
    }

    /// A consistent snapshot, or `{}` (no correction) when every bounded
    /// attempt raced a publish.
    [[nodiscard]] Value Load() const noexcept {
        for (int attempt = 0; attempt < kMaxLoadAttempts; ++attempt) {
            const LoadCursor cursor = BeginLoad();
            if (cursor.sequence % 2U != 0U) {
                continue; // a publish was mid-write
            }
            Value value{};
            if (FinishLoad(cursor, value)) {
                return value;
            }
        }
        return Value{};
    }

    /// The first half of a read: the sequence this attempt validates against
    /// and the first payload field. Exposed for the deterministic CXX-13 test.
    struct LoadCursor {
        std::uint64_t sequence = 0;
        std::int64_t until_seq = 0;
    };

    [[nodiscard]] LoadCursor BeginLoad() const noexcept {
        LoadCursor cursor{};
        cursor.sequence = sequence_.load(std::memory_order_acquire);
        cursor.until_seq = until_seq_.load(std::memory_order_relaxed);
        return cursor;
    }

    /// The second half: reads the remaining fields and validates that no
    /// publish started between the halves. Returns false (and leaves `out`
    /// untouched) when the cursor is stale, so the caller retries `Load`.
    [[nodiscard]] bool FinishLoad(const LoadCursor &cursor, Value &out) const noexcept {
        Value value{};
        value.yaw_offset_deg = yaw_offset_deg_.load(std::memory_order_relaxed);
        value.pending = pending_.load(std::memory_order_relaxed);
        value.generation = generation_.load(std::memory_order_relaxed);
        value.until_seq = cursor.until_seq;
        std::atomic_thread_fence(std::memory_order_acquire);
        if (sequence_.load(std::memory_order_relaxed) != cursor.sequence) {
            return false;
        }
        out = value;
        return true;
    }

  private:
    static constexpr int kMaxLoadAttempts = 64;

    mutable std::atomic<std::uint64_t> sequence_{0};
    std::atomic<double> yaw_offset_deg_{0.0};
    std::atomic<std::int64_t> until_seq_{0};
    std::atomic<bool> pending_{false};
    std::atomic<std::uint64_t> generation_{0};
};

} // namespace cg::glasses
