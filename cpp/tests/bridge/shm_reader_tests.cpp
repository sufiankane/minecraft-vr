// Reader-contract tests for the 5.12 bridge against the test-only writer
// (S6 Task 1b). The writer publishes through the real 5.6 layout in this same
// process, so the tests exercise the exact shared-memory bytes the Unity DLL
// will read.

#include "cg/bridge/shm_layout.hpp"
#include "cg/bridge/test_writer.h"
#include "cg_unity_bridge.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <thread>

#include <gtest/gtest.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif defined(__unix__) || defined(__APPLE__)
#include <fcntl.h>
#include <sys/mman.h>
#include <unistd.h>
#endif

namespace cg::bridge {
namespace {

std::int64_t now_ns() {
    const auto elapsed = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
}

cg_head_sample MakeHead(std::uint32_t sequence, cg_track_state state = CG_TRACK_STABLE) {
    cg_head_sample sample{};
    sample.host_time = static_cast<cg_time_ns>(sequence) * 1'000'000;
    sample.pose.p = cg_vec3{static_cast<float>(sequence), 0.25f, -static_cast<float>(sequence)};
    sample.pose.q = cg_quat{1.0f, 0.0f, 0.0f, 0.0f};
    sample.state = state;
    sample.sequence = sequence;
    return sample;
}

cg_hand_frame MakeHands(std::uint32_t sequence) {
    cg_hand_frame frame{};
    frame.capture_time = static_cast<cg_time_ns>(sequence);
    frame.publish_time = static_cast<cg_time_ns>(sequence) + 1;
    frame.predicted_for = static_cast<cg_time_ns>(sequence) + 2;
    frame.sequence = sequence;
    frame.hands[0].present = 1;
    frame.hands[0].handedness = 0;
    frame.hands[0].confidence = 0.75f;
    frame.hands[0].joints[20] = cg_vec3{0.1f, 0.2f, -0.3f};
    frame.hands[0].velocity = cg_vec3{0.5f, 0.0f, 0.0f};
    frame.hands[1].present = 1;
    frame.hands[1].handedness = 1;
    frame.hands[1].confidence = 0.5f;
    return frame;
}

/// A second writable view of the test writer's region. Only the open-header
/// tests need it: no bridge or test-writer API can leave the region
/// uninitialised while the writer is open, so the corruption is injected
/// through a raw mapping behind the writer's back.
class RawRegionView {
  public:
    RawRegionView() = default;
    RawRegionView(const RawRegionView &) = delete;
    RawRegionView &operator=(const RawRegionView &) = delete;
    ~RawRegionView() { Close(); }

    bool Open() noexcept {
#if defined(_WIN32)
        mapping_ = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, L"Local\\cubeglass.v1.state");
        if (mapping_ == nullptr) {
            return false;
        }
        base_ = static_cast<std::uint8_t *>(MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        if (base_ == nullptr) {
            CloseHandle(mapping_);
            mapping_ = nullptr;
            return false;
        }
#elif defined(__unix__) || defined(__APPLE__)
        fd_ = shm_open("/cubeglass.v1.state", O_RDWR | O_CLOEXEC, 0);
        if (fd_ < 0) {
            return false;
        }
        void *view = mmap(nullptr, kHeaderSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd_, 0);
        if (view == MAP_FAILED) {
            close(fd_);
            fd_ = -1;
            return false;
        }
        base_ = static_cast<std::uint8_t *>(view);
#endif
        return base_ != nullptr;
    }

    void ZeroMagic() const noexcept { std::memset(base_, 0, sizeof(std::uint64_t)); }

    /// Stores the header fields the open validation checks with the writer's
    /// publication order (shm_layout.hpp, I-3): the mutable fields first with
    /// relaxed stores, then `magic` last with a release store, so an acquire
    /// load of a valid magic sees a complete header. CXX-10: storing `magic`
    /// first would model a protocol-violating writer and would let a
    /// magic-first writer regression pass this suite.
    void InitialiseHeader() const noexcept {
        std::atomic_ref<std::uint32_t>(*reinterpret_cast<std::uint32_t *>(base_ + offsetof(ShmHeader, abi_version)))
            .store(kShmAbiVersion, std::memory_order_relaxed);
        std::atomic_ref<std::uint32_t>(*reinterpret_cast<std::uint32_t *>(base_ + offsetof(ShmHeader, header_size)))
            .store(kHeaderSize, std::memory_order_relaxed);
        std::atomic_ref<std::uint64_t>(*reinterpret_cast<std::uint64_t *>(base_))
            .store(kShmMagic, std::memory_order_release);
    }

    void SetAbiVersion(std::uint32_t version) const noexcept {
        auto *abi = reinterpret_cast<std::uint32_t *>(base_ + offsetof(ShmHeader, abi_version));
        std::memcpy(abi, &version, sizeof(version));
    }

    void SetHeaderSize(std::uint32_t size) const noexcept {
        auto *word = reinterpret_cast<std::uint32_t *>(base_ + offsetof(ShmHeader, header_size));
        std::memcpy(word, &size, sizeof(size));
    }

    [[nodiscard]] std::uint8_t *base() const noexcept { return base_; }

  private:
    void Close() noexcept {
#if defined(_WIN32)
        if (base_ != nullptr) {
            UnmapViewOfFile(base_);
        }
        if (mapping_ != nullptr) {
            CloseHandle(mapping_);
        }
        mapping_ = nullptr;
#elif defined(__unix__) || defined(__APPLE__)
        if (base_ != nullptr) {
            munmap(base_, kHeaderSize);
        }
        if (fd_ >= 0) {
            close(fd_);
        }
        fd_ = -1;
#endif
        base_ = nullptr;
    }

    std::uint8_t *base_ = nullptr;
#if defined(_WIN32)
    HANDLE mapping_ = nullptr;
#elif defined(__unix__) || defined(__APPLE__)
    int fd_ = -1;
#endif
};

class ShmReaderTest : public ::testing::Test {
  protected:
    void SetUp() override { ASSERT_EQ(cg_test_writer_create(), CG_OK); }

    void TearDown() override {
        if (handle_ != nullptr) {
            cg_bridge_close(handle_);
            handle_ = nullptr;
        }
        cg_test_writer_close();
    }

    void OpenBridge() {
        ASSERT_EQ(cg_bridge_open(&handle_), CG_OK);
        ASSERT_NE(handle_, nullptr);
    }

    void *handle_ = nullptr;
};

TEST_F(ShmReaderTest, OpenOnMissingMappingReturnsNotReady) {
    cg_test_writer_close();
    void *handle = nullptr;
    EXPECT_EQ(cg_bridge_open(&handle), CG_ERR_NOT_READY);
    EXPECT_EQ(handle, nullptr); // the out parameter is written only on CG_OK
}

TEST_F(ShmReaderTest, PublishAndReadRoundTrip) {
    OpenBridge();

    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns()), CG_OK);
    const cg_head_sample head = MakeHead(7);
    ASSERT_EQ(cg_test_writer_publish_head(&head), CG_OK);

    cg_head_sample read_head{};
    ASSERT_EQ(cg_bridge_read_head(handle_, &read_head), CG_OK);
    EXPECT_EQ(std::memcmp(&read_head, &head, sizeof(head)), 0);

    const cg_hand_frame hands = MakeHands(3);
    ASSERT_EQ(cg_test_writer_publish_hands(&hands), CG_OK);

    cg_hand_frame read_hands{};
    ASSERT_EQ(cg_bridge_read_hands(handle_, &read_hands), CG_OK);
    EXPECT_EQ(std::memcmp(&read_hands, &hands, sizeof(hands)), 0);
}

TEST_F(ShmReaderTest, ReadBeforeFirstPublishIsNotReady) {
    OpenBridge();

    cg_head_sample head{};
    EXPECT_EQ(cg_bridge_read_head(handle_, &head), CG_ERR_NOT_READY);

    cg_hand_frame hands{};
    EXPECT_EQ(cg_bridge_read_hands(handle_, &hands), CG_ERR_NOT_READY);
}

TEST_F(ShmReaderTest, FreshHeartbeatKeepsStateAndStaleHeartbeatForcesTrackLost) {
    OpenBridge();

    const cg_head_sample head = MakeHead(5, CG_TRACK_STABLE);
    // Far-future heartbeat: fresh no matter how long the test runs.
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() + 1'000'000'000), CG_OK);
    ASSERT_EQ(cg_test_writer_publish_head(&head), CG_OK);

    cg_head_sample fresh{};
    ASSERT_EQ(cg_bridge_read_head(handle_, &fresh), CG_OK);
    EXPECT_EQ(fresh.state, CG_TRACK_STABLE);
    EXPECT_EQ(fresh.sequence, 5U);

    // Far-past heartbeat: stale well beyond the 250 ms rule.
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() - kStaleAfterNs - 1'000'000'000), CG_OK);
    cg_head_sample stale{};
    ASSERT_EQ(cg_bridge_read_head(handle_, &stale), CG_OK);
    EXPECT_EQ(stale.state, CG_TRACK_LOST);
    EXPECT_EQ(stale.sequence, 5U);
    EXPECT_EQ(stale.host_time, head.host_time);
}

TEST_F(ShmReaderTest, StaleHandsReportNotReadyInsteadOfASample) {
    OpenBridge();

    const cg_hand_frame hands = MakeHands(6);
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() + 1'000'000'000), CG_OK);
    ASSERT_EQ(cg_test_writer_publish_hands(&hands), CG_OK);

    cg_hand_frame read{};
    ASSERT_EQ(cg_bridge_read_hands(handle_, &read), CG_OK);
    EXPECT_EQ(std::memcmp(&read, &hands, sizeof(hands)), 0);

    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() - kStaleAfterNs - 1'000'000'000), CG_OK);
    EXPECT_EQ(cg_bridge_read_hands(handle_, &read), CG_ERR_NOT_READY);
}

TEST_F(ShmReaderTest, SendCommandRejectsZeroAndUint32MaxWithoutWriting) {
    OpenBridge();

    ASSERT_EQ(cg_bridge_send_command(handle_, 4), CG_OK);
    EXPECT_EQ(cg_bridge_send_command(handle_, 0), CG_ERR_INVALID_ARG);
    EXPECT_EQ(cg_bridge_send_command(handle_, UINT32_MAX), CG_ERR_INVALID_ARG);

    std::uint32_t command = 0;
    std::uint32_t ack = 0;
    ASSERT_EQ(cg_test_writer_read_command(&command, &ack), CG_OK);
    EXPECT_EQ(command, 5U); // the rejected sends left the word untouched
    EXPECT_EQ(ack, 0U);
}

TEST_F(ShmReaderTest, UninitialisedHeaderReportsNotReadyAfterTheInitWindow) {
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    view.ZeroMagic();

    void *handle = nullptr;
    EXPECT_EQ(cg_bridge_open(&handle), CG_ERR_NOT_READY);
    EXPECT_EQ(handle, nullptr); // the out parameter is written only on CG_OK
}

TEST_F(ShmReaderTest, IncompatibleHeaderReportsUnsupportedAfterTheInitWindow) {
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    view.SetAbiVersion(kShmAbiVersion + 1);

    void *handle = nullptr;
    EXPECT_EQ(cg_bridge_open(&handle), CG_ERR_UNSUPPORTED);
    EXPECT_EQ(handle, nullptr);
}

/// A legacy v1 writer (the pre-S6 layout without `cg_hand_frame`) must be
/// detected through the region ABI, and the diagnostic must name both the
/// observed and the expected versions (I-2).
TEST_F(ShmReaderTest, LegacyAbiVersionIsRejectedNamingBothVersions) {
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    view.SetAbiVersion(1);

    void *handle = nullptr;
    ASSERT_EQ(cg_bridge_open(&handle), CG_ERR_UNSUPPORTED);
    EXPECT_EQ(handle, nullptr);
    const std::string message = LastHeaderError();
    EXPECT_NE(message.find("1"), std::string::npos) << "message was: " << message;
    EXPECT_NE(message.find("2"), std::string::npos) << "message was: " << message;
    EXPECT_NE(message.find("abi_version"), std::string::npos) << "message was: " << message;

    // Restoring the version lets the same region open, and a successful open
    // clears the diagnostic.
    view.SetAbiVersion(kShmAbiVersion);
    ASSERT_EQ(cg_bridge_open(&handle), CG_OK);
    ASSERT_NE(handle, nullptr);
    EXPECT_STREQ(LastHeaderError(), "");
    cg_bridge_close(handle);
}

TEST_F(ShmReaderTest, TestWriterPublishesTheDeclaredAbiVersion) {
    // The test writer asserts the region bytes it writes carry the ABI the
    // layout header declares (kShmAbiVersion == 2 for the hand payload).
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    auto *abi = reinterpret_cast<std::uint32_t *>(view.base() + offsetof(ShmHeader, abi_version));
    EXPECT_EQ(std::atomic_ref<std::uint32_t>(*abi).load(std::memory_order_relaxed), kShmAbiVersion);
    EXPECT_EQ(kShmAbiVersion, 2U);
}

TEST_F(ShmReaderTest, OpenRecoversWhenTheHeaderInitialisesWithinTheRetryWindow) {
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    view.ZeroMagic();

    // The helper thread is created (and warm) before the open starts, then
    // completes the header a short delay into the bounded retry window.
    std::atomic<bool> start{false};
    std::thread initialiser([&view, &start] {
        while (!start.load(std::memory_order_acquire)) {
            std::this_thread::yield();
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        view.InitialiseHeader();
    });

    start.store(true, std::memory_order_release);
    void *handle = nullptr;
    const cg_status status = cg_bridge_open(&handle);
    initialiser.join();

    EXPECT_EQ(status, CG_OK);
    EXPECT_NE(handle, nullptr);
    cg_bridge_close(handle);
}

TEST_F(ShmReaderTest, OpenRejectsAValidMagicWithAnIncompleteHeader) {
    // A region whose `magic` is valid but whose remaining header is not yet
    // published (exactly what a magic-first writer leaves visible) must not
    // open: the validation checks abi_version and header_size, not magic
    // alone. Pins the reader side of the CXX-10 publication order.
    RawRegionView view;
    ASSERT_TRUE(view.Open());
    view.SetAbiVersion(0);
    view.SetHeaderSize(0);

    void *handle = nullptr;
    EXPECT_EQ(cg_bridge_open(&handle), CG_ERR_UNSUPPORTED);
    EXPECT_EQ(handle, nullptr);
    const std::string error = LastHeaderError();
    EXPECT_NE(error.find("abi_version 0"), std::string::npos) << error;
}

TEST_F(ShmReaderTest, SendCommandReachesWriterAndAckIsWriterSideOnly) {
    OpenBridge();

    ASSERT_EQ(cg_bridge_send_command(handle_, 7), CG_OK);

    std::uint32_t command = 0;
    std::uint32_t ack = 0;
    ASSERT_EQ(cg_test_writer_read_command(&command, &ack), CG_OK);
    EXPECT_EQ(command, 8U); // cmd + 1; 0 stays idle
    EXPECT_EQ(ack, 0U);

    // 5.12 has no read-ack function, so the reader cannot observe the ack.
    // The round trip is asserted only where the writer can see it.
    ASSERT_EQ(cg_test_writer_ack_command(8), CG_OK);
    ASSERT_EQ(cg_test_writer_read_command(&command, &ack), CG_OK);
    EXPECT_EQ(command, 8U);
    EXPECT_EQ(ack, 8U);
}

TEST_F(ShmReaderTest, RawTornCountersAreRejectedWithTimeout) {
    OpenBridge();

    const cg_head_sample head = MakeHead(1);

    // Writer held mid-publish: seq_a stays odd, so every attempt retries and
    // the bounded retry budget is exhausted.
    ASSERT_EQ(cg_test_writer_publish_head_raw(1, 1, &head), CG_OK);
    cg_head_sample read{};
    EXPECT_EQ(cg_bridge_read_head(handle_, &read), CG_ERR_TIMEOUT);

    // Even seq_a but a mismatched seq_b: a copy is never accepted.
    ASSERT_EQ(cg_test_writer_publish_head_raw(2, 1, &head), CG_OK);
    EXPECT_EQ(cg_bridge_read_head(handle_, &read), CG_ERR_TIMEOUT);

    // A proper publish recovers the slot.
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns()), CG_OK);
    ASSERT_EQ(cg_test_writer_publish_head(&head), CG_OK);
    EXPECT_EQ(cg_bridge_read_head(handle_, &read), CG_OK);
    EXPECT_EQ(read.sequence, 1U);
}

TEST_F(ShmReaderTest, CloseIsNullSafeAndAFreshOpenStillReads) {
    OpenBridge();

    cg_bridge_close(handle_);
    handle_ = nullptr;
    cg_bridge_close(nullptr); // null-safe no-op

    // A handle is invalid after cg_bridge_close; reopen instead of reusing it.
    OpenBridge();
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns()), CG_OK);
    const cg_head_sample head = MakeHead(9);
    ASSERT_EQ(cg_test_writer_publish_head(&head), CG_OK);

    cg_head_sample read{};
    EXPECT_EQ(cg_bridge_read_head(handle_, &read), CG_OK);
    EXPECT_EQ(read.sequence, 9U);
}

TEST_F(ShmReaderTest, NullArgumentsAreRejected) {
    OpenBridge();

    cg_head_sample head{};
    EXPECT_EQ(cg_bridge_read_head(nullptr, &head), CG_ERR_INVALID_ARG);
    EXPECT_EQ(cg_bridge_read_head(handle_, nullptr), CG_ERR_INVALID_ARG);

    cg_hand_frame hands{};
    EXPECT_EQ(cg_bridge_read_hands(nullptr, &hands), CG_ERR_INVALID_ARG);
    EXPECT_EQ(cg_bridge_read_hands(handle_, nullptr), CG_ERR_INVALID_ARG);

    EXPECT_EQ(cg_bridge_send_command(nullptr, 1), CG_ERR_INVALID_ARG);
    EXPECT_EQ(cg_bridge_send_command(handle_, 0), CG_ERR_INVALID_ARG);

    EXPECT_EQ(cg_bridge_open(nullptr), CG_ERR_INVALID_ARG);
}

} // namespace
} // namespace cg::bridge
