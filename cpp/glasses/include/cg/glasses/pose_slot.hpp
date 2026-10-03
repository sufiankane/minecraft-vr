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
/// false is only the bounded-retry exhaustion case. The test-only
/// `SetTestPublishHook` seam (see its comment) makes that case deterministic
/// for `cpp/tests/glasses/thread_safety_tests.cpp`.
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
        if (test_publish_hook_ != nullptr) {
            test_publish_hook_(test_publish_context_);
        }
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

    /// Test-only publish seam. When armed, `Publish` calls `hook(context)`
    /// after storing the odd version counter (and the release fence) and before
    /// writing the payload, so a test can hold one publish mid-flight and pin
    /// the bounded-retry exhaustion path deterministically. Production leaves
    /// it unset and pays one predictable null check. Not thread-safe: arm the
    /// hook while no publish is in flight and clear it afterwards.
    using TestPublishHook = void (*)(void *) noexcept;
    static void SetTestPublishHook(TestPublishHook hook, void *context) noexcept {
        test_publish_hook_ = hook;
        test_publish_context_ = context;
    }

    /// Returns the slot to its before-first-publish state: `TryRead` fails
    /// until the next `Publish` (the version counter stays monotonic, so a
    /// reader that already copied the payload can only fail the `published_`
    /// acquire). Single writer only, and only while no reader may depend on
    /// the previous contents: a dataset reload, never a live hand-off.
    void Reset() noexcept { published_.store(false, std::memory_order_release); }

  private:
    static_assert(std::is_trivially_copyable_v<HeadSample>, "the seqlock payload must be trivially copyable");
    static_assert(sizeof(HeadSample) % sizeof(std::uint64_t) == 0, "the seqlock payload must be 8-byte sized");
    static_assert(alignof(HeadSample) <= alignof(std::uint64_t), "the seqlock payload must be 8-byte aligned");
    static_assert(std::atomic_ref<std::uint64_t>::is_always_lock_free, "the payload words must be lock-free");
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free, "the version counter must be lock-free");

    static constexpr std::size_t kWordCount = sizeof(HeadSample) / sizeof(std::uint64_t);

    // Test-only publish seam; null unless `SetTestPublishHook` armed it (see
    // the public comment). The writer only reads these.
    inline static TestPublishHook test_publish_hook_ = nullptr;
    inline static void *test_publish_context_ = nullptr;

    mutable std::uint64_t payload_[kWordCount]{};
    std::atomic<std::uint64_t> sequence_{0};
    std::atomic<bool> published_{false};
};

} // namespace cg::glasses
