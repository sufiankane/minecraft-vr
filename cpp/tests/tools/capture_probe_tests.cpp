// cg-capture-probe argument parsing and report formatting (S8 Task 6). The
// header next to the tool compiles here directly, so the CLI contract is
// pinned without a device.

#include <cstdint>
#include <string>
#include <vector>

#include "args.hpp"

#include <gtest/gtest.h>

namespace {

using cg::HostTime;
using cg::capture_probe::FormatObservations;
using cg::capture_probe::FramesPerSecond;
using cg::capture_probe::Observations;
using cg::capture_probe::Options;
using cg::capture_probe::ParseOptions;
using cg::capture_probe::ParseResult;
using cg::capture_probe::UsageText;

TEST(CaptureProbeArgs, DefaultsApply) {
    const ParseResult result = ParseOptions({});
    ASSERT_TRUE(result.ok) << result.error;
    EXPECT_TRUE(result.options.dll.empty());
    EXPECT_TRUE(result.options.out.empty());
    EXPECT_EQ(result.options.dof, "3dof");
    EXPECT_DOUBLE_EQ(result.options.seconds, 10.0);
    EXPECT_EQ(result.options.snapshot_frame, 1U);
    EXPECT_FALSE(result.options.help);
}

TEST(CaptureProbeArgs, AllOptionsParse) {
    const ParseResult result = ParseOptions({"--dll", "C:/sdk/glasses.dll", "--out", "C:/tmp/snap", "--dof", "6dof",
                                             "--seconds", "2.5", "--snapshot-frame", "30"});
    ASSERT_TRUE(result.ok) << result.error;
    EXPECT_EQ(result.options.dll, "C:/sdk/glasses.dll");
    EXPECT_EQ(result.options.out, "C:/tmp/snap");
    EXPECT_EQ(result.options.dof, "6dof");
    EXPECT_DOUBLE_EQ(result.options.seconds, 2.5);
    EXPECT_EQ(result.options.snapshot_frame, 30U);
}

TEST(CaptureProbeArgs, HelpIsRecognised) {
    for (const std::string flag : {"-h", "--help"}) {
        const ParseResult result = ParseOptions({flag});
        ASSERT_TRUE(result.ok) << result.error;
        EXPECT_TRUE(result.options.help);
    }
}

TEST(CaptureProbeArgs, InvalidDofIsRejectedByName) {
    const ParseResult result = ParseOptions({"--dof", "9dof"});
    ASSERT_FALSE(result.ok);
    EXPECT_NE(result.error.find("--dof"), std::string::npos);
    EXPECT_NE(result.error.find("3dof"), std::string::npos);
}

TEST(CaptureProbeArgs, MissingValuesAreRejectedByName) {
    for (const std::string flag : {"--dll", "--out", "--dof", "--seconds", "--snapshot-frame"}) {
        const ParseResult result = ParseOptions({flag});
        ASSERT_FALSE(result.ok) << flag;
        EXPECT_NE(result.error.find(flag), std::string::npos) << flag;
    }
}

TEST(CaptureProbeArgs, InvalidNumbersAreRejected) {
    EXPECT_FALSE(ParseOptions({"--seconds", "0"}).ok);
    EXPECT_FALSE(ParseOptions({"--seconds", "-1"}).ok);
    EXPECT_FALSE(ParseOptions({"--seconds", "abc"}).ok);
    EXPECT_FALSE(ParseOptions({"--snapshot-frame", "0"}).ok);
    EXPECT_FALSE(ParseOptions({"--snapshot-frame", "x"}).ok);
}

TEST(CaptureProbeArgs, UnknownArgumentsAreRejected) {
    const ParseResult result = ParseOptions({"--fps", "60"});
    ASSERT_FALSE(result.ok);
    EXPECT_NE(result.error.find("--fps"), std::string::npos);
}

TEST(CaptureProbeReport, RateUsesTheSdkSpan) {
    Observations observations;
    observations.frames = 6;
    observations.first_time = 0;
    observations.last_time = 500'000'000; // 0.5 s over 5 intervals
    EXPECT_DOUBLE_EQ(FramesPerSecond(observations), 10.0);
}

TEST(CaptureProbeReport, RateIsZeroWithoutASpan) {
    Observations observations;
    EXPECT_DOUBLE_EQ(FramesPerSecond(observations), 0.0);
    observations.frames = 1;
    EXPECT_DOUBLE_EQ(FramesPerSecond(observations), 0.0);
    observations.frames = 5;
    observations.first_time = 100;
    observations.last_time = 100;
    EXPECT_DOUBLE_EQ(FramesPerSecond(observations), 0.0);
}

TEST(CaptureProbeReport, FormatCarriesEveryField) {
    Observations observations;
    observations.frames = 120;
    observations.sequence_gaps = 1;
    observations.first_time = 1'000'000;
    observations.last_time = 1'001'000'000;
    observations.width = 640;
    observations.height = 480;
    observations.stride = 640;
    observations.f0_equals_f1 = false;
    observations.left0_equals_right0 = true;
    observations.snapshots_written = 4;
    observations.snapshot_dir = "C:/tmp/snap";
    const std::string report = FormatObservations(observations);
    EXPECT_NE(report.find("frames=120"), std::string::npos);
    EXPECT_NE(report.find("rate=119.0 Hz"), std::string::npos);
    EXPECT_NE(report.find("geometry=640x480 stride=640 packed=yes"), std::string::npos);
    EXPECT_NE(report.find("sequence_gaps=1"), std::string::npos);
    EXPECT_NE(report.find("f0_equals_f1=no"), std::string::npos);
    EXPECT_NE(report.find("left0_equals_right0=yes"), std::string::npos);
    EXPECT_NE(report.find("snapshots=4"), std::string::npos);
    EXPECT_NE(report.find("C:/tmp/snap"), std::string::npos);
}

TEST(CaptureProbeReport, FormatReportsNoSnapshotsWhenDisabled) {
    Observations observations;
    observations.frames = 2;
    observations.first_time = 0;
    observations.last_time = 1'000'000;
    observations.width = 16;
    observations.height = 8;
    observations.stride = 24;
    const std::string report = FormatObservations(observations);
    EXPECT_NE(report.find("stride=24 padded=yes"), std::string::npos);
    EXPECT_NE(report.find("snapshots=0"), std::string::npos);
    EXPECT_NE(report.find("no snapshots"), std::string::npos);
}

TEST(CaptureProbeReport, UsageListsEveryOption) {
    const std::string usage = UsageText();
    for (const std::string option : {"--dll", "--out", "--dof", "--seconds", "--snapshot-frame"}) {
        EXPECT_NE(usage.find(option), std::string::npos) << option;
    }
}

} // namespace
