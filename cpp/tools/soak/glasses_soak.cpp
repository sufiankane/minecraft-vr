// glasses_soak: the S5 glasses soak, leak and latency gate (task 4, TD-007).
//
// Drives the deterministic fake head pose source at a wall-paced `--rate` for
// `--minutes` while a consumer thread reads `TryGetLatest` continuously. Every
// 60 s it prints the elapsed time, the published/read/fresh sample counts, the
// current process RSS and the cumulative read-to-read gap p95 (the gap
// between consecutive successful reads, a fixed log2-nanosecond histogram,
// allocation-free).
//
// At exit four gates are evaluated:
//   - RSS growth after the first-minute baseline must stay within 1 MiB
//     (CXX-04: a failed reading fails the gate);
//   - the read-to-read gap p95 must stay at or below `--latency-p95-ms`
//     (default 50 ms, deliberately generous for loaded CI runners);
//   - the process thread count and handle count must not exceed the baseline
//     taken right after the consumer starts (NFR-07's no-leaked-threads-or-
//     handles half; Windows toolhelp/handle-count, Linux /proc).
// Ctrl+C stops the run cleanly through a signal flag.
//
// The RSS reading is the current resident set: `GetProcessMemoryInfo` working
// set on Windows and `/proc/self/statm` on Linux (not the `getrusage` peak, so
// growth that stays below a transient peak is still visible); macOS falls back
// to `ru_maxrss` and has no cheap thread/handle query, so the count gates are
// skipped there and reported as such.
//
// `fresh` counts reads that observed a new sequence number; `read` counts every
// successful TryGetLatest (the render-path read), and `misses` counts the
// transient false reads (before the first publish or a bounded-retry
// exhaustion). The fake's ManualClock is dragged onto the steady host timeline
// so a sample time is a host instant.
//
// usage: glasses_soak [--rate HZ] [--minutes N] [--latency-p95-ms N]
//
// exit codes: 0 clean run within every gate; 1 a gate failed, a source failure
// or a usage error.

#include <atomic>
#include <bit>
#include <charconv>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <optional>
#include <string_view>
#include <system_error>
#include <thread>

#include "cg/core_math/time.hpp"
#include "cg/glasses/fake_head_pose_source.hpp"
#include "cg/glasses/manual_clock.hpp"
#include "ports.hpp"
#include "result.hpp"
#include "rss_gate.hpp"

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
#include <tlhelp32.h>
#else
#include <dirent.h>
#include <sys/resource.h>
#include <unistd.h>
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
/// Upper-bound buckets for the read-to-read gap histogram: bucket `i` holds
/// gaps below 2^i nanoseconds (bucket 0 is a zero gap), so the p95 estimate is
/// the covering bucket's upper bound.
constexpr int kLatencyBuckets = 40;

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
    /// Read-to-read gap p95 budget in milliseconds (TD-007). Generous by
    /// default: the soak is a regression gate, not a benchmark, and a loaded
    /// CI runner must not fail on scheduling noise.
    double latency_p95_ms = 50.0;
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
    std::fputs("glasses_soak: S5 fake-source soak, RSS leak and latency gate\n"
               "\n"
               "usage: glasses_soak [--rate HZ] [--minutes N] [--latency-p95-ms N]\n"
               "\n"
               "options:\n"
               "  --rate HZ            fake source sample rate in hertz (default 500)\n"
               "  --minutes N          run time in minutes (default 30)\n"
               "  --latency-p95-ms N   read-to-read gap p95 budget in ms (default 50)\n"
               "  -h, --help           print this help\n"
               "\n"
               "A producer publishes fake samples at wall-paced --rate deadlines while\n"
               "a consumer thread reads TryGetLatest continuously. Every 60 s the tool\n"
               "prints the elapsed time, the published/read/fresh sample counts, the\n"
               "current process RSS and the cumulative read-to-read gap p95. At exit it\n"
               "gates the RSS growth after the first-minute baseline (1 MiB), the p95\n"
               "against --latency-p95-ms, and the process thread/handle counts against\n"
               "the baseline taken right after the consumer starts; a failed reading or\n"
               "an exceeded budget exits non-zero.\n",
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
            // The fake source rejects a rate whose period rounds to 0 ns; the
            // tool must do the same instead of dividing by a zero period when
            // it computes its own pacing (CXX-08).
            if (!ParseDouble(value, options.rate_hz) || options.rate_hz <= 0.0 ||
                cg::core_math::ToNanoseconds(1.0 / options.rate_hz) <= 0) {
                error = "--rate must be a finite positive rate whose period is at least 1 ns";
                return ParseOutcome::Error;
            }
        } else if (arg == "--minutes") {
            if (!ParseDouble(value, options.minutes) || options.minutes <= 0.0) {
                error = "--minutes must be a finite positive number";
                return ParseOutcome::Error;
            }
        } else if (arg == "--latency-p95-ms") {
            if (!ParseDouble(value, options.latency_p95_ms) || options.latency_p95_ms <= 0.0) {
                error = "--latency-p95-ms must be a finite positive number";
                return ParseOutcome::Error;
            }
        } else {
            error = "unknown option";
            return ParseOutcome::Error;
        }
    }
    return ParseOutcome::Ok;
}

/// The process's *current* resident set in bytes, or `std::nullopt` when the
/// query fails. Windows: `GetProcessMemoryInfo` working set. Linux:
/// `/proc/self/statm`'s resident pages times the page size — the live RSS, not
/// `getrusage`'s peak, so growth under an earlier transient peak is visible
/// (CXX-04). macOS: `ru_maxrss` (bytes), the only cheap portable query there.
std::optional<std::uint64_t> ResidentBytes() noexcept {
#if defined(_WIN32)
    PROCESS_MEMORY_COUNTERS counters{};
    if (::GetProcessMemoryInfo(::GetCurrentProcess(), &counters, sizeof(counters)) == 0) {
        return std::nullopt;
    }
    return static_cast<std::uint64_t>(counters.WorkingSetSize);
#elif defined(__APPLE__)
    rusage usage{};
    if (::getrusage(RUSAGE_SELF, &usage) != 0) {
        return std::nullopt;
    }
    return static_cast<std::uint64_t>(usage.ru_maxrss);
#else
    std::FILE *file = std::fopen("/proc/self/statm", "r");
    if (file == nullptr) {
        return std::nullopt;
    }
    unsigned long size_pages = 0;
    unsigned long resident_pages = 0;
    const int fields = std::fscanf(file, "%lu %lu", &size_pages, &resident_pages);
    std::fclose(file);
    if (fields != 2) {
        return std::nullopt;
    }
    const long page_size = ::sysconf(_SC_PAGESIZE);
    if (page_size <= 0) {
        return std::nullopt;
    }
    return static_cast<std::uint64_t>(resident_pages) * static_cast<std::uint64_t>(page_size);
#endif
}

double Mib(std::uint64_t bytes) noexcept { return static_cast<double>(bytes) / (1024.0 * 1024.0); }

/// Whether this platform implements the thread/handle count queries (TD-007).
#if defined(_WIN32) || defined(__linux__)
constexpr bool kProcessCountsSupported = true;
#else
constexpr bool kProcessCountsSupported = false;
#endif

/// Threads owned by this process, or `std::nullopt` on query failure.
/// Windows: a toolhelp snapshot filtered by the current process id. Linux:
/// the `Threads:` line of `/proc/self/status`. Unsupported platforms return
/// nullopt and the tool skips the gate (reported, not silently passed).
std::optional<std::uint64_t> ProcessThreadCount() noexcept {
#if defined(_WIN32)
    HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return std::nullopt;
    }
    const DWORD self = ::GetCurrentProcessId();
    std::uint64_t count = 0;
    THREADENTRY32 entry{};
    entry.dwSize = sizeof(entry);
    if (::Thread32First(snapshot, &entry) != 0) {
        do {
            if (entry.th32OwnerProcessID == self) {
                ++count;
            }
            entry.dwSize = sizeof(entry);
        } while (::Thread32Next(snapshot, &entry) != 0);
    }
    ::CloseHandle(snapshot);
    return count;
#elif defined(__linux__)
    std::FILE *file = std::fopen("/proc/self/status", "r");
    if (file == nullptr) {
        return std::nullopt;
    }
    std::optional<std::uint64_t> threads;
    char line[256];
    while (std::fgets(line, sizeof(line), file) != nullptr) {
        unsigned long long parsed = 0;
        if (std::sscanf(line, "Threads: %llu", &parsed) == 1) {
            threads = static_cast<std::uint64_t>(parsed);
            break;
        }
    }
    std::fclose(file);
    return threads;
#else
    return std::nullopt;
#endif
}

/// Handles (Windows) or open file descriptors (Linux) held by this process, or
/// `std::nullopt` on query failure.
std::optional<std::uint64_t> ProcessHandleCount() noexcept {
#if defined(_WIN32)
    DWORD count = 0;
    if (::GetProcessHandleCount(::GetCurrentProcess(), &count) == 0) {
        return std::nullopt;
    }
    return static_cast<std::uint64_t>(count);
#elif defined(__linux__)
    DIR *directory = ::opendir("/proc/self/fd");
    if (directory == nullptr) {
        return std::nullopt;
    }
    std::uint64_t count = 0;
    while (const dirent *entry = ::readdir(directory)) {
        if (std::strcmp(entry->d_name, ".") != 0 && std::strcmp(entry->d_name, "..") != 0) {
            ++count;
        }
    }
    ::closedir(directory);
    return count;
#else
    return std::nullopt;
#endif
}

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
    std::atomic<std::uint64_t> read_gaps[kLatencyBuckets]{};
};

/// Records one gap between consecutive successful reads into the fixed
/// histogram (bucket `i` covers gaps below 2^i ns). Allocation-free.
void RecordReadGap(ReadCounters &counters, HostTime gap_ns) noexcept {
    const std::uint64_t gap = gap_ns > 0 ? static_cast<std::uint64_t>(gap_ns) : 0;
    int bucket = static_cast<int>(std::bit_width(gap));
    if (bucket >= kLatencyBuckets) {
        bucket = kLatencyBuckets - 1;
    }
    counters.read_gaps[bucket].fetch_add(1, std::memory_order_relaxed);
}

/// p95 of the read-to-read gap in nanoseconds from the fixed histogram: the
/// covering bucket's upper bound. Allocation-free; called once per report.
double P95ReadGapNs(const ReadCounters &counters) noexcept {
    std::uint64_t total = 0;
    for (const std::atomic<std::uint64_t> &bucket : counters.read_gaps) {
        total += bucket.load(std::memory_order_relaxed);
    }
    if (total == 0) {
        return 0.0;
    }
    const std::uint64_t threshold = (total * 95ULL + 99ULL) / 100ULL;
    std::uint64_t cumulative = 0;
    for (int index = 0; index < kLatencyBuckets; ++index) {
        cumulative += counters.read_gaps[index].load(std::memory_order_relaxed);
        if (cumulative >= threshold) {
            return static_cast<double>(std::uint64_t{1} << index);
        }
    }
    return 0.0;
}

/// The render-path consumer: a tight TryGetLatest loop until the producer
/// finishes or the stop flag is set. Every successful read timestamps the gap
/// from the previous successful read into the fixed log2 histogram. Yields
/// every 1024 iterations so a single-core runner can schedule the producer.
void Consume(const cg::IHeadPoseSource &source, ReadCounters &counters, const std::atomic<bool> &producing,
             std::chrono::steady_clock::time_point start) noexcept {
    HeadSample sample = PlaceholderSample();
    std::uint32_t last_seq = 0;
    bool have_sample = false;
    HostTime last_read_ns = 0;
    bool have_read = false;
    std::uint64_t iterations = 0;
    while (producing.load(std::memory_order_relaxed) && g_stop_requested == 0) {
        if (source.TryGetLatest(sample, Duration{0})) {
            counters.reads.fetch_add(1, std::memory_order_relaxed);
            const HostTime read_ns = ElapsedSince(start);
            if (have_read) {
                RecordReadGap(counters, read_ns - last_read_ns);
            }
            last_read_ns = read_ns;
            have_read = true;
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
    const auto start = std::chrono::steady_clock::now();
    std::thread consumer([&source, &counters, &producing, start] { Consume(source, counters, producing, start); });

    const double total_seconds = options.minutes * 60.0;
    const HostTime period_ns = static_cast<HostTime>(std::llround(kNanosecondsPerSecond / options.rate_hz));
    // TD-007: the count baseline is taken right after the consumer starts (so
    // it includes that thread); the final reading is taken after the join.
    const std::optional<std::uint64_t> baseline_threads = kProcessCountsSupported ? ProcessThreadCount() : std::nullopt;
    const std::optional<std::uint64_t> baseline_handles = kProcessCountsSupported ? ProcessHandleCount() : std::nullopt;
    std::uint64_t published = 0;
    std::optional<std::uint64_t> baseline_rss;
    double baseline_seconds = 0.0;
    bool reported = false;
    double next_report_seconds = kReportIntervalSeconds;

    std::printf("glasses_soak: rate=%.3f Hz minutes=%.3f rss budget=%.2f MiB latency p95 budget=%.2f ms\n",
                options.rate_hz, options.minutes, Mib(kRssBudgetBytes), options.latency_p95_ms);
    std::fflush(stdout);

    for (;;) {
        const HostTime elapsed_ns = ElapsedSince(start);
        const double elapsed_seconds = ToSeconds(elapsed_ns);
        if (g_stop_requested != 0 || elapsed_seconds >= total_seconds) {
            break;
        }

        if (elapsed_seconds >= next_report_seconds) {
            const std::optional<std::uint64_t> rss = ResidentBytes();
            char rss_text[32];
            cg::soak::FormatRssBytes(rss_text, sizeof(rss_text), rss);
            std::printf("[soak] t=%.1f s published=%llu read=%llu fresh=%llu misses=%llu rss=%s\n", elapsed_seconds,
                        static_cast<unsigned long long>(published),
                        static_cast<unsigned long long>(counters.reads.load(std::memory_order_relaxed)),
                        static_cast<unsigned long long>(counters.fresh.load(std::memory_order_relaxed)),
                        static_cast<unsigned long long>(counters.misses.load(std::memory_order_relaxed)), rss_text);
            std::printf("[soak] latency t=%.1f s reads=%llu fresh=%llu p95_read_gap=%.2f us "
                        "(cumulative, bucket bound)\n",
                        elapsed_seconds,
                        static_cast<unsigned long long>(counters.reads.load(std::memory_order_relaxed)),
                        static_cast<unsigned long long>(counters.fresh.load(std::memory_order_relaxed)),
                        P95ReadGapNs(counters) / 1e3);
            std::fflush(stdout);
            reported = true;
            if (!baseline_rss.has_value()) {
                baseline_rss = rss;
                baseline_seconds = elapsed_seconds;
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
    const std::optional<std::uint64_t> final_rss = ResidentBytes();
    const std::optional<std::uint64_t> final_threads = kProcessCountsSupported ? ProcessThreadCount() : std::nullopt;
    const std::optional<std::uint64_t> final_handles = kProcessCountsSupported ? ProcessHandleCount() : std::nullopt;
    const double p95_ns = P95ReadGapNs(counters);
    // Format both readings into separate buffers once: formatting two values
    // through one shared buffer in a single printf would print the same text
    // twice (argument evaluation order is unspecified), hiding the baseline.
    char baseline_rss_text[32];
    char final_rss_text[32];
    cg::soak::FormatRssBytes(baseline_rss_text, sizeof(baseline_rss_text), baseline_rss);
    cg::soak::FormatRssBytes(final_rss_text, sizeof(final_rss_text), final_rss);
    std::printf("[soak] done t=%.1f s published=%llu read=%llu fresh=%llu misses=%llu rss=%s "
                "p95_read_gap=%.2f us\n",
                elapsed_seconds, static_cast<unsigned long long>(published),
                static_cast<unsigned long long>(counters.reads.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(counters.fresh.load(std::memory_order_relaxed)),
                static_cast<unsigned long long>(counters.misses.load(std::memory_order_relaxed)), final_rss_text,
                p95_ns / 1e3);
    if (g_stop_requested != 0) {
        std::printf("[soak] stopped by signal\n");
    }

    // TD-007 gates: latency, then the thread/handle counts. They are evaluated
    // before the RSS short-run early-out so even a smoke-length run checks
    // them. A missing count reading fails on a platform that supports the
    // query; unsupported platforms report the skip explicitly.
    bool failed = false;
    const cg::soak::LatencyGateResult latency = cg::soak::EvaluateLatencyGate(p95_ns, options.latency_p95_ms * 1e6);
    std::printf("[soak] latency gate: p95=%.2f us threshold=%.2f ms -> %s\n", latency.p95_ns / 1e3,
                latency.threshold_ns / 1e6, latency.pass ? "PASS" : "FAIL");
    failed = failed || !latency.pass;
    if (kProcessCountsSupported) {
        const cg::soak::CountGateResult threads = cg::soak::EvaluateCountGate(baseline_threads, final_threads);
        const cg::soak::CountGateResult handles = cg::soak::EvaluateCountGate(baseline_handles, final_handles);
        std::printf("[soak] thread gate: baseline=%llu final=%llu -> %s\n",
                    static_cast<unsigned long long>(threads.baseline),
                    static_cast<unsigned long long>(threads.final_count), threads.pass ? "PASS" : "FAIL");
        std::printf("[soak] handle gate: baseline=%llu final=%llu -> %s\n",
                    static_cast<unsigned long long>(handles.baseline),
                    static_cast<unsigned long long>(handles.final_count), handles.pass ? "PASS" : "FAIL");
        failed = failed || !threads.pass || !handles.pass;
    } else {
        std::printf("[soak] thread/handle gates: skipped (query unsupported on this platform)\n");
    }

    if (!reported && !baseline_rss.has_value()) {
        // The run ended before the first report: no baseline exists by design,
        // so the RSS gate cannot be evaluated. This is the documented short-run
        // behaviour, not a reading failure.
        std::printf("[soak] rss delta: no baseline (run shorter than %.0f s)\n", kReportIntervalSeconds);
        std::fflush(stdout);
        return failed ? 1 : 0;
    }

    const cg::soak::RssGateResult gate = cg::soak::EvaluateRssGate(baseline_rss, final_rss, kRssBudgetBytes);
    switch (gate.reason) {
    case cg::soak::RssGateReason::MissingBaseline:
        std::printf("[soak] FAIL: baseline RSS reading unavailable; the leak gate cannot be evaluated\n");
        failed = true;
        break;
    case cg::soak::RssGateReason::MissingFinal:
        std::printf("[soak] FAIL: final RSS reading unavailable; the leak gate cannot be evaluated\n");
        failed = true;
        break;
    case cg::soak::RssGateReason::OverBudget:
        std::printf("[soak] rss baseline (t=%.1f s)=%s final=%s delta=%.2f MiB (budget %.2f MiB)\n", baseline_seconds,
                    baseline_rss_text, final_rss_text, Mib(gate.growth_bytes), Mib(kRssBudgetBytes));
        std::printf("[soak] FAIL: RSS growth %.2f MiB exceeds the %.2f MiB budget\n", Mib(gate.growth_bytes),
                    Mib(kRssBudgetBytes));
        failed = true;
        break;
    case cg::soak::RssGateReason::WithinBudget:
        std::printf("[soak] rss baseline (t=%.1f s)=%s final=%s delta=%.2f MiB (budget %.2f MiB)\n", baseline_seconds,
                    baseline_rss_text, final_rss_text, Mib(gate.growth_bytes), Mib(kRssBudgetBytes));
        std::printf("[soak] PASS: RSS growth %.2f MiB within the %.2f MiB budget\n", Mib(gate.growth_bytes),
                    Mib(kRssBudgetBytes));
        break;
    }
    std::fflush(stdout);
    return failed ? 1 : 0;
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
