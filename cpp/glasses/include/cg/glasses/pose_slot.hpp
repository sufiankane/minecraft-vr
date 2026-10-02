#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

#include "ports.hpp"

namespace cg::glasses {

/// Single-writer / many-reader wait-free slot with seqlock semantics.
///
/// The writer is the polling thread; `TryRead` is called by any number of
/// readers. Two complete copies are kept, each behind its own even/odd
/// sequence counter: `payload_` (the newest sample) and `previous_` (the copy
/// published one step earlier). `Publish` snapshots the old newest into the
/// previous copy, then writes the new newest. `TryRead` copies the newest and
/// accepts it only when its counter is unchanged and even; if `Publish` keeps
/// overtaking the copy, the reader spends the same bounded budget
/// (`kMaxReadAttempts`) on the previous copy, which the in-flight publish does
/// not touch. Only if both budgets are exhausted is the last previous copy
/// returned unvalidated: that is the documented best-effort case, reachable
/// only against a writer that never lets up for that many attempts (real
/// polling threads block between samples). The validated paths never tear.
///
/// `Publish` and `TryRead` take no lock, allocate nothing and are `noexcept`.
/// `TryRead` returns false until the first `Publish` has completed.
class PoseSlot {
  public:
    /// Bounded reader retry count per copy before the fallback is returned.
    static constexpr int kMaxReadAttempts = 64;

    PoseSlot() noexcept = default;
    PoseSlot(const PoseSlot &) = delete;
    PoseSlot &operator=(const PoseSlot &) = delete;

    /// Publishes `sample` as the newest value. Single writer only.
    void Publish(const HeadSample &sample) noexcept {
        const std::uint64_t previous_begin = previous_sequence_.load(std::memory_order_relaxed);
        previous_sequence_.store(previous_begin + 1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        std::memcpy(previous_, payload_, sizeof(HeadSample));
        std::atomic_thread_fence(std::memory_order_release);
        previous_sequence_.store(previous_begin + 2, std::memory_order_release);

        const std::uint64_t begin = sequence_.load(std::memory_order_relaxed);
        sequence_.store(begin + 1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        std::memcpy(payload_, &sample, sizeof(HeadSample));
        std::atomic_thread_fence(std::memory_order_release);
        sequence_.store(begin + 2, std::memory_order_release);
        published_.store(true, std::memory_order_release);
    }

    /// Copies the newest fully published sample into `out`. Returns false
    /// before the first publish. Wait-free apart from the bounded retries.
    [[nodiscard]] bool TryRead(HeadSample &out) const noexcept {
        if (!published_.load(std::memory_order_acquire)) {
            return false;
        }
        if (TryReadCopy(sequence_, payload_, out)) {
            return true;
        }
        if (TryReadCopy(previous_sequence_, previous_, out)) {
            return true;
        }
        std::memcpy(&out, previous_, sizeof(HeadSample));
        return true;
    }

  private:
    static_assert(std::is_trivially_copyable_v<HeadSample>, "the seqlock payload must be trivially copyable");
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free, "the seqlock counter must be lock-free");

    [[nodiscard]] bool TryReadCopy(const std::atomic<std::uint64_t> &sequence, const std::byte *buffer,
                                   HeadSample &out) const noexcept {
        for (int attempt = 0; attempt < kMaxReadAttempts; ++attempt) {
            const std::uint64_t begin = sequence.load(std::memory_order_acquire);
            if ((begin & 1U) != 0U) {
                continue;
            }
            std::memcpy(&out, buffer, sizeof(HeadSample));
            std::atomic_thread_fence(std::memory_order_acquire);
            if (sequence.load(std::memory_order_relaxed) == begin) {
                return true;
            }
        }
        return false;
    }

    std::byte payload_[sizeof(HeadSample)]{};
    std::byte previous_[sizeof(HeadSample)]{};
    std::atomic<std::uint64_t> sequence_{0};
    std::atomic<std::uint64_t> previous_sequence_{0};
    std::atomic<bool> published_{false};
};

} // namespace cg::glasses
