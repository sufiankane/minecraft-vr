// glasses_soak: the S5 glasses soak and RSS leak gate (task 4).
//
// Drives the deterministic fake head pose source at a wall-paced `--rate` for
// `--minutes` while a consumer thread reads `TryGetLatest` continuously. Every
// 60 s it prints the elapsed time, the published/read/fresh sample counts and
// the current process RSS. At exit it prints the RSS growth after the
// first-minute baseline and exits non-zero when that growth exceeds 1 MiB.
// Ctrl+C stops the run cleanly through a signal flag.
//
// `fresh` counts reads that observed a new sequence number; `read` counts every
// successful TryGetLatest (the render-path read), and `misses` counts the
// transient false reads (before the first publish or a bounded-retry
// exhaustion). The fake's ManualClock is dragged onto the steady host timeline
// so a sample time is a host instant.
//
// usage: glasses_soak [--rate HZ] [--minutes N]
//
// exit codes: 0 clean run within the RSS budget; 1 RSS growth over the budget,
// a source failure or a usage error.

#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <string_view>
#include <system_error>
#include <thread>

#include "cg/core_math/time.hpp"
#include "cg/glasses/fake_head_pose_source.hpp"
#include "cg/glasses/manual_clock.hpp"
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

#include <psapi.h>
#include <timeapi.h>
#else
#include <sys/resource.h>
#endif

namespace {

using cg::Duration;
using cg::HeadSample;
using cg::TrackState;

using cg::core_math::HostTime;
using cg::core_math::ToSeconds;

using cg::glasses::FakeHeadPoseSource;
using cg::glasses::FakeHeadPoseSourceConfig;
using cg::glasses::FakeScript;
using cg::glasses::ManualClock;

constexpr double kNanosecondsPerSecond = 1e9;
constexpr double kReportIntervalSeconds = 60.0;
constexpr std::uint64_t kRssBudgetBytes = 1024ULL * 1024ULL;

volatile std::sig_atomic_t g_stop_requested = 0;

void OnStopSignal(int /*signal*/) noexcept { g_stop_requested = 1; }

#ifdef _WIN32
/// Raises the system timer to 1 ms for the run so the 500 Hz producer's
/// `sleep_until` deadlines are not quantised to the ~15.6 ms default tick.
/// Restored on scope exit.
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

struct Options {
    double rate_hz = 500.0;
    double minutes = 30.0;
};

enum class ParseOutcome { Ok, Help, Error };

bool ParseDouble(std::string_view text, double &out) noexcept {
    if (text.empty()) {
        return false;
    }
    const char *first = text.data();
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, out, std::chars_format::general);
    return result.ec == std::errc{} && result.ptr == last && std::isfinite(out);
}

void PrintUsage(std::FILE *stream) {
    std::fputs("glasses_soak: S5 fake-source soak and RSS leak gate\n"
               "\n"
               "usage: glasses_soak [--rate HZ] [--minutes N]\n"
               "\n"
               "options:\n"
               "  --rate HZ     fake source sample rate in hertz (default 500)\n"
               "  --minutes N   run time in minutes (default 30)\n"
               "  -h, --help    print this help\n"
               "\n"
               "A producer publishes fake samples at wall-paced --rate deadlines while\n"
               "a consumer thread reads TryGetLatest continuously. Every 60 s the tool\n"
               "prints the elapsed time, the published/read/fresh sample counts and the\n"
               "current process RSS; at exit it prints the RSS growth after the\n"
               "first-minute baseline and exits non-zero when it exceeds 1 MiB.\n",
               stream);
}

ParseOutcome ParseOptions(int argc, char **argv, Options &options, std::string_view &error) {
    for (int index = 1; index < argc; ++index) {
        const std::string_view arg{argv[index]};
        if (arg == "-h" || arg == "--help") {
            return ParseOutcome::Help;
        }
        if (index + 1 >= argc) {
            error = "missing value for option";
            return ParseOutcome::Error;
        }
        const std::string_view value{argv[++index]};
        if (arg == "--rate") {
            if (!ParseDouble(value, options.rate_hz) || options.rate_hz <= 0.0) {
                error = "--rate must be a finite positive number";
                return ParseOutcome::Error;
            }
        } else if (arg == "--minutes") {
            if (!ParseDouble(value, options.minutes) || options.minutes <= 0.0) {
                error = "--minutes must be a finite positive number";
                return ParseOutcome::Error;
            }
        } else {
            error = "unknown option";
            return ParseOutcome::Error;
        }
    }
    return ParseOutcome::Ok;
}

/// The process RSS in bytes: current working set on Windows, the `getrusage`
/// peak resident set on POSIX (`ru_maxrss` is KiB on Linux, bytes on macOS).
std::uint64_t ResidentBytes() noexcept {
#if defined(_WIN32)
    PROCESS_MEMORY_COUNTERS counters{};
    if (::GetProcessMemoryInfo(::GetCurrentProcess(), &counters, sizeof(counters)) == 0) {
        return 0;
    }
    return static_cast<std::uint64_t>(counters.WorkingSetSize);
#else
    rusage usage{};
    if (::getrusage(RUSAGE_SELF, &usage) != 0) {
        return 0;
    }
#if defined(__APPLE__)
    return static_cast<std::uint64_t>(usage.ru_maxrss);
#else
    return static_cast<std::uint64_t>(usage.ru_maxrss) * 1024ULL;
#endif
#endif
}

double Mib(std::uint64_t bytes) noexcept { return static_cast<double>(bytes) / (1024.0 * 1024.0); }

HeadSample PlaceholderSample() noexcept {
    return HeadSample{0, cg::Pose{cg::core_math::Vec3{0.0, 0.0, 0.0}, cg::core_math::Quat::kIdentity},
                      TrackState::Stable, 0};
}

HostTime ElapsedSince(std::chrono::steady_clock::time_point start) noexcept {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - start).count();
}

/// Sleeps until `deadline`, then yield-spins the last half millisecond, the
/// probe's wall-pacing rule: `sleep_until` on the absolute deadline keeps the
/// tick from accumulating drift and the spin buys sub-millisecond pacing.
void WaitUntil(std::chrono::steady_clock::time_point deadline) {
    const auto coarse = deadline - std::chrono::microseconds(500);
    while (std::chrono::steady_clock::now() < coarse) {
        std::this_thread::sleep_until(coarse);
    }
    while (std::chrono::steady_clock::now() < deadline) {
        std::this_thread::yield();
    }
}

struct ReadCounters {
    std::atomic<std::uint64_t> reads{0};
    std::atomic<std::uint64_t> fresh{0};
    std::atomic<std::uint64_t> misses{0};
};

/// The render-path consumer: a tight TryGetLatest loop until the producer
/// finishes or the stop flag is set. Yields every 1024 iterations so a
/// single-core runner can schedule the producer.
void Consume(const cg::IHeadPoseSource &source, ReadCounters &counters, const std::atomic<bool> &producing) noexcept {
    HeadSample sample = PlaceholderSample();
    std::uint32_t last_seq = 0;
    bool have_sample = false;
    std::uint64_t iterations = 0;
    while (producing.load(std::memory_order_relaxed) && g_stop_requested == 0) {
        if (source.TryGetLatest(sample, Duration{0})) {
            counters.reads.fetch_add(1, std::memory_order_relaxed);
            if (!have_sample || sample.seq != last_seq) {
                counters.fresh.fetch_add(1, std::memory_order_relaxed);
                last_seq = sample.seq;
                have_sample = true;
            }
        } else {
            counters.misses.fetch_add(1, std::memory_order_relaxed);
        }
        if ((++iterations & 0x3FFU) == 0U) {
            std::this_thread::yield();
        }
    }
}

int Run(const Options &options) {
    ManualClock clock;
    FakeHeadPoseSource source(FakeHeadPoseSourceConfig{options.rate_hz, &clock, FakeScript::YawSweep(30.0)});
    const cg::Result<void> started = source.Start();
    if (!started.ok()) {
        std::fprintf(stderr, "glasses_soak: fake source start failed: %s\n", started.status().message());
        return 1;
    }

    ReadCounters counters;
    std::atomic<bool> producing{true};
    std::thread consumer([&source, &counters, &producing] { Consume(source, counters, producing); });

    const auto start = std::chrono::steady_clock::now();
    const double total_seconds = options.minutes * 60.0;
    const HostTime period_ns = static_cast<HostTime>(std::llround(kNanosecondsPerSecond / options.rate_hz));
    std::uint64_t published = 0;
    std::uint64_t baseline_rss = 0;
    bool have_baseline = false;
    double baseline_seconds = 0.0;
    double next_report_seconds = kReportIntervalSeconds;

    std::printf("glasses_soak: rate=%.3f Hz minutes=%.3f rss budget=%.2f MiB\n", options.rate_hz, options.minutes,
                Mib(kRssBudgetBytes));
    std::fflush(stdout);

    for (;;) {
        const HostTime elapsed_ns = ElapsedSince(start);
        const double elapsed_seconds = ToSeconds(elapsed_ns);
        if (g_stop_requested != 0 || elapsed_seconds >= total_seconds) {
            break;
        }

        if (elapsed_seconds >= next_report_seconds) {
            const std::uint64_t rss = ResidentBytes();
            std::printf("[soak] t=%.1f s published=%llu read=%llu fresh=%llu misses=%llu rss=%.2f MiB\n",
                        elapsed_seconds, static_cast<unsigned long long>(published),
                        static_cast<unsigned long long>(counters.reads.load(std::memory_order_relaxed)),
                        static_cast<unsigned long long>(counters.fresh.load(std::memory_order_relaxed)),
                        static_cast<unsigned long long>(counters.misses.load(std::memory_order_relaxed)), Mib(rss));
            std::fflush(stdout);
            if (!have_baseline) {
                baseline_rss = rss;
                baseline_seconds = elapsed_seconds;
                have_baseline = true;
            }
            do {
                next_report_seconds += kReportIntervalSeconds;
            } while (elapsed_seconds >= next_report_seconds);
        }

        // One sample per pass at its absolute wall deadline; a late pass emits
        // immediately to catch up, exactly like the probe's fake driver.
        const HostTime due_ns = static_cast<HostTime>(published) * period_ns;
        if (due_ns > elapsed_ns) {
            WaitUntil(start + std::chrono::nanoseconds{due_ns});
        }
        const HostTime wall_ns = ElapsedSince(start);
        const HostTime delta_ns = wall_ns - clock.Now();
        if (delta_ns > 0) {
            clock.Advance(Duration{delta_ns});
        }
        source.AdvanceSamples(1);
        ++published;
    }

    producing.store(false, std::memory_order_relaxed);
    consumer.join();
    source.Stop();

    const double elapsed_seconds = ToSeconds(ElapsedSince(start));
    const std::uint64_t final_rss = ResidentBytes();
    std::printf("[soak] done t=%.1f s published=%llu read=%llu fresh=%llu misses=%llu rss=%.2f MiB\n", elapsed_seconds,
                static_cast<unsigned long long>(published),
                static_cast<unsigned long long>(counters.reads.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(counters.fresh.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(counters.misses.load(std::memory_order_relaxed)), Mib(final_rss));
    if (g_stop_requested != 0) {
        std::printf("[soak] stopped by signal\n");
    }

    if (!have_baseline) {
        std::printf("[soak] rss delta: no baseline (run shorter than %.0f s)\n", kReportIntervalSeconds);
        std::fflush(stdout);
        return 0;
    }

    const std::uint64_t delta = final_rss > baseline_rss ? final_rss - baseline_rss : 0;
    std::printf("[soak] rss baseline (t=%.1f s)=%.2f MiB final=%.2f MiB delta=%.2f MiB (budget %.2f MiB)\n",
                baseline_seconds, Mib(baseline_rss), Mib(final_rss), Mib(delta), Mib(kRssBudgetBytes));
    if (delta > kRssBudgetBytes) {
        std::printf("[soak] FAIL: RSS growth %.2f MiB exceeds the %.2f MiB budget\n", Mib(delta), Mib(kRssBudgetBytes));
        std::fflush(stdout);
        return 1;
    }
    std::printf("[soak] PASS: RSS growth %.2f MiB within the %.2f MiB budget\n", Mib(delta), Mib(kRssBudgetBytes));
    std::fflush(stdout);
    return 0;
}

} // namespace

int main(int argc, char **argv) {
    std::signal(SIGINT, OnStopSignal);
    std::signal(SIGTERM, OnStopSignal);

    Options options;
    std::string_view error;
    const ParseOutcome outcome = ParseOptions(argc, argv, options, error);
    if (outcome == ParseOutcome::Help) {
        PrintUsage(stdout);
        return 0;
    }
    if (outcome == ParseOutcome::Error) {
        std::fprintf(stderr, "glasses_soak: %s\n", error.data());
        PrintUsage(stderr);
        return 1;
    }

#ifdef _WIN32
    const ScopedTimerResolution timer_resolution;
#endif
    return Run(options);
}
