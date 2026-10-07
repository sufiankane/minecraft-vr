// cg-recorder — writes a `.cgrec` session from a stereo frame source
// (dossier 5.8; S8 Task 3). The synthetic source drives software round trips
// and CI; `--source viture` records the real device (S8 Task 7) through the
// shared loader and `VitureStereoSource`.

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <thread>

#include "cg/capture/cgrec.hpp"
#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/recorder.hpp"
#include "cg/glasses/viture_loader.hpp"
#include "cg/glasses/viture_stereo_source.hpp"

namespace {

using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::FrameStorage;
using cg::capture::Recorder;
using cg::capture::RecorderConfig;
using cg::capture::SessionInfo;
using cg::capture::ValidateSession;
using cg::glasses::VitureStereoSource;

struct Options {
    std::string source = "fake"; // fake | viture
    std::string dll;             // viture only, loader path policy applies
    std::string dof = "3dof";    // viture only, exported as CG_VITURE_DOF
    std::string out;
    double seconds = 5.0;
    int width = 640;
    int height = 480;
    int stride = 0;    // 0 = width (fake only; the device reports its own)
    double fps = 30.0; // fake only: the device paces itself
    FrameStorage storage = FrameStorage::Pgm;
};

void PrintUsage() {
    std::printf("usage: cg-recorder [--source fake|viture] [--out DIR] [--seconds S]\n"
                "                   [--width W] [--height H] [--stride N] [--fps F]\n"
                "                   [--storage pgm|bin] [--dll PATH] [--dof 3dof|6dof]\n"
                "\n"
                "Records a stereo session into `.cgrec` (manifest.json, stereo.csv, frames/).\n"
                "--source fake (default) drives the synthetic pattern at --fps; --source viture\n"
                "records the real device for --seconds through --dll (the geometry comes from the\n"
                "frames; --dof selects the tracking mode). Without --out a\n"
                "session_YYYYMMDD_HHMMSS directory is created in the current directory.\n"
                "Exit codes: 0 the session recorded and validated; 1 usage or argument error;\n"
                "2 the session could not be written or validated (or the device failed).\n");
}

[[nodiscard]] bool NextValue(int argc, char **argv, int &index, std::string_view option, std::string &value) {
    if (index + 1 >= argc) {
        std::fprintf(stderr, "cg-recorder: %.*s requires a value\n", static_cast<int>(option.size()), option.data());
        return false;
    }
    ++index;
    value = argv[index];
    return true;
}

[[nodiscard]] bool ParseDouble(std::string_view text, double &out) {
    char *end = nullptr;
    const double value = std::strtod(std::string{text}.c_str(), &end);
    if (end == nullptr || *end != '\0') {
        return false;
    }
    out = value;
    return true;
}

[[nodiscard]] bool ParseInt(std::string_view text, int &out) {
    char *end = nullptr;
    const long value = std::strtol(std::string{text}.c_str(), &end, 10);
    if (end == nullptr || *end != '\0' || value <= 0 || value > 100'000) {
        return false;
    }
    out = static_cast<int>(value);
    return true;
}

[[nodiscard]] bool ParseOptions(int argc, char **argv, Options &options, bool &help) {
    help = false;
    for (int index = 1; index < argc; ++index) {
        const std::string_view argument = argv[index];
        std::string value;
        if (argument == "--source") {
            if (!NextValue(argc, argv, index, argument, value)) {
                return false;
            }
            if (value != "fake" && value != "viture") {
                std::fprintf(stderr, "cg-recorder: --source must be 'fake' or 'viture'\n");
                return false;
            }
            options.source = value;
        } else if (argument == "--dll") {
            if (!NextValue(argc, argv, index, argument, options.dll)) {
                return false;
            }
        } else if (argument == "--dof") {
            if (!NextValue(argc, argv, index, argument, value)) {
                return false;
            }
            if (value != "3dof" && value != "6dof") {
                std::fprintf(stderr, "cg-recorder: --dof must be '3dof' or '6dof'\n");
                return false;
            }
            options.dof = value;
        } else if (argument == "--out") {
            if (!NextValue(argc, argv, index, argument, options.out)) {
                return false;
            }
        } else if (argument == "--seconds") {
            if (!NextValue(argc, argv, index, argument, value) || !ParseDouble(value, options.seconds)) {
                return false;
            }
        } else if (argument == "--width") {
            if (!NextValue(argc, argv, index, argument, value) || !ParseInt(value, options.width)) {
                return false;
            }
        } else if (argument == "--height") {
            if (!NextValue(argc, argv, index, argument, value) || !ParseInt(value, options.height)) {
                return false;
            }
        } else if (argument == "--stride") {
            if (!NextValue(argc, argv, index, argument, value) || !ParseInt(value, options.stride)) {
                return false;
            }
        } else if (argument == "--fps") {
            if (!NextValue(argc, argv, index, argument, value) || !ParseDouble(value, options.fps) ||
                options.fps <= 0.0) {
                return false;
            }
        } else if (argument == "--storage") {
            if (!NextValue(argc, argv, index, argument, value)) {
                return false;
            }
            if (value == "pgm") {
                options.storage = FrameStorage::Pgm;
            } else if (value == "bin") {
                options.storage = FrameStorage::PackedBin;
            } else {
                std::fprintf(stderr, "cg-recorder: --storage must be 'pgm' or 'bin'\n");
                return false;
            }
        } else if (argument == "-h" || argument == "--help") {
            help = true;
        } else {
            std::fprintf(stderr, "cg-recorder: unknown argument '%.*s'\n", static_cast<int>(argument.size()),
                         argument.data());
            return false;
        }
    }
    return true;
}

[[nodiscard]] std::string DefaultSessionName() {
    const std::time_t now = std::time(nullptr);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    char buffer[32] = {};
    (void)std::strftime(buffer, sizeof(buffer), "session_%Y%m%d_%H%M%S", &utc);
    return buffer;
}

/// Shared tail: print the stats and validate the written session.
[[nodiscard]] int Finish(const Options &options, Recorder &recorder) {
    const cg::capture::RecorderStats stats = recorder.Stats();
    std::printf("cg-recorder: session %s: submitted=%llu written=%llu dropped=%llu\n", options.out.c_str(),
                static_cast<unsigned long long>(stats.submitted), static_cast<unsigned long long>(stats.written),
                static_cast<unsigned long long>(stats.dropped));

    const cg::Result<SessionInfo> info = ValidateSession(options.out);
    if (!info.ok()) {
        std::fprintf(stderr, "cg-recorder: validation failed: %s\n", info.status().message());
        return 2;
    }
    std::printf("cg-recorder: validated: %llu frames, %dx%d, storage=%s, dropped=%llu\n",
                static_cast<unsigned long long>((*info).frame_count), (*info).width, (*info).height,
                (*info).storage == FrameStorage::Pgm ? "pgm" : "bin", static_cast<unsigned long long>((*info).dropped));
    return 0;
}

[[nodiscard]] int RunFake(const Options &options) {
    FakeStereoConfig geometry;
    geometry.width = options.width;
    geometry.height = options.height;
    geometry.stride = options.stride;
    FakeStereoSource source(geometry);

    RecorderConfig config;
    config.session_dir = options.out;
    config.storage = options.storage;
    Recorder recorder(config);
    const cg::Result<void> opened = recorder.Open();
    if (!opened.ok()) {
        std::fprintf(stderr, "cg-recorder: %s\n", opened.status().message());
        return 2;
    }
    const cg::Result<void> started = source.Start(&recorder);
    if (!started.ok()) {
        std::fprintf(stderr, "cg-recorder: %s\n", started.status().message());
        return 2;
    }

    const auto period = std::chrono::duration<double>(1.0 / options.fps);
    const auto total = static_cast<std::uint64_t>(options.seconds * options.fps);
    auto deadline = std::chrono::steady_clock::now();
    for (std::uint64_t frame = 0; frame < total; ++frame) {
        source.EmitFrame();
        deadline += std::chrono::duration_cast<std::chrono::steady_clock::duration>(period);
        std::this_thread::sleep_until(deadline);
    }
    source.Stop();
    recorder.Close();
    return Finish(options, recorder);
}

[[nodiscard]] int RunViture(const Options &options) {
    // Export the DOF selection before the loader creates the device.
#if defined(_WIN32)
    (void)_putenv_s("CG_VITURE_DOF", options.dof.c_str());
#else
    (void)setenv("CG_VITURE_DOF", options.dof.c_str(), 1);
#endif

    cg::Result<std::unique_ptr<cg::glasses::IVitureApi>> loaded = cg::glasses::LoadVitureApi(options.dll);
    if (!loaded.ok()) {
        std::fprintf(stderr, "cg-recorder: %s\n", loaded.status().message());
        return 2;
    }
    cg::glasses::IVitureApi &api = **loaded;
    const cg::Result<void> created = api.CreateDevice();
    if (!created.ok()) {
        std::fprintf(stderr, "cg-recorder: %s\n", created.status().message());
        return 2;
    }
    const cg::Result<void> started_pose = api.StartPose();
    if (!started_pose.ok()) {
        api.DestroyDevice();
        std::fprintf(stderr, "cg-recorder: %s\n", started_pose.status().message());
        return 2;
    }

    RecorderConfig config;
    config.session_dir = options.out;
    config.storage = options.storage;
    Recorder recorder(config);
    const cg::Result<void> opened = recorder.Open();
    if (!opened.ok()) {
        api.RequestStop();
        api.DestroyDevice();
        std::fprintf(stderr, "cg-recorder: %s\n", opened.status().message());
        return 2;
    }

    VitureStereoSource source(api);
    const cg::Result<void> attached = source.Start(&recorder);
    if (!attached.ok()) {
        api.RequestStop();
        api.DestroyDevice();
        std::fprintf(stderr, "cg-recorder: %s\n", attached.status().message());
        return 2;
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::duration<double>(options.seconds);
    while (std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    source.Stop();
    api.RequestStop();
    api.DestroyDevice();
    recorder.Close();
    return Finish(options, recorder);
}

} // namespace

int main(int argc, char **argv) {
    Options options;
    bool help = false;
    if (!ParseOptions(argc, argv, options, help)) {
        PrintUsage();
        return 1;
    }
    if (help) {
        PrintUsage();
        return 0;
    }
    if (options.out.empty()) {
        options.out = DefaultSessionName();
    }
    if (options.source == "viture") {
        return RunViture(options);
    }
    return RunFake(options);
}
