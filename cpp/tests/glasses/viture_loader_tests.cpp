#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <memory>
#include <optional>
#include <string>
#include <system_error>
#include <thread>

#include <gtest/gtest.h>

#include "cg/glasses/file_hash.hpp"
#include "cg/glasses/viture_loader.hpp"
#include "fake_viture_api.hpp"

namespace cg::glasses {
namespace {

using test::FakeVitureApi;

/// The hash pin is read from the process environment; clear it once for the
/// whole binary so a developer shell that exports it cannot change the
/// outcome of the unrelated loader tests. Individual tests set it explicitly.
class ClearHashPinEnvironment final : public ::testing::Environment {
  public:
    void SetUp() override { ClearHashPin(); }
    void TearDown() override { ClearHashPin(); }

  private:
    static void ClearHashPin() {
#ifdef _WIN32
        _putenv_s("CG_VITURE_DLL_SHA256", "");
#else
        unsetenv("CG_VITURE_DLL_SHA256");
#endif
    }
};

[[maybe_unused]] const ::testing::Environment *const kClearHashPinEnvironment =
    ::testing::AddGlobalTestEnvironment(new ClearHashPinEnvironment());

/// Sets an environment variable for the enclosing scope and removes it (or
/// restores nothing) afterwards. The tests run sequentially.
class ScopedEnv {
  public:
    ScopedEnv(const char *name, const char *value) : name_(name) {
#ifdef _WIN32
        _putenv_s(name_.c_str(), value);
#else
        setenv(name_.c_str(), value, 1);
#endif
    }

    ~ScopedEnv() {
#ifdef _WIN32
        _putenv_s(name_.c_str(), "");
#else
        unsetenv(name_.c_str());
#endif
    }

    ScopedEnv(const ScopedEnv &) = delete;
    ScopedEnv &operator=(const ScopedEnv &) = delete;

  private:
    std::string name_;
};

/// A unique temporary file removed on destruction. The tests never write a
/// valid library, so the hash pin is exercised up to (and through) the open
/// attempt.
class TempFile {
  public:
    explicit TempFile(const std::string &contents) : path_(UniquePath()) {
        std::ofstream stream(path_, std::ios::binary | std::ios::trunc);
        stream.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    }

    ~TempFile() {
        std::error_code error;
        std::filesystem::remove(path_, error);
    }

    TempFile(const TempFile &) = delete;
    TempFile &operator=(const TempFile &) = delete;

    [[nodiscard]] const std::filesystem::path &path() const noexcept { return path_; }

  private:
    static std::filesystem::path UniquePath() {
        static std::atomic<std::uint64_t> counter{0};
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        return std::filesystem::temp_directory_path() /
               ("cg_loader_pin_" + std::to_string(stamp) + "_" + std::to_string(counter.fetch_add(1)) + ".dll");
    }

    std::filesystem::path path_;
};

/// An absolute path outside the working directory that no build or install
/// tree provides, so LoadLibraryExW/dlopen must fail without any vendor DLL.
/// Relative paths are rejected earlier by the path policy (InvalidArgument).
std::string MissingDllPath() {
    return (std::filesystem::temp_directory_path() / "cg_no_such_viture_sdk_2a.dll").string();
}

TEST(VitureLoader, MissingLibraryIsUnsupportedAndNamesThePath) {
    const std::string missing = MissingDllPath();
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(missing);

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    EXPECT_NE(std::string(result.status().message()).find("cg_no_such_viture_sdk_2a.dll"), std::string::npos)
        << "message was: " << result.status().message();
}

TEST(VitureLoader, EmptyPathIsInvalidArgument) {
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi("");

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
}

// --- TD-051: self-contained SHA-256 and the optional content pin -----------

TEST(FileHash, Sha256MatchesTheKnownFipsVectors) {
    // FIPS 180-4 / NIST examples: SHA-256("abc") and SHA-256("").
    const std::uint8_t abc[] = {'a', 'b', 'c'};
    EXPECT_EQ(Sha256Hex(abc, sizeof(abc)), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    EXPECT_EQ(Sha256Hex(nullptr, 0), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
}

TEST(FileHash, FileSha256HexReadsWholeFilesAndRejectsMissingPaths) {
    const TempFile file("abc");
    const std::optional<std::string> hash = FileSha256Hex(file.path());
    ASSERT_TRUE(hash.has_value());
    EXPECT_EQ(*hash, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    const std::filesystem::path path = file.path();
    std::error_code error;
    std::filesystem::remove(path, error);
    EXPECT_FALSE(FileSha256Hex(path).has_value()) << "a missing file must not hash to a value";
}

TEST(VitureLoader, HashPinMismatchRefusesTheLibraryNamingBothDigests) {
    const TempFile file("this is not a vendor library");
    const std::string zeros(64, '0');
    const ScopedEnv pin("CG_VITURE_DLL_SHA256", zeros.c_str());

    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(file.path().string());
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    const std::string message = result.status().message();
    EXPECT_NE(message.find("hash mismatch"), std::string::npos) << "message was: " << message;
    EXPECT_NE(message.find(zeros), std::string::npos) << "message was: " << message;
    const std::optional<std::string> actual = FileSha256Hex(file.path());
    ASSERT_TRUE(actual.has_value());
    EXPECT_NE(message.find(*actual), std::string::npos) << "message was: " << message;
}

TEST(VitureLoader, MatchingHashPinProceedsToTheOpenAttempt) {
    const TempFile file("this is not a vendor library");
    const std::optional<std::string> hash = FileSha256Hex(file.path());
    ASSERT_TRUE(hash.has_value());
    // The known-vector test above pins that this helper computes real SHA-256.
    const ScopedEnv pin("CG_VITURE_DLL_SHA256", hash->c_str());

    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(file.path().string());
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    const std::string message = result.status().message();
    EXPECT_EQ(message.find("hash mismatch"), std::string::npos)
        << "a matching pin must not be reported as a mismatch; the failure must come from the open attempt: "
        << message;
}

TEST(VitureLoader, HashPinIsCaseInsensitive) {
    const TempFile file("this is not a vendor library");
    std::optional<std::string> hash = FileSha256Hex(file.path());
    ASSERT_TRUE(hash.has_value());
    std::transform(hash->begin(), hash->end(), hash->begin(), [](char character) {
        return static_cast<char>(std::toupper(static_cast<unsigned char>(character)));
    });
    const ScopedEnv pin("CG_VITURE_DLL_SHA256", hash->c_str());

    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(file.path().string());
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(std::string(result.status().message()).find("hash mismatch"), std::string::npos)
        << "an upper-case hex pin must match the lower-case digest";
}

TEST(VitureLoader, MalformedHashPinIsInvalidArgument) {
    const TempFile file("x");
    const ScopedEnv pin("CG_VITURE_DLL_SHA256", "not-a-hex-digest");

    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(file.path().string());
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
    EXPECT_NE(std::string(result.status().message()).find("64 hex"), std::string::npos);
}

TEST(VitureLoader, HashPinOnAnUnreadablePathIsUnsupported) {
    const ScopedEnv pin("CG_VITURE_DLL_SHA256", std::string(64, 'a').c_str());
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(MissingDllPath());
    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    EXPECT_NE(std::string(result.status().message()).find("cannot read"), std::string::npos)
        << "message was: " << result.status().message();
}

/// The seam tests below compile and pin the programmable fake the Task 2b
/// Viture source drives; the loader itself is tested on its error paths only.
constexpr cg_head_sample Sample(std::uint32_t sequence, std::int64_t host_time) noexcept {
    return cg_head_sample{host_time, cg_pose{cg_vec3{0.0F, 0.0F, 0.0F}, cg_quat{1.0F, 0.0F, 0.0F, 0.0F}},
                          CG_TRACK_STABLE, sequence};
}

TEST(FakeVitureApi, FeedsSamplesVerbatimAndCountsCalls) {
    FakeVitureApi api;
    api.samples = {Sample(1, 100), Sample(2, 200)};

    EXPECT_TRUE(api.CreateDevice().ok());
    EXPECT_TRUE(api.StartPose().ok());

    const Result<cg_head_sample> first = api.PollPose();
    ASSERT_TRUE(first.ok());
    EXPECT_EQ((*first).sequence, 1U);
    EXPECT_EQ((*first).host_time, 100);
    const Result<cg_head_sample> second = api.PollPose();
    ASSERT_TRUE(second.ok());
    EXPECT_EQ((*second).sequence, 2U);
    const Result<cg_head_sample> exhausted = api.PollPose();
    ASSERT_FALSE(exhausted.ok());
    EXPECT_EQ(exhausted.status().code(), StatusCode::Timeout);

    api.DestroyDevice();
    EXPECT_EQ(api.create_calls.load(), 1U);
    EXPECT_EQ(api.poll_calls.load(), 3U);
    EXPECT_EQ(api.destroy_calls.load(), 1U);
}

TEST(FakeVitureApi, ScriptsResultsAndRecordsDisplayAndRecentreArguments) {
    FakeVitureApi api;
    api.create_script.push_back(Err<void>(Status{StatusCode::Device, "no device"}));
    EXPECT_EQ(api.CreateDevice().status().code(), StatusCode::Device);
    EXPECT_TRUE(api.CreateDevice().ok());

    const std::array<float, kViturePoseFloatCount> kPose{1.0F, 2.0F, 3.0F, 1.0F, 0.0F, 0.0F, 0.0F};
    EXPECT_TRUE(api.ResetOriginCarina(kPose).ok());
    EXPECT_EQ(api.reset_origin_calls.load(), 1U);
    EXPECT_TRUE(api.has_reset_pose.load(std::memory_order_acquire));
    EXPECT_FLOAT_EQ(api.last_reset_pose[2], 3.0F);

    EXPECT_TRUE(api.SetDisplayMode(90, true).ok());
    EXPECT_EQ(api.last_refresh_hz, 90U);
    EXPECT_TRUE(api.last_sbs);
    const Result<std::uint32_t> refresh_hz = api.GetRefreshHz();
    ASSERT_TRUE(refresh_hz.ok());
    EXPECT_EQ(*refresh_hz, 90U);
    EXPECT_EQ(api.SdkVersion(), "fake-viture-0.0.0");
}

TEST(FakeVitureApi, ScriptFeedPublishesTask1PatternSamples) {
    FakeVitureApi api;
    ASSERT_TRUE(api.FeedScript(FakeScript::Static(), 100.0).ok());
    ASSERT_NE(api.script_clock(), nullptr);
    ASSERT_TRUE(api.CreateDevice().ok());
    ASSERT_TRUE(api.StartPose().ok());

    const Result<cg_head_sample> first = api.PollPose();
    ASSERT_TRUE(first.ok());
    EXPECT_EQ((*first).sequence, 1U);
    EXPECT_EQ((*first).host_time, 0);
    EXPECT_EQ((*first).state, CG_TRACK_STABLE);
}

TEST(FakeVitureApi, LongPollReturnsPromptlyAfterRequestStop) {
    FakeVitureApi api;
    api.poll_delay_ns = 5'000'000'000; // 5 s; the test must not wait it out.

    std::atomic<bool> done{false};
    StatusCode code = StatusCode::Ok;
    std::thread poller([&api, &done, &code] {
        code = api.PollPose().status().code();
        done.store(true, std::memory_order_release);
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    api.RequestStop();
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!done.load(std::memory_order_acquire) && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    const bool finished = done.load(std::memory_order_acquire);
    poller.join();

    EXPECT_TRUE(finished) << "the blocked poll ignored RequestStop";
    EXPECT_EQ(code, StatusCode::Timeout);
    EXPECT_TRUE(api.stop_requested());
    EXPECT_EQ(api.poll_calls.load(), 1U);
}

} // namespace
} // namespace cg::glasses
