#pragma once

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "ports.hpp"

namespace cg::capture_probe {

/// Nanoseconds per second on the SDK timeline.
inline constexpr double kNanosecondsPerSecond = 1e9;

/// Parsed `cg-capture-probe` arguments (S8 Task 6). The header is next to the
/// tool so the unit tests compile it directly (the `rss_gate.hpp` pattern).
struct Options {
    /// Vendor library path (the loader's path policy applies).
    std::string dll;
    /// Directory for the first-frame PGM snapshots; empty disables them.
    std::string out;
    /// `3dof` or `6dof`; exported as `CG_VITURE_DOF` before the device opens.
    std::string dof = "3dof";
    /// Measurement window in seconds.
    double seconds = 10.0;
    /// 1-based frame whose streams are snapshotted (`--snapshot-frame`).
    std::uint64_t snapshot_frame = 1;
    bool help = false;
};

struct ParseResult {
    bool ok = false;
    std::string error;
    Options options;
};

/// Parses the tool's arguments; the tests pin every accepted form and each
/// named rejection.
[[nodiscard]] inline ParseResult ParseOptions(const std::vector<std::string> &args) {
    ParseResult result;
    bool ok = true;
    const auto fail = [&result, &ok](std::string message) {
        result.ok = false;
        result.error = std::move(message);
        ok = false;
    };
    const auto take_value = [&args, &fail](std::size_t &index, const std::string &flag, std::string &value) {
        if (index + 1 >= args.size()) {
            fail(flag + " requires a value");
            return false;
        }
        value = args.at(++index);
        return true;
    };
    for (std::size_t index = 0; index < args.size() && ok; ++index) {
        const std::string &argument = args.at(index);
        std::string value;
        if (argument == "--dll") {
            if (!take_value(index, argument, value)) {
                break;
            }
            result.options.dll = value;
        } else if (argument == "--out") {
            if (!take_value(index, argument, value)) {
                break;
            }
            result.options.out = value;
        } else if (argument == "--dof") {
            if (!take_value(index, argument, value)) {
                break;
            }
            if (value != "3dof" && value != "6dof") {
                fail("--dof must be '3dof' or '6dof'");
                break;
            }
            result.options.dof = value;
        } else if (argument == "--seconds") {
            if (!take_value(index, argument, value)) {
                break;
            }
            char *end = nullptr;
            const double seconds = std::strtod(value.c_str(), &end);
            if (end == value.c_str() || *end != '\0' || seconds <= 0.0) {
                fail("--seconds must be a positive number");
                break;
            }
            result.options.seconds = seconds;
        } else if (argument == "--snapshot-frame") {
            if (!take_value(index, argument, value)) {
                break;
            }
            char *end = nullptr;
            const unsigned long long frame = std::strtoull(value.c_str(), &end, 10);
            if (end == value.c_str() || *end != '\0' || frame == 0) {
                fail("--snapshot-frame must be a positive integer");
                break;
            }
            result.options.snapshot_frame = static_cast<std::uint64_t>(frame);
        } else if (argument == "-h" || argument == "--help") {
            result.options.help = true;
        } else {
            fail("unknown argument '" + argument + "'");
            break;
        }
    }
    if (!ok) {
        return result;
    }
    result.ok = true;
    return result;
}

/// What the probe observed during the run (S8: resolution/stride, rate,
/// sequence gaps, f0-vs-f1 equality, snapshots).
struct Observations {
    std::uint64_t frames = 0;
    std::uint64_t sequence_gaps = 0;
    /// SDK monotonic nanoseconds (the frame's `time`); the first stamp of a
    /// session is a startup artifact (observed 2026-10-07), so the span is
    /// reported for the record.
    HostTime first_time = 0;
    HostTime last_time = 0;
    /// Host arrival span between the first and last delivered frame; the rate
    /// is computed from this when positive, because it is artifact-free.
    HostTime host_span_ns = 0;
    int width = 0;
    int height = 0;
    int stride = 0;
    /// Which streams the vendor delivered on the first frame (U-03): a null
    /// pointer means the callback carries no such stream.
    bool l0_present = false;
    bool r0_present = false;
    bool l1_present = false;
    bool r1_present = false;
    /// Any observed frame where the `f0` pair equals the `f1` pair byte-wise.
    bool f0_equals_f1 = false;
    /// Any observed frame where `left0` equals `right0` byte-wise (a likely
    /// sign of a mono or duplicated stream).
    bool left0_equals_right0 = false;
    std::uint64_t snapshots_written = 0;
    std::string snapshot_dir;
};
/// Frames per second: over the host arrival span when it is known (the first
/// SDK stamp of a session is a startup artifact), else over the SDK span;
/// 0 when fewer than two frames or a non-positive span (never divides by
/// zero).
[[nodiscard]] inline double FramesPerSecond(const Observations &observations) noexcept {
    if (observations.frames < 2) {
        return 0.0;
    }
    HostTime span_ns = observations.host_span_ns;
    if (span_ns <= 0) {
        span_ns = observations.last_time - observations.first_time;
    }
    if (span_ns <= 0) {
        return 0.0;
    }
    return static_cast<double>(observations.frames - 1) / (static_cast<double>(span_ns) / kNanosecondsPerSecond);
}

/// The multi-line HIL log block; the tests pin the field labels and values.
[[nodiscard]] inline std::string FormatObservations(const Observations &observations) {
    const bool packed = observations.stride == observations.width;
    char rate[32] = {};
    (void)std::snprintf(rate, sizeof(rate), "%.1f", FramesPerSecond(observations));
    std::string text = "frames=" + std::to_string(observations.frames);
    text += "\nrate=" + std::string{rate} + " Hz";
    text += "\nhost_span_ns=" + std::to_string(observations.host_span_ns);
    text += "\nsdk_span_ns=" + std::to_string(observations.last_time - observations.first_time);
    text += "\ngeometry=" + std::to_string(observations.width) + "x" + std::to_string(observations.height) +
            " stride=" + std::to_string(observations.stride) + (packed ? " packed=yes" : " padded=yes");
    text += "\nstreams=l0:" + std::string{observations.l0_present ? "y" : "n"} +
            " r0:" + std::string{observations.r0_present ? "y" : "n"} +
            " l1:" + std::string{observations.l1_present ? "y" : "n"} +
            " r1:" + std::string{observations.r1_present ? "y" : "n"};
    text += "\nsequence_gaps=" + std::to_string(observations.sequence_gaps);
    text += "\nf0_equals_f1=" + std::string{observations.f0_equals_f1 ? "yes" : "no"} +
            " left0_equals_right0=" + std::string{observations.left0_equals_right0 ? "yes" : "no"};
    text += "\nsnapshots=" + std::to_string(observations.snapshots_written);
    if (observations.snapshots_written > 0 && !observations.snapshot_dir.empty()) {
        text += " dir=" + observations.snapshot_dir;
    } else {
        text += " (no snapshots)";
    }
    return text;
}

/// The usage text (also printed for `--help`).
[[nodiscard]] inline std::string UsageText() {
    return "usage: cg-capture-probe --dll <path> [--out DIR] [--dof 3dof|6dof] [--seconds S]\n"
           "                         [--snapshot-frame N]\n"
           "\n"
           "Runs the VITURE stereo frame source for S seconds (default 10) and logs the\n"
           "measured frame rate, sequence gaps, geometry/stride, f0-vs-f1 equality and the\n"
           "first-frame PGM snapshots written to DIR (S8 U-02/U-03 evidence).\n"
           "Exit codes: 0 frames observed; 1 argument or no-frame failure; 2 the vendor\n"
           "library or the device failed (same matrix as cg-pose-probe).\n";
}

} // namespace cg::capture_probe
