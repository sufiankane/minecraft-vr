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
/// readers. The payload is copied word-wise through relaxed
/// `std::atomic_ref<std::uint64_t>` accesses, so every payload access is an
/// atomic operation and the implementation is TSan-clean by construction (no
/// annotations needed). The writer bumps an even/odd version counter around
/// the payload stores; a reader copies the payload and accepts it only when
/// the version is unchanged and even.
///
/// A reader that cannot obtain a validated copy within `kMaxReadAttempts`
/// attempts (a transient state under a writer that never lets up) returns
/// `false`, the R39 late-latch contract: the caller keeps its previous frame
/// and the next call normally succeeds. `TryRead` never returns a torn or
/// unvalidated sample. It returns false before the first publish; after that,
/// false is only the bounded-retry exhaustion case.
///
/// `Publish` and `TryRead` take no lock, allocate nothing, throw nothing and
/// have a bounded number of steps. `HeadSample` is asserted trivially copyable,
/// 8-byte sized and 8-byte aligned, and the atomic accesses are asserted
/// lock-free.
class PoseSlot {
  public:
    /// Bounded reader retry count before `TryRead` returns false.
    static constexpr int kMaxReadAttempts = 64;

    PoseSlot() noexcept = default;
    PoseSlot(const PoseSlot &) = delete;
    PoseSlot &operator=(const PoseSlot &) = delete;

    /// Publishes `sample` as the newest value. Single writer only.
    void Publish(const HeadSample &sample) noexcept {
        std::uint64_t words[kWordCount];
        std::memcpy(words, &sample, sizeof(HeadSample));

        const std::uint64_t begin = sequence_.load(std::memory_order_relaxed);
        sequence_.store(begin + 1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        for (std::size_t word = 0; word < kWordCount; ++word) {
            std::atomic_ref<std::uint64_t>(payload_[word]).store(words[word], std::memory_order_relaxed);
        }
        std::atomic_thread_fence(std::memory_order_release);
        sequence_.store(begin + 2, std::memory_order_release);
        published_.store(true, std::memory_order_release);
    }

    /// Copies the newest published sample into `out` when a consistent copy is
    /// available. Returns false before the first publish and on bounded-retry
    /// exhaustion; a returned sample has always passed the version validation.
    [[nodiscard]] bool TryRead(HeadSample &out) const noexcept {
        if (!published_.load(std::memory_order_acquire)) {
            return false;
        }
        for (int attempt = 0; attempt < kMaxReadAttempts; ++attempt) {
            const std::uint64_t begin = sequence_.load(std::memory_order_acquire);
            if ((begin & 1U) != 0U) {
                continue;
            }
            std::uint64_t words[kWordCount];
            for (std::size_t word = 0; word < kWordCount; ++word) {
                words[word] = std::atomic_ref<std::uint64_t>(payload_[word]).load(std::memory_order_relaxed);
            }
            std::atomic_thread_fence(std::memory_order_acquire);
            if (sequence_.load(std::memory_order_relaxed) != begin) {
                continue;
            }
            std::memcpy(&out, words, sizeof(HeadSample));
            return true;
        }
        return false;
    }

  private:
    static_assert(std::is_trivially_copyable_v<HeadSample>, "the seqlock payload must be trivially copyable");
    static_assert(sizeof(HeadSample) % sizeof(std::uint64_t) == 0, "the seqlock payload must be 8-byte sized");
    static_assert(alignof(HeadSample) <= alignof(std::uint64_t), "the seqlock payload must be 8-byte aligned");
    static_assert(std::atomic_ref<std::uint64_t>::is_always_lock_free, "the payload words must be lock-free");
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free, "the version counter must be lock-free");

    static constexpr std::size_t kWordCount = sizeof(HeadSample) / sizeof(std::uint64_t);

    mutable std::uint64_t payload_[kWordCount]{};
    std::atomic<std::uint64_t> sequence_{0};
    std::atomic<bool> published_{false};
};

} // namespace cg::glasses
