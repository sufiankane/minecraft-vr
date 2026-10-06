// cg-pose-probe: the S5 head-pose instrument (task 4).
//
// Drives one of the three IHeadPoseSource implementations for a bounded wall
// time, records every newly published sample, prints the measured sample rate,
// the inter-sample jitter (p50/p95/p99 of |interval - mean|), the per-state
// histogram and the first/last pose, and optionally writes the samples as a
// CSV compatible with ReplayHeadPoseSource.
//
// The output CSV has the S5 replay header:
//
//   host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status
//
// `host_time_ns` is the sample's mapped host-timeline instant. `sdk_time_s`
// is per mode: `--source viture` records the true SDK seconds stamp from the
// diagnostics-only `VitureHeadPoseSource::LastSdkSeconds()` accessor (added
// for HIL latency analysis), falling back to host-derived seconds with a
// warning until the first stamp is available; `--source fake` and
// `--source replay` record host-derived seconds because those sources carry
// no SDK stamp. The column round-trips through ReplayHeadPoseSource, which
// parses and validates it but does not publish it.
//
// `--source fake` couples the fake's ManualClock to the steady host clock and
// emits at the requested rate, so the measured jitter is the probe's own
// wall-clock pacing error; `--source replay` paces the recording by its CSV
// intervals (the first two rows are published back-to-back to learn the first
// interval) or on a fixed wall grid when `--rate` is given (the recorded
// sample times are unchanged either way, so the reported rate and jitter stay
// the dataset's); `--source viture` loads the vendor library and polls the
// real source (the HIL path).
//
// Exit codes: 0 when at least one sample was read; 2 when the viture library
// fails to load (any LoadVitureApi failure, including InvalidArgument for an
// empty path) or the source reports Unsupported/NotReady; 3 when the vendor
// teardown wedged (`source.Stop()` did not return within 5 s — the run's
// report and CSV are already written when this exit occurs); 1 for every other
// failure (usage, bad dataset, no samples, output error).

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <system_error>
#include <thread>
#include <vector>

#include "cg/core_math/time.hpp"
#include "cg/glasses/fake_head_pose_source.hpp"
#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/replay_head_pose_source.hpp"
#include "cg/glasses/viture_api.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "cg/glasses/viture_loader.hpp"
#include "ports.hpp"
#include "result.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <timeapi.h>
#endif

namespace {

using cg::Duration;
using cg::HeadSample;
using cg::IHeadPoseSource;
using cg::Status;
using cg::StatusCode;
using cg::TrackState;

using cg::core_math::HostTime;
using cg::core_math::ToSeconds;

using cg::glasses::FakeHeadPoseSource;
using cg::glasses::FakeHeadPoseSourceConfig;
using cg::glasses::FakeScript;
using cg::glasses::IVitureApi;
using cg::glasses::LoadVitureApi;
using cg::glasses::ManualClock;
using cg::glasses::ReplayHeadPoseSource;
using cg::glasses::SteadyHostClock;
using cg::glasses::VitureHeadPoseSource;

constexpr double kNanosecondsPerSecond = 1e9; // static_cast<double>(core_math::kNanosecondsPerSecond)

#ifdef _WIN32
/// Raises the system timer to 1 ms for the run. Without it `sleep_for` quantises
/// to the ~15.6 ms default tick on Windows and the fake's wall pacing is far
/// coarser than its sample period. Restored on scope exit.
class ScopedTimerResolution {
  public:
    ScopedTimerResolution() noexcept : raised_(::timeBeginPeriod(1) == TIMERR_NOERROR) {}
    ~ScopedTimerResolution() noexcept {
        if (raised_) {
            ::timeEndPeriod(1);
        }
    }

    ScopedTimerResolution(const ScopedTimerResolution &) = delete;
    ScopedTimerResolution &operator=(const ScopedTimerResolution &) = delete;

  private:
    bool raised_ = false;
};
#endif

// --- command line -----------------------------------------------------------

enum class SourceKind { Fake, Replay, Viture };

struct Options {
    SourceKind source = SourceKind::Fake;
    std::string dll;
    std::string csv;
    double seconds = 5.0;
    double rate_hz = 90.0;
    bool rate_set = false;
    bool display = false;
    std::string out;
    std::uint64_t print_every = 0;
};

const char *SourceName(SourceKind kind) noexcept {
    switch (kind) {
    case SourceKind::Fake:
        return "fake";
    case SourceKind::Replay:
        return "replay";
    case SourceKind::Viture:
        return "viture";
    }
    return "?";
}

void PrintUsage(std::FILE *stream) {
    std::fputs("cg-pose-probe: drive a cg-glasses head pose source and measure it\n"
               "\n"
               "usage: cg-pose-probe --source fake|replay|viture [options]\n"
               "\n"
               "options:\n"
               "  --source NAME    sample source: fake (default), replay, viture\n"
               "  --dll PATH       VITURE SDK library for --source viture (or CG_VITURE_DLL);\n"
               "                   an absolute path outside the working directory (a DLL\n"
               "                   next to cg-pose-probe.exe is also accepted)\n"
               "  --csv PATH       recorded dataset for --source replay (required)\n"
               "  --seconds N      run time in seconds (default 5)\n"
               "  --rate HZ        fake: sample rate (default 90). replay: wall-pacing\n"
               "                   rate: publish rows on a fixed HZ grid instead of the\n"
               "                   CSV's own intervals; the recorded sample times (and\n"
               "                   the measured rate/jitter) are unchanged. viture: ignored.\n"
               "  --out FILE       write the samples to FILE (replay-compatible CSV)\n"
               "  --print-every N  print a progress line every N samples (default 0 = off)\n"
               "  --display        viture: run the U-08 display check on the live\n"
               "                   session (refresh read, 90 Hz SBS round trip)\n"
               "  -h, --help       print this help\n"
               "\n"
               "csv columns: host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status\n"
               "  host_time_ns  mapped host-timeline sample time, integer nanoseconds\n"
               "  sdk_time_s    viture: the sample's true SDK seconds stamp\n"
               "                (VitureHeadPoseSource::LastSdkSeconds, for HIL\n"
               "                latency analysis); host-derived seconds until the\n"
               "                first stamp is available, with a warning.\n"
               "                fake|replay: host-derived seconds (those sources\n"
               "                carry no SDK stamp)\n"
               "  px,py,pz      position (meters); qw,qx,qy,qz rotation quaternion\n"
               "  status        stable, unstable or lost\n"
               "\n"
               "exit codes: 0 at least one sample; 2 loader failure (any viture library\n"
               "load failure, including InvalidArgument) or source Unsupported/NotReady;\n"
               "3 vendor teardown wedged (report and CSV already written); 1 otherwise.\n",
               stream);
}

bool ParseDouble(std::string_view text, double &out) noexcept {
    if (text.empty()) {
        return false;
    }
    const char *first = text.data();
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, out, std::chars_format::general);
    return result.ec == std::errc{} && result.ptr == last && std::isfinite(out);
}

bool ParseUint64(std::string_view text, std::uint64_t &out) noexcept {
    if (text.empty()) {
        return false;
    }
    const char *first = text.data();
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, out);
    return result.ec == std::errc{} && result.ptr == last;
}

/// Copies the next argv element into `value`. Returns false at the end of argv.
bool NextValue(int argc, char **argv, int &index, std::string_view name, std::string_view &value, std::string &error) {
    if (index + 1 >= argc) {
        error = std::string(name) + " requires a value";
        return false;
    }
    ++index;
    value = std::string_view{argv[index]};
    return true;
}

enum class ParseOutcome { Ok, Help, Error };

ParseOutcome ParseOptions(int argc, char **argv, Options &options, std::string &error) {
    for (int index = 1; index < argc; ++index) {
        const std::string_view arg{argv[index]};
        std::string_view value;

        if (arg == "-h" || arg == "--help") {
            return ParseOutcome::Help;
        }
        if (arg == "--source") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            if (value == "fake") {
                options.source = SourceKind::Fake;
            } else if (value == "replay") {
                options.source = SourceKind::Replay;
            } else if (value == "viture") {
                options.source = SourceKind::Viture;
            } else {
                error = "unknown source \"" + std::string(value) + "\" (expected fake|replay|viture)";
                return ParseOutcome::Error;
            }
            continue;
        }
        if (arg == "--dll") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            options.dll = std::string(value);
            continue;
        }
        if (arg == "--csv") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            options.csv = std::string(value);
            continue;
        }
        if (arg == "--seconds") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            if (!ParseDouble(value, options.seconds) || options.seconds <= 0.0) {
                error = "--seconds must be a finite positive number";
                return ParseOutcome::Error;
            }
            continue;
        }
        if (arg == "--rate") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            // A period that rounds to 0 ns would spin the fake driver and make
            // the replay fixed grid degenerate (CXX-08).
            if (!ParseDouble(value, options.rate_hz) || options.rate_hz <= 0.0 ||
                cg::core_math::ToNanoseconds(1.0 / options.rate_hz) <= 0) {
                error = "--rate must be a finite positive rate whose period is at least 1 ns";
                return ParseOutcome::Error;
            }
            options.rate_set = true;
            continue;
        }
        if (arg == "--out") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            options.out = std::string(value);
            continue;
        }
        if (arg == "--print-every") {
            if (!NextValue(argc, argv, index, arg, value, error)) {
                return ParseOutcome::Error;
            }
            if (!ParseUint64(value, options.print_every)) {
                error = "--print-every must be a non-negative integer";
                return ParseOutcome::Error;
            }
            continue;
        }
        if (arg == "--display") {
            options.display = true;
            continue;
        }

        error = "unknown option: " + std::string(arg);
        return ParseOutcome::Error;
    }
    return ParseOutcome::Ok;
}

// --- measurement ------------------------------------------------------------

struct SampleLog {
    std::vector<HeadSample> samples;
    std::vector<double> sdk_seconds; // per-sample sdk_time_s column value
    std::uint64_t polls = 0;
    std::uint64_t failed_polls = 0;
    bool dataset_exhausted = false;
    bool warned_missing_sdk = false;
};

HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, cg::Pose{cg::core_math::Vec3{0.0, 0.0, 0.0}, cg::core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

const char *StateName(TrackState state) noexcept {
    switch (state) {
    case TrackState::Stable:
        return "stable";
    case TrackState::Unstable:
        return "unstable";
    case TrackState::Lost:
        return "lost";
    }
    return "?";
}

void PrintSampleLine(const char *label, const HeadSample &sample) {
    std::printf("%s: t=%.9f s seq=%u state=%s pos=(%.6f, %.6f, %.6f) quat=(%.6f, %.6f, %.6f, %.6f)\n", label,
                ToSeconds(sample.time), static_cast<unsigned>(sample.seq), StateName(sample.state),
                sample.pose.position.x, sample.pose.position.y, sample.pose.position.z, sample.pose.rotation.w(),
                sample.pose.rotation.x(), sample.pose.rotation.y(), sample.pose.rotation.z());
}

/// Reads the newest sample; when it is new, records it (with its
/// `sdk_time_s` column value) and honors `--print-every`. `stamp_source` is
/// the viture source when its diagnostics accessor should supply the true SDK
/// stamp; it is null for fake and replay, which carry no SDK stamp. Returns
/// true when a new sample was recorded.
bool TryRecordNewest(IHeadPoseSource &source, const Options &options, SampleLog &log,
                     const VitureHeadPoseSource *stamp_source = nullptr) {
    HeadSample sample = PlaceholderSample();
    ++log.polls;
    if (!source.TryGetLatest(sample, Duration{0})) {
        ++log.failed_polls;
        return false;
    }
    if (!log.samples.empty() && sample.seq == log.samples.back().seq) {
        return false;
    }

    double sdk_seconds = ToSeconds(sample.time);
    if (stamp_source != nullptr) {
        const std::optional<double> stamp = stamp_source->LastSdkSeconds();
        if (stamp.has_value()) {
            sdk_seconds = *stamp;
        } else if (!log.warned_missing_sdk) {
            std::fprintf(stderr,
                         "warning: the viture source has no SDK stamp yet; writing host-derived seconds for now\n");
            log.warned_missing_sdk = true;
        }
    }

    log.samples.push_back(sample);
    log.sdk_seconds.push_back(sdk_seconds);
    if (options.print_every != 0 && log.samples.size() % static_cast<std::size_t>(options.print_every) == 0) {
        std::printf("progress: ");
        PrintSampleLine("sample", sample);
    }
    return true;
}

double Percentile(std::vector<double> values, double fraction) {
    if (values.empty()) {
        return 0.0;
    }
    std::sort(values.begin(), values.end());
    const double index = fraction * static_cast<double>(values.size() - 1);
    const std::size_t lower = static_cast<std::size_t>(index);
    const std::size_t upper = std::min(lower + 1, values.size() - 1);
    const double weight = index - static_cast<double>(lower);
    return values[lower] * (1.0 - weight) + values[upper] * weight;
}

void PrintReport(const Options &options, const SampleLog &log) {
    const std::size_t count = log.samples.size();
    const HeadSample &first = log.samples.front();
    const HeadSample &last = log.samples.back();

    double rate_hz = 0.0;
    double jitter_p50_us = 0.0;
    double jitter_p95_us = 0.0;
    double jitter_p99_us = 0.0;
    if (count >= 2U) {
        const double span_s = ToSeconds(last.time - first.time);
        if (span_s > 0.0) {
            rate_hz = static_cast<double>(count - 1U) / span_s;
        }
        const double mean_interval_ns = static_cast<double>(last.time - first.time) / static_cast<double>(count - 1U);
        std::vector<double> deviations_ns;
        deviations_ns.reserve(count - 1U);
        for (std::size_t index = 1; index < count; ++index) {
            const double interval_ns = static_cast<double>(log.samples[index].time - log.samples[index - 1U].time);
            deviations_ns.push_back(std::fabs(interval_ns - mean_interval_ns));
        }
        jitter_p50_us = Percentile(deviations_ns, 0.50) / 1e3;
        jitter_p95_us = Percentile(deviations_ns, 0.95) / 1e3;
        jitter_p99_us = Percentile(deviations_ns, 0.99) / 1e3;
    }

    std::uint64_t stable = 0;
    std::uint64_t unstable = 0;
    std::uint64_t lost = 0;
    for (const HeadSample &sample : log.samples) {
        switch (sample.state) {
        case TrackState::Stable:
            ++stable;
            break;
        case TrackState::Unstable:
            ++unstable;
            break;
        case TrackState::Lost:
            ++lost;
            break;
        }
    }

    std::printf("source: %s\n", SourceName(options.source));
    std::printf("samples: %zu  sample span: %.9f s  measured rate: %.3f Hz\n", count, ToSeconds(last.time - first.time),
                rate_hz);
    std::printf("jitter (|interval - mean|): p50=%.3f us  p95=%.3f us  p99=%.3f us\n", jitter_p50_us, jitter_p95_us,
                jitter_p99_us);
    std::printf("status: stable=%llu unstable=%llu lost=%llu\n", static_cast<unsigned long long>(stable),
                static_cast<unsigned long long>(unstable), static_cast<unsigned long long>(lost));
    std::printf("polls: %llu (failed: %llu)%s\n", static_cast<unsigned long long>(log.polls),
                static_cast<unsigned long long>(log.failed_polls), log.dataset_exhausted ? "  dataset exhausted" : "");
    PrintSampleLine("first", first);
    PrintSampleLine("last ", last);
}

bool WriteCsv(const std::string &path, const SampleLog &log, std::string &error) {
    std::ofstream file(path, std::ios::binary);
    if (!file.is_open()) {
        error = "cannot open output CSV: " + path;
        return false;
    }
    file << "host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status\n";
    file.setf(std::ios::fixed, std::ios::floatfield);
    file.precision(9);
    for (std::size_t index = 0; index < log.samples.size(); ++index) {
        const HeadSample &sample = log.samples[index];
        file << sample.time << ',' << log.sdk_seconds[index] << ',' << sample.pose.position.x << ','
             << sample.pose.position.y << ',' << sample.pose.position.z << ',' << sample.pose.rotation.w() << ','
             << sample.pose.rotation.x() << ',' << sample.pose.rotation.y() << ',' << sample.pose.rotation.z() << ','
             << StateName(sample.state) << '\n';
    }
    file.flush();
    if (!file) {
        error = "failed while writing output CSV: " + path;
        return false;
    }
    std::printf("csv: wrote %zu samples to %s\n", log.samples.size(), path.c_str());
    return true;
}

int ExitForStatus(const Status &status) {
    std::fprintf(stderr, "cg-pose-probe: %s\n", status.message());
    if (status.code() == StatusCode::Unsupported || status.code() == StatusCode::NotReady) {
        return 2;
    }
    return 1;
}

/// Status word for the `--display` report (diagnostics only).
const char *StatusCodeName(StatusCode code) noexcept {
    switch (code) {
    case StatusCode::Ok:
        return "ok";
    case StatusCode::InvalidArgument:
        return "invalid-argument";
    case StatusCode::NotReady:
        return "not-ready";
    case StatusCode::Device:
        return "device";
    case StatusCode::Timeout:
        return "timeout";
    case StatusCode::Unsupported:
        return "unsupported";
    case StatusCode::Internal:
        return "internal";
    }
    return "?";
}

/// `--display`: reads the current refresh rate and reports it.
void ReportRefresh(IVitureApi &api, const char *label) {
    const cg::Result<std::uint32_t> refresh = api.GetRefreshHz();
    if (refresh.ok()) {
        std::printf("display: %s: %u Hz\n", label, static_cast<unsigned>(*refresh));
    } else {
        std::printf("display: %s: failed: %s (%s)\n", label, refresh.status().message(),
                    StatusCodeName(refresh.status().code()));
    }
}

/// `--display`: reports a display-seam call's status.
void PrintDisplayStatus(const char *label, const Status &status) {
    if (status.code() == StatusCode::Ok) {
        std::printf("display: %s: ok\n", label);
    } else {
        std::printf("display: %s: failed: %s (%s)\n", label, status.message(), StatusCodeName(status.code()));
    }
}

/// `--display` (U-08 HIL check): runs on the live session created and started
/// by the source (a display-only create/destroy window crashes inside the
/// vendor `destroy`, observed on HIL 2026-10-06). Reads the refresh rate,
/// switches 90 Hz SBS and back to 2D 90, settling 500 ms between a set and
/// its readback so an asynchronous mode switch is observable.
void RunDisplayCheck(IVitureApi &api) {
    std::printf("display: HIL check (U-08, live session)\n");
    const std::string version = api.SdkVersion();
    std::printf("display: sdk/firmware: %s\n", version.empty() ? "(unavailable)" : version.c_str());
    ReportRefresh(api, "initial refresh");
    PrintDisplayStatus("set 90 Hz SBS", api.SetDisplayMode(90, true).status());
    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    ReportRefresh(api, "refresh after SBS 90");
    PrintDisplayStatus("set 90 Hz 2D", api.SetDisplayMode(90, false).status());
    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    ReportRefresh(api, "refresh after 2D 90");
    PrintDisplayStatus("restore 2D 60", api.SetDisplayMode(60, false).status());
}

/// Shared tail: no samples is an error, then the report and the optional CSV.
int Finish(const Options &options, const SampleLog &log) {
    if (log.samples.empty()) {
        std::fprintf(stderr, "cg-pose-probe: %s source produced no samples\n", SourceName(options.source));
        return 1;
    }
    PrintReport(options, log);
    if (options.source != SourceKind::Viture) {
        std::printf("sdk_time_s: host-derived seconds (the %s source carries no SDK stamp)\n",
                    SourceName(options.source));
    }
    if (!options.out.empty()) {
        std::string error;
        if (!WriteCsv(options.out, log, error)) {
            std::fprintf(stderr, "cg-pose-probe: %s\n", error.c_str());
            return 1;
        }
    }
    return 0;
}

// --- drivers ----------------------------------------------------------------

HostTime ElapsedSince(std::chrono::steady_clock::time_point start) noexcept {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count();
}

/// Sleeps until `deadline`, then yields-spins the last half millisecond. The
/// coarse part is a `sleep_until` on the absolute deadline, which stops the
/// Windows tick from accumulating drift; the spin buys sub-millisecond pacing.
void WaitUntil(std::chrono::steady_clock::time_point deadline) {
    const auto coarse = deadline - std::chrono::microseconds(500);
    while (std::chrono::steady_clock::now() < coarse) {
        std::this_thread::sleep_until(coarse);
    }
    while (std::chrono::steady_clock::now() < deadline) {
        std::this_thread::yield();
    }
}

bool TimeLimitReached(HostTime elapsed_ns, double seconds) noexcept {
    return static_cast<double>(elapsed_ns) / kNanosecondsPerSecond >= seconds;
}

/// Reads an environment variable without the MSVC `getenv` deprecation warning.
std::string EnvironmentValue(const char *name) {
#if defined(_MSC_VER)
    char *buffer = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&buffer, &size, name) != 0 || buffer == nullptr) {
        return {};
    }
    std::string value{buffer};
    std::free(buffer);
    return value;
#else
    const char *value = std::getenv(name);
    return value == nullptr ? std::string{} : std::string{value};
#endif
}

int RunFake(const Options &options, SampleLog &log) {
    ManualClock clock;
    FakeHeadPoseSource source(FakeHeadPoseSourceConfig{options.rate_hz, &clock, FakeScript::YawSweep(30.0)});
    const cg::Result<void> started = source.Start();
    if (!started.ok()) {
        return ExitForStatus(started.status());
    }

    const auto start = std::chrono::steady_clock::now();
    const HostTime period_ns = static_cast<HostTime>(std::llround(kNanosecondsPerSecond / options.rate_hz));
    std::uint64_t emitted = 0;
    for (;;) {
        const HostTime elapsed_ns = ElapsedSince(start);
        if (TimeLimitReached(elapsed_ns, options.seconds)) {
            break;
        }
        // One sample per pass, emitted at its absolute wall deadline. The
        // fake's clock is dragged onto the steady host timeline first, so the
        // sample time is a host instant and the jitter below is the probe's
        // own pacing error.
        const HostTime due_ns = static_cast<HostTime>(emitted) * period_ns;
        if (due_ns > elapsed_ns) {
            WaitUntil(start + std::chrono::nanoseconds{due_ns});
        }
        const HostTime wall_ns = ElapsedSince(start);
        const HostTime delta_ns = wall_ns - clock.Now();
        if (delta_ns > 0) {
            clock.Advance(Duration{delta_ns});
        }
        source.AdvanceSamples(1);
        ++emitted;
        TryRecordNewest(source, options, log);
    }
    source.Stop();
    return Finish(options, log);
}

int RunReplay(const Options &options, SampleLog &log) {
    ManualClock clock;
    ReplayHeadPoseSource source(std::filesystem::path{options.csv}, clock);
    const cg::Result<void> loaded = source.Load();
    if (!loaded.ok()) {
        return ExitForStatus(loaded.status());
    }
    const cg::Result<void> started = source.Start();
    if (!started.ok()) {
        return ExitForStatus(started.status());
    }

    if (options.rate_set) {
        std::printf("pacing: replay rows published on a fixed %.3f Hz wall grid; the recorded sample times "
                    "(and the measured rate/jitter) are unchanged\n",
                    options.rate_hz);
    }

    const auto start = std::chrono::steady_clock::now();
    const HostTime fixed_period_ns =
        options.rate_set ? static_cast<HostTime>(std::llround(kNanosecondsPerSecond / options.rate_hz)) : 0;
    HostTime next_wall_ns = 0;
    for (;;) {
        const HostTime elapsed_ns = ElapsedSince(start);
        if (TimeLimitReached(elapsed_ns, options.seconds)) {
            break;
        }
        if (next_wall_ns > elapsed_ns) {
            WaitUntil(start + std::chrono::nanoseconds{next_wall_ns});
        }

        const HostTime previous_time = log.samples.empty() ? 0 : log.samples.back().time;
        const std::size_t before = source.next_row_index();
        source.PublishNext();
        if (source.next_row_index() == before) {
            log.dataset_exhausted = true;
            break;
        }
        TryRecordNewest(source, options, log);

        if (options.rate_set) {
            next_wall_ns += fixed_period_ns;
        } else if (log.samples.size() >= 2U) {
            // The CSV's own intervals: wait one recorded interval before the
            // next row. The second row is published immediately to learn the
            // first interval, so all later rows keep the CSV's cadence.
            next_wall_ns += log.samples.back().time - previous_time;
        } else {
            next_wall_ns = 0;
        }
    }
    source.Stop();
    return Finish(options, log);
}

int RunViture(const Options &options, SampleLog &log) {
    std::string dll = options.dll;
    if (dll.empty()) {
        dll = EnvironmentValue("CG_VITURE_DLL");
    }

    cg::Result<std::unique_ptr<IVitureApi>> loaded = LoadVitureApi(dll);
    if (!loaded.ok()) {
        std::fprintf(stderr, "cg-pose-probe: viture: %s\n", loaded.status().message());
        return 2; // The brief fixes the load-failure exit code at 2.
    }
    IVitureApi &api = **loaded;

    SteadyHostClock clock;
    VitureHeadPoseSource source(api, clock);
    std::fprintf(stderr, "source: starting (create + start)\n");
    const cg::Result<void> started = source.Start();
    if (!started.ok()) {
        return ExitForStatus(started.status());
    }
    std::fprintf(stderr, "source: started; polling for %.3f s\n", options.seconds);

    if (options.display) {
        RunDisplayCheck(api);
    }

    const auto start = std::chrono::steady_clock::now();
    HostTime next_heartbeat_ns = 0;
    for (;;) {
        const HostTime elapsed_ns = ElapsedSince(start);
        if (TimeLimitReached(elapsed_ns, options.seconds)) {
            break;
        }
        if (elapsed_ns >= next_heartbeat_ns) {
            // HIL heartbeat: shows whether samples arrive at all and whether the
            // SDK stamp (pose callback) is advancing, even when the report is
            // still far away.
            const std::optional<double> sdk = source.LastSdkSeconds();
            char stamp_text[32] = "none";
            if (sdk.has_value()) {
                static_cast<void>(std::snprintf(stamp_text, sizeof(stamp_text), "%.6f", *sdk));
            }
            std::fprintf(stderr, "poll: %.1f s, samples=%zu, sdk_stamp=%s\n", ToSeconds(elapsed_ns), log.samples.size(),
                         stamp_text);
            next_heartbeat_ns += 1'000'000'000;
        }
        TryRecordNewest(source, options, log, &source);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    std::fprintf(stderr, "source: stopping\n");

    // Report and CSV before teardown: a vendor teardown that never returns
    // (observed on HIL when the USB data stream wedges) must not lose the run.
    const int finish_code = Finish(options, log);

    // Bounded Stop: the seam contract says Stop joins the polling thread; when
    // the vendor call blocks inside PollPose or teardown, the join can never
    // complete. The watchdog turns that into a documented exit code 3 instead
    // of a hung HIL log.
    std::atomic<bool> stop_returned{false};
    std::thread watchdog([&stop_returned] {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (!stop_returned.load(std::memory_order_acquire)) {
            if (std::chrono::steady_clock::now() >= deadline) {
                std::fprintf(stderr,
                             "cg-pose-probe: source.Stop() did not return within 5 s; the vendor teardown is "
                             "wedged, forcing exit (the report and CSV above are already written)\n");
                std::fflush(stderr);
                std::_Exit(3);
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        }
    });
    source.Stop();
    stop_returned.store(true, std::memory_order_release);
    watchdog.join();
    std::fprintf(stderr, "source: stopped\n");
    return finish_code;
}

} // namespace

int main(int argc, char **argv) {
    Options options;
    std::string error;
    const ParseOutcome outcome = ParseOptions(argc, argv, options, error);
    if (outcome == ParseOutcome::Help) {
        PrintUsage(stdout);
        return 0;
    }
    if (outcome == ParseOutcome::Error) {
        std::fprintf(stderr, "cg-pose-probe: %s\n", error.c_str());
        PrintUsage(stderr);
        return 1;
    }
    if (options.source == SourceKind::Replay && options.csv.empty()) {
        std::fprintf(stderr, "cg-pose-probe: --source replay requires --csv <path>\n");
        return 1;
    }

#ifdef _WIN32
    const ScopedTimerResolution timer_resolution;
#endif

    // Unbuffered stdout: this is the HIL instrument, and a vendor-library fault
    // must not swallow the progress log with the CRT buffer.
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    SampleLog log;
    switch (options.source) {
    case SourceKind::Fake:
        return RunFake(options, log);
    case SourceKind::Replay:
        return RunReplay(options, log);
    case SourceKind::Viture:
        return RunViture(options, log);
    }
    return 1;
}
