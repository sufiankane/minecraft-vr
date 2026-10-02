#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <thread>
#include <vector>

#include <gtest/gtest.h>

#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "fake_viture_api.hpp"
#include "ports.hpp"

namespace cg::glasses::test {

namespace {

constexpr std::size_t kSlotReaders = 8;
constexpr std::uint32_t kSlotSamples = 200'000;
constexpr std::size_t kSourceReaders = 4;
constexpr int kSourceStressSeconds = 3;
constexpr int kStopIterations = 1'000;
constexpr std::int64_t kPollDelayNs = 2'000'000;
constexpr std::size_t kFeedSamples = 100'000;

HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

/// Slot sample `seq`: time and position derive from the sequence, so a reader
/// can detect a torn read from the returned values alone.
HeadSample SlotSample(std::uint32_t seq) noexcept {
    return HeadSample{static_cast<HostTime>(seq) * 1000,
                      core_math::Pose{core_math::Vec3{static_cast<double>(seq), 0.0, 0.0}, core_math::Quat::kIdentity},
                      TrackState::Stable, seq};
}

[[nodiscard]] bool IsFiniteUnitPose(const HeadSample &sample) noexcept {
    const core_math::Quat &rotation = sample.pose.rotation;
    return std::isfinite(rotation.w()) && std::isfinite(rotation.x()) && std::isfinite(rotation.y()) &&
           std::isfinite(rotation.z()) && rotation.IsNormalized(1e-9) && std::isfinite(sample.pose.position.x) &&
           std::isfinite(sample.pose.position.y) && std::isfinite(sample.pose.position.z);
}

// --- deterministic retry-exhaustion gate -------------------------------------

/// Test-only gate for `PoseSlot::SetTestPublishHook`: `reached` is set from
/// inside `Publish` while the version counter is odd and the payload has not
/// been written; the hook then spins until `release` is set.
struct PublishGate {
    std::atomic<bool> reached{false};
    std::atomic<bool> release{false};
};

void PauseWriterHook(void *context) noexcept {
    auto *gate = static_cast<PublishGate *>(context);
    gate->reached.store(true, std::memory_order_release);
    while (!gate->release.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }
}

// --- VitureHeadPoseSource harness --------------------------------------------

/// Advances the injected `ManualHostClock` from another thread. `ManualClock`
/// stores its instant in a `std::atomic` (`manual_clock.hpp`), so this is the
/// thread-safe manual-clock option the brief allows (no `SteadyHostClock`
/// fallback is needed).
class ClockFeeder {
  public:
    explicit ClockFeeder(ManualHostClock &clock) : clock_(clock), thread_([this] { Run(); }) {}
    ~ClockFeeder() {
        stop_.store(true, std::memory_order_release);
        thread_.join();
    }

    ClockFeeder(const ClockFeeder &) = delete;
    ClockFeeder &operator=(const ClockFeeder &) = delete;

  private:
    void Run() {
        while (!stop_.load(std::memory_order_acquire)) {
            clock_.Advance(Duration{10'000'000});
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    ManualHostClock &clock_;
    std::atomic<bool> stop_{false};
    std::thread thread_;
};

/// Fills `FakeVitureApi::samples` with `count` identity poses. The SDK stamp is
/// held constant on purpose: the mapper output is then a non-decreasing
/// function of the host clock alone (every window element is an offset of a
/// non-decreasing host instant), which makes the stream's `time` monotone by
/// construction. This stress targets threading, not time mapping.
void FillFakeFeed(FakeVitureApi &api, std::size_t count) {
    api.samples.reserve(count);
    for (std::size_t i = 0; i < count; ++i) {
        cg_head_sample sample{};
        sample.host_time = 0;
        sample.pose.q.w = 1.0F;
        sample.state = CG_TRACK_STABLE;
        sample.sequence = static_cast<std::uint32_t>(i);
        api.samples.push_back(sample);
    }
}

struct SourceHarness {
    std::shared_ptr<FakeVitureApi> api = std::make_shared<FakeVitureApi>();
    std::shared_ptr<ManualHostClock> clock = std::make_shared<ManualHostClock>();
    std::unique_ptr<VitureHeadPoseSource> source;
};

[[nodiscard]] bool WaitForFirstSample(const IHeadPoseSource &source, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        HeadSample sample = PlaceholderSample();
        if (source.TryGetLatest(sample, Duration{0})) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

// --- slot --------------------------------------------------------------------

TEST(ThreadSafety, PoseSlotSaturatedWriterNeverTearsOrReordersForReaders) {
    PoseSlot slot;
    std::atomic<bool> writer_done{false};
    std::atomic<int> torn_count{0};
    std::atomic<int> order_violations{0};
    std::atomic<std::uint64_t> false_count{0};
    std::array<std::atomic<std::uint64_t>, kSlotReaders> valid_counts{};

    const auto reader = [&](std::size_t index) {
        std::uint32_t last_seq = 0;
        HostTime last_time = std::numeric_limits<HostTime>::min();
        while (!writer_done.load(std::memory_order_acquire)) {
            HeadSample sample = PlaceholderSample();
            if (!slot.TryRead(sample)) {
                // Transient exhaustion is allowed (R39); a false read is
                // retried by the caller, which keeps its previous frame. The
                // bounded-retry path is asserted deterministically below.
                false_count.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            valid_counts[index].fetch_add(1, std::memory_order_relaxed);
            const bool torn = !IsFiniteUnitPose(sample) || sample.time != static_cast<HostTime>(sample.seq) * 1000 ||
                              sample.pose.position.x != static_cast<double>(sample.seq);
            if (torn) {
                torn_count.fetch_add(1, std::memory_order_relaxed);
            } else if (sample.seq < last_seq || sample.time < last_time) {
                order_violations.fetch_add(1, std::memory_order_relaxed);
            }
            last_seq = sample.seq;
            last_time = sample.time;
        }
    };

    std::vector<std::thread> readers;
    readers.reserve(kSlotReaders);
    for (std::size_t i = 0; i < kSlotReaders; ++i) {
        readers.emplace_back(reader, i);
    }

    std::thread writer([&slot, &writer_done, &valid_counts] {
        for (std::uint32_t seq = 1; seq <= kSlotSamples; ++seq) {
            slot.Publish(SlotSample(seq));
            // Saturated writer: no yield, so readers hit the retry cap often.
        }
        // Keep publishing pressure off but do not finish until every reader
        // has observed at least one sample, so the per-reader success
        // assertion cannot lose a start-up scheduling race.
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        for (;;) {
            bool all_seen = true;
            for (const std::atomic<std::uint64_t> &count : valid_counts) {
                all_seen = all_seen && count.load(std::memory_order_relaxed) > 0;
            }
            if (all_seen || std::chrono::steady_clock::now() >= deadline) {
                break;
            }
            std::this_thread::yield();
        }
        writer_done.store(true, std::memory_order_release);
    });

    writer.join();
    for (std::thread &reader_thread : readers) {
        reader_thread.join();
    }

    RecordProperty("slot_false_reads", static_cast<std::int64_t>(false_count.load()));
    EXPECT_EQ(torn_count.load(), 0);
    EXPECT_EQ(order_violations.load(), 0);
    for (std::size_t i = 0; i < kSlotReaders; ++i) {
        EXPECT_GT(valid_counts[i].load(), 0U) << "reader " << i;
    }
}

TEST(ThreadSafety, PoseSlotRetryExhaustionReturnsFalseDeterministically) {
    // Mechanism: `PoseSlot::SetTestPublishHook` is a test-only seam that runs
    // inside `Publish` between the odd version store and the payload stores.
    // While the hook spins, the slot is mid-publish by construction, so all 64
    // bounded read attempts observe the odd version and `TryRead` must return
    // false (R39) without ever handing back an unvalidated sample. A saturated
    // writer makes that timing likely but not certain; the hook makes it
    // deterministic. The slot has already published once, so this false is the
    // exhaustion path, not the before-first-publish path.
    PoseSlot slot;
    slot.Publish(SlotSample(1));
    PublishGate gate;
    PoseSlot::SetTestPublishHook(&PauseWriterHook, &gate);
    std::thread writer([&slot] { slot.Publish(SlotSample(2)); });

    struct Cleanup {
        PublishGate *gate;
        std::thread *writer;
        ~Cleanup() {
            gate->release.store(true, std::memory_order_release);
            if (writer->joinable()) {
                writer->join();
            }
            PoseSlot::SetTestPublishHook(nullptr, nullptr);
        }
    } cleanup{&gate, &writer};

    bool reached = false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (std::chrono::steady_clock::now() < deadline) {
        if (gate.reached.load(std::memory_order_acquire)) {
            reached = true;
            break;
        }
        std::this_thread::yield();
    }

    HeadSample out = PlaceholderSample();
    const bool read = slot.TryRead(out);

    EXPECT_TRUE(reached) << "the writer never reached the mid-publish hook";
    EXPECT_FALSE(read) << "a reader must not validate a sample while the writer holds the version odd";

    gate.release.store(true, std::memory_order_release);
    writer.join();
    PoseSlot::SetTestPublishHook(nullptr, nullptr);

    ASSERT_TRUE(slot.TryRead(out));
    EXPECT_EQ(out.seq, 2U);
    EXPECT_EQ(out.time, 2000);
}

// --- VitureHeadPoseSource ----------------------------------------------------

TEST(ThreadSafety, VitureSourceSaturatedReadersSeeOnlyValidOrderedSamples) {
    // Producer: the source's polling thread, fed by `FakeVitureApi`; a clock
    // feeder thread advances the injected `ManualHostClock` (atomic inside).
    SourceHarness harness;
    FillFakeFeed(*harness.api, kFeedSamples);
    harness.api->poll_delay_ns = kPollDelayNs;
    ClockFeeder feeder(*harness.clock);
    harness.source = std::make_unique<VitureHeadPoseSource>(*harness.api, *harness.clock);
    ASSERT_TRUE(harness.source->Start().ok());

    std::atomic<bool> run{true};
    std::atomic<int> torn_count{0};
    std::atomic<int> order_violations{0};
    std::array<std::atomic<std::uint64_t>, kSourceReaders> valid_counts{};

    std::vector<std::thread> readers;
    readers.reserve(kSourceReaders);
    for (std::size_t i = 0; i < kSourceReaders; ++i) {
        readers.emplace_back([&harness, &run, &torn_count, &order_violations, &valid_counts, i] {
            std::uint32_t last_seq = 0;
            HostTime last_time = std::numeric_limits<HostTime>::min();
            while (run.load(std::memory_order_acquire)) {
                HeadSample sample = PlaceholderSample();
                if (!harness.source->TryGetLatest(sample, Duration{0})) {
                    std::this_thread::yield();
                    continue;
                }
                valid_counts[i].fetch_add(1, std::memory_order_relaxed);
                if (!IsFiniteUnitPose(sample)) {
                    torn_count.fetch_add(1, std::memory_order_relaxed);
                } else if (sample.seq < last_seq || sample.time < last_time) {
                    order_violations.fetch_add(1, std::memory_order_relaxed);
                }
                last_seq = sample.seq;
                last_time = sample.time;
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::seconds(kSourceStressSeconds));
    run.store(false, std::memory_order_release);
    for (std::thread &reader_thread : readers) {
        reader_thread.join();
    }
    harness.source->Stop();

    EXPECT_EQ(torn_count.load(), 0);
    EXPECT_EQ(order_violations.load(), 0);
    for (std::size_t i = 0; i < kSourceReaders; ++i) {
        EXPECT_GT(valid_counts[i].load(), 0U) << "reader " << i;
    }
}

TEST(ThreadSafety, StopIsTotalUnderConcurrentReaders) {
    SourceHarness harness;
    FillFakeFeed(*harness.api, kFeedSamples);
    harness.api->poll_delay_ns = kPollDelayNs;
    ClockFeeder feeder(*harness.clock);
    harness.source = std::make_unique<VitureHeadPoseSource>(*harness.api, *harness.clock);
    ASSERT_TRUE(harness.source->Start().ok());
    ASSERT_TRUE(WaitForFirstSample(*harness.source, std::chrono::seconds(5)));

    std::atomic<bool> stop_returned{false};
    std::atomic<std::uint32_t> newest_seq{0};
    std::atomic<HostTime> newest_time{0};
    std::array<std::atomic<int>, kSourceReaders> violations{};

    std::vector<std::thread> readers;
    readers.reserve(kSourceReaders);
    for (std::size_t i = 0; i < kSourceReaders; ++i) {
        readers.emplace_back([&harness, &stop_returned, &newest_seq, &newest_time, &violations, i] {
            // Spin while `Stop` runs, then run the 1000-iteration totality
            // check: every read is false or the newest pre-stop sample.
            while (!stop_returned.load(std::memory_order_acquire)) {
                HeadSample sample = PlaceholderSample();
                static_cast<void>(harness.source->TryGetLatest(sample, Duration{0}));
            }
            const std::uint32_t seq = newest_seq.load(std::memory_order_acquire);
            const HostTime time = newest_time.load(std::memory_order_acquire);
            for (int iteration = 0; iteration < kStopIterations; ++iteration) {
                HeadSample sample = PlaceholderSample();
                if (harness.source->TryGetLatest(sample, Duration{0}) && (sample.seq != seq || sample.time != time)) {
                    violations[i].fetch_add(1, std::memory_order_relaxed);
                }
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    harness.source->Stop();
    HeadSample after_stop = PlaceholderSample();
    ASSERT_TRUE(harness.source->TryGetLatest(after_stop, Duration{0}));
    newest_seq.store(after_stop.seq, std::memory_order_release);
    newest_time.store(after_stop.time, std::memory_order_release);
    stop_returned.store(true, std::memory_order_release);

    for (std::thread &reader_thread : readers) {
        reader_thread.join();
    }
    for (std::size_t i = 0; i < kSourceReaders; ++i) {
        EXPECT_EQ(violations[i].load(), 0) << "reader " << i;
    }
}

} // namespace

} // namespace cg::glasses::test
