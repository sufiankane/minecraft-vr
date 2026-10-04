// Decision-matrix tests for the vendor-DLL path policy, exercised through the
// pure DecideDllPath helper so no real DLL (and no filesystem state) is needed,
// plus the loader-level outcomes for relative, working-directory and missing
// absolute paths.

#include <filesystem>
#include <memory>
#include <string>

#include <gtest/gtest.h>

#include "cg/glasses/viture_loader.hpp"

namespace cg::glasses {
namespace {

#ifdef _WIN32
const std::filesystem::path kRoot{"C:\\cubeglass-test"};
#else
const std::filesystem::path kRoot{"/cubeglass-test"};
#endif
const std::filesystem::path kWorkingDirectory = kRoot / "worktree";
const std::filesystem::path kExecutableDirectory = kRoot / "app";

TEST(DecideDllPath, AbsoluteOutsideTheWorkingDirectoryIsAllowed) {
    const std::filesystem::path candidate = kRoot / "sdk" / "viture_glasses_sdk.dll";

    EXPECT_EQ(DecideDllPath(candidate, kWorkingDirectory, kExecutableDirectory), DllPathDecision::kAllow);
}

TEST(DecideDllPath, AbsoluteInsideTheWorkingDirectoryIsRejected) {
    const std::filesystem::path candidate = kWorkingDirectory / "viture_glasses_sdk.dll";

    EXPECT_EQ(DecideDllPath(candidate, kWorkingDirectory, kExecutableDirectory),
              DllPathDecision::kRejectInsideWorkingDirectory);
}

TEST(DecideDllPath, AbsoluteInsideTheWorkingDirectoryButNextToTheExecutableIsAllowed) {
    const std::filesystem::path executable = kWorkingDirectory / "app";
    const std::filesystem::path candidate = executable / "viture_glasses_sdk.dll";

    EXPECT_EQ(DecideDllPath(candidate, kWorkingDirectory, executable), DllPathDecision::kAllow);
}

TEST(DecideDllPath, RelativePathIsRejected) {
    EXPECT_EQ(DecideDllPath("viture_glasses_sdk.dll", kWorkingDirectory, kExecutableDirectory),
              DllPathDecision::kRejectRelative);
}

TEST(DecideDllPath, RelativePathThatEscapesIsStillRejected) {
    const std::filesystem::path candidate = std::filesystem::path("..") / ".." / "viture_glasses_sdk.dll";

    EXPECT_EQ(DecideDllPath(candidate, kWorkingDirectory, kExecutableDirectory), DllPathDecision::kRejectRelative);
}

TEST(VitureLoaderPath, RelativePathIsInvalidArgument) {
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi("cg_no_such_viture_sdk_2a.dll");

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
    EXPECT_NE(std::string(result.status().message()).find("relative"), std::string::npos)
        << "message was: " << result.status().message();
}

TEST(VitureLoaderPath, WorkingDirectoryPathIsInvalidArgument) {
    // Nested, not directly next to the test executable, so the executable's own
    // directory exemption cannot apply whatever the test working directory is.
    const std::filesystem::path planted =
        std::filesystem::current_path() / "nested-plant" / "cg_no_such_viture_sdk_2a.dll";
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(planted.string());

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::InvalidArgument);
    EXPECT_NE(std::string(result.status().message()).find("working directory"), std::string::npos)
        << "message was: " << result.status().message();
}

TEST(VitureLoaderPath, MissingAbsoluteLibraryIsUnsupported) {
    const std::filesystem::path missing = std::filesystem::temp_directory_path() / "cg_no_such_viture_sdk_2a.dll";
    const Result<std::unique_ptr<IVitureApi>> result = LoadVitureApi(missing.string());

    ASSERT_FALSE(result.ok());
    EXPECT_EQ(result.status().code(), StatusCode::Unsupported);
    EXPECT_NE(std::string(result.status().message()).find("cg_no_such_viture_sdk_2a.dll"), std::string::npos)
        << "message was: " << result.status().message();
}

} // namespace
} // namespace cg::glasses
