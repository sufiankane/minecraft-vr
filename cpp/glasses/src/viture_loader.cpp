// The only translation unit that knows a VITURE SDK symbol or opens the
// vendor library. No vendor header is included anywhere in the repository
// (ADR-0001, ADR-0009): the SDK surface arrives through the function table
// below, resolved at load time.
//
// This table binds the real VITURE Windows SDK (`viture sdk/include/*.h` and
// `x86_64/glasses.dll`, exports observed 2026-10-05). The pre-HIL placeholder
// spellings that never existed (`xr_device_provider_start_pose`,
// `..._get_display_refresh_rate`, `..._get_sdk_version`) are gone; create,
// poll and reset now use the vendor's true signatures, including the product
// id `create` requires and the out-parameter poll.
//
// Calling convention: the SDK header marks every entry point
// `__declspec(dllexport)` with no explicit convention, i.e. the platform
// default (`__cdecl`; on x64 there is only the unified Microsoft convention).
//
// What the HIL run still has to answer (ADR-0009 "U-01"/"U-08"):
// - U-01: does 3DoF Carina polling work on Windows, at what rate and latency,
//   and what does `pose_status` mean in practice (the header documents only
//   `0 = stable`, `1 = unstable`)? The rate is measurable from the poll
//   cadence; the latency estimate needs an SDK timestamp, and the poll call
//   returns none — the stamped reference is the Carina pose callback
//   (registered below), whose `double timestamp` is the SDK monotonic clock.
//   `PollPose` therefore stamps each polled pose with the newest callback
//   stamp (falling back to a host steady clock only until the first callback
//   arrives, which the HIL report must note). If the callback is too slow for
//   a usable latency bound, the fallback decision is recorded from the run.
// - U-08: live display-mode switching. The mode constants and the
//   set/get/switch calls come from `viture_protocol_public.h`; the run has to
//   confirm the encoding, the SBS signalling and the refresh behaviour.
//
// Security: the DLL path policy, the optional SHA-256 pin (TD-051), the
// dependency search constraints and the vendor-handle lifetime rules are
// unchanged from the pre-HIL binding.

#include "cg/glasses/viture_loader.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <charconv>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <system_error>
#include <unordered_map>
#include <utility>

#include "cg/glasses/file_hash.hpp"
#include "result.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <setupapi.h>

#pragma comment(lib, "setupapi.lib")
#else
#include <dlfcn.h>
#endif

namespace cg::glasses {

namespace {

#ifdef _WIN32

using LibraryHandle = HMODULE;

std::string WideToUtf8(const wchar_t *text, int length) {
    if (text == nullptr || length <= 0) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        return {};
    }
    std::string utf8(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, length, utf8.data(), size, nullptr, nullptr);
    return utf8;
}

std::wstring Utf8ToWide(const std::string &text) {
    if (text.empty()) {
        return {};
    }
    const int size =
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (size <= 0) {
        return {};
    }
    std::wstring wide(static_cast<std::size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), wide.data(), size);
    return wide;
}

[[nodiscard]] std::string LastLibraryError() {
    const DWORD error = GetLastError();
    LPWSTR buffer = nullptr;
    // FormatMessageW's ALLOCATE_BUFFER form takes a pointer to the output
    // pointer; the cast is the documented Win32 calling convention, not a
    // type pun on the data.
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0,
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
        reinterpret_cast<LPWSTR>(&buffer), 0, nullptr);
    std::string message;
    if (length != 0 && buffer != nullptr) {
        message = WideToUtf8(buffer, static_cast<int>(length));
        LocalFree(buffer);
        while (!message.empty() && (message.back() == '\r' || message.back() == '\n' || message.back() == ' ')) {
            message.pop_back();
        }
    }
    if (message.empty()) {
        message = "LoadLibraryW failed with error " + std::to_string(error);
    }
    return message;
}

/// Opens `path`; on failure fills `error` with the reason for *this* call
/// (CXX-11). A path that never reaches `LoadLibraryExW` (invalid UTF-8) gets
/// its own message instead of a stale `GetLastError` value.
[[nodiscard]] LibraryHandle OpenLibrary(const std::string &path, std::string &error) {
    const std::wstring wide = Utf8ToWide(path);
    if (wide.empty()) {
        error = "the DLL path is not valid UTF-8";
        return nullptr;
    }
    // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR needs an absolute path and keeps the
    // DLL's own directory available for its dependencies; DEFAULT_DIRS covers
    // the application and user directories. Unlike the legacy search, neither
    // flag consults the working directory or PATH, so a planted dependency
    // next to the process cannot hijack the imports.
    HMODULE handle =
        LoadLibraryExW(wide.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (handle == nullptr) {
        error = LastLibraryError();
    }
    return handle;
}

void CloseLibrary(LibraryHandle handle) noexcept {
    if (handle != nullptr) {
        FreeLibrary(handle);
    }
}

#else

using LibraryHandle = void *;

/// Opens `path`; on failure fills `error` with the reason for *this* call
/// (CXX-11). `dlerror()` is cleared first so a successful call cannot leave a
/// stale message behind, and the failure message is captured immediately.
[[nodiscard]] LibraryHandle OpenLibrary(const std::string &path, std::string &error) noexcept {
    (void)dlerror();
    LibraryHandle handle = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (handle == nullptr) {
        const char *message = dlerror();
        error = message == nullptr ? "dlopen failed" : message;
    }
    return handle;
}

void CloseLibrary(LibraryHandle handle) noexcept {
    if (handle != nullptr) {
        dlclose(handle);
    }
}

#endif

/// Reads environment variable `name`, or an empty string when unset.
/// Windows uses the two-call `GetEnvironmentVariableW` (no allocation and no
/// MSVC deprecation warning); POSIX uses `std::getenv`.
[[nodiscard]] std::string ReadEnvironment(const char *name) {
#ifdef _WIN32
    const std::wstring wide_name = Utf8ToWide(name);
    if (wide_name.empty()) {
        return {};
    }
    const DWORD needed = ::GetEnvironmentVariableW(wide_name.c_str(), nullptr, 0);
    if (needed == 0) {
        return {}; // unset, or an empty value: both mean "no pin"
    }
    std::wstring wide_value(static_cast<std::size_t>(needed), L'\0');
    const DWORD written = ::GetEnvironmentVariableW(wide_name.c_str(), wide_value.data(), needed);
    if (written == 0 || written >= needed) {
        return {};
    }
    wide_value.resize(static_cast<std::size_t>(written));
    return WideToUtf8(wide_value.c_str(), static_cast<int>(wide_value.size()));
#else
    const char *value = std::getenv(name);
    return value == nullptr ? std::string{} : std::string{value};
#endif
}

/// `CG_VITURE_DEBUG=1` enables verbose seam tracing on stderr (HIL only):
/// the trace pinpoints whether a vendor call blocks, which a silent hang
/// cannot.
[[nodiscard]] bool DebugTracesEnabled() {
    static const bool enabled = ReadEnvironment("CG_VITURE_DEBUG") == "1";
    return enabled;
}

/// Outcome of validating a caller-supplied vendor-DLL path.
///
/// A path is accepted only when it is absolute and either outside the working
/// directory or a sibling of the running executable: the working directory is
/// attacker-influenced, so a library there is a planted DLL. The optional
/// `CG_VITURE_DLL_SHA256` pin in `LoadVitureApi` additionally checks the file
/// content; signature verification remains open (TD-051).
[[nodiscard]] bool SamePath(const std::filesystem::path &left, const std::filesystem::path &right) {
#ifdef _WIN32
    const std::wstring &left_text = left.native();
    const std::wstring &right_text = right.native();
    return _wcsicmp(left_text.c_str(), right_text.c_str()) == 0;
#else
    return left == right;
#endif
}

/// Whether `candidate` is `directory` itself or lives below it (lexically).
[[nodiscard]] bool IsWithin(const std::filesystem::path &candidate, const std::filesystem::path &directory) {
    if (directory.empty()) {
        return false;
    }
    if (SamePath(candidate, directory)) {
        return true;
    }
    const std::filesystem::path relative = candidate.lexically_relative(directory);
    if (relative.empty()) {
        return false;
    }
    for (const std::filesystem::path &part : relative) {
        if (part == "..") {
            return false;
        }
    }
    return true;
}

/// The directory holding the running executable; empty when it cannot be read.
[[nodiscard]] std::filesystem::path ExecutableDirectory() {
#ifdef _WIN32
    std::wstring buffer(MAX_PATH, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0) {
        return {};
    }
    buffer.resize(length);
    return std::filesystem::path(buffer).parent_path();
#else
    std::error_code error;
    const std::filesystem::path executable = std::filesystem::read_symlink("/proc/self/exe", error);
    if (error) {
        return {};
    }
    return executable.parent_path();
#endif
}

/// Maximum number of distinct messages interned over the process lifetime.
constexpr std::size_t kMaxInternedMessages = 64;

/// Returns a process-lifetime copy of `message`. `Status` stores a non-owning
/// `const char*`, and loader messages are built at runtime, so they are
/// interned here. Identical messages share one pointer; at most
/// `kMaxInternedMessages` distinct messages are kept and every further one
/// gets the static fallback, so a pathological caller cannot grow the pool
/// without bound. The error path is cold, so interned strings are never
/// released; `std::deque` keeps every `c_str()` stable across later pushes.
[[nodiscard]] const char *InternMessage(std::string message) {
    static const char *const kOverflowMessage = "viture_loader: too many distinct error messages";
    static std::mutex mutex;
    static std::deque<std::string> pool;
    static std::unordered_map<std::string, const char *> interned;

    const std::lock_guard<std::mutex> lock(mutex);
    const auto existing = interned.find(message);
    if (existing != interned.end()) {
        return existing->second;
    }
    if (interned.size() >= kMaxInternedMessages) {
        return kOverflowMessage;
    }
    pool.push_back(std::move(message));
    const char *stable = pool.back().c_str();
    interned.emplace(pool.back(), stable);
    return stable;
}

// --- Vendor binding (real VITURE Windows SDK, exports observed 2026-10-05) -

/// Opaque `XRDeviceProviderHandle` from `viture_glasses_provider.h`.
using XrDeviceProviderHandle = void *;

/// `xr_device_provider_create(int product_id)` -> handle or null.
using CreateDeviceFn = XrDeviceProviderHandle (*)(int product_id);
/// `xr_device_provider_initialize(handle, custom_config, cache_file_dir)`.
using InitializeFn = int (*)(XrDeviceProviderHandle handle, const char *custom_config, const char *cache_file_dir);
/// `xr_device_provider_start(handle)`.
using StartFn = int (*)(XrDeviceProviderHandle handle);
/// `xr_device_provider_stop(handle)`.
using StopFn = int (*)(XrDeviceProviderHandle handle);
/// `xr_device_provider_shutdown(handle)`.
using ShutdownFn = int (*)(XrDeviceProviderHandle handle);
/// `xr_device_provider_destroy(handle)`.
using DestroyDeviceFn = void (*)(XrDeviceProviderHandle handle);
/// `xr_device_provider_set_dof_type_carina(handle, is_6dof)`; 0 selects 3DoF.
using SetDofTypeCarinaFn = int (*)(XrDeviceProviderHandle handle, int is_6dof);
/// `XRPoseCallback(float* pose, double timestamp)`, layout
/// `[px, py, pz, qw, qx, qy, qz]`, timestamp in SDK monotonic seconds.
using XrPoseCallbackFn = void (*)(float *pose, double timestamp);
/// `XRVSyncCallback(double timestamp)`.
using XrVSyncCallbackFn = void (*)(double timestamp);
/// `XRImuCallback(float* imu, double timestamp)`.
using XrImuCallbackFn = void (*)(float *imu, double timestamp);
/// `XRCameraCallback(...)`.
using XrCameraCallbackFn =
    void (*)(char *left0, char *right0, char *left1, char *right1, double timestamp, int width, int height);
/// `xr_device_provider_register_callbacks_carina(handle, pose, vsync, imu, camera)`.
using RegisterCallbacksCarinaFn = int (*)(XrDeviceProviderHandle handle, XrPoseCallbackFn pose_callback,
                                          XrVSyncCallbackFn vsync_callback, XrImuCallbackFn imu_callback,
                                          XrCameraCallbackFn camera_callback);
/// `xr_device_provider_get_gl_pose_carina(handle, pose, predict_time, pose_status)`.
using GetGlPoseCarinaFn = int (*)(XrDeviceProviderHandle handle, float *pose, double predict_time, int *pose_status);
/// `xr_device_provider_reset_origin_carina(handle, float pose[7])`.
using ResetOriginCarinaFn = int (*)(XrDeviceProviderHandle handle, float *pose);
/// `xr_device_provider_set_display_mode(handle, display_mode)`.
using SetDisplayModeFn = int (*)(XrDeviceProviderHandle handle, int display_mode);
/// `xr_device_provider_get_display_mode(handle)` -> mode value or negative error.
using GetDisplayModeFn = int (*)(XrDeviceProviderHandle handle);
/// `xr_device_provider_get_glasses_version(handle, response, length)`.
using GetGlassesVersionFn = int (*)(XrDeviceProviderHandle handle, char *response, int *length);
/// `GlassStateCallback(int glass_state_id, int glass_value)`.
using GlassStateCallbackFn = void (*)(int glass_state_id, int glass_value);
/// `xr_device_provider_register_state_callback(handle, callback)`.
using RegisterStateCallbackFn = int (*)(XrDeviceProviderHandle handle, GlassStateCallbackFn callback);
/// `xr_device_provider_is_product_id_valid(product_id)` -> 1/0.
using IsProductIdValidFn = int (*)(int product_id);
/// `xr_device_provider_set_log_level(level)` (global).
using SetLogLevelFn = void (*)(int level);

/// The resolved vendor entry points. Every pointer is required: a library
/// missing one is refused by name (the pre-HIL behaviour kept as the HIL
/// evidence path).
struct VitureApiFns {
    CreateDeviceFn create = nullptr;
    InitializeFn initialize = nullptr;
    StartFn start = nullptr;
    StopFn stop = nullptr;
    ShutdownFn shutdown = nullptr;
    DestroyDeviceFn destroy = nullptr;
    SetDofTypeCarinaFn set_dof_type_carina = nullptr;
    RegisterCallbacksCarinaFn register_callbacks_carina = nullptr;
    GetGlPoseCarinaFn get_gl_pose_carina = nullptr;
    ResetOriginCarinaFn reset_origin_carina = nullptr;
    SetDisplayModeFn set_display_mode = nullptr;
    GetDisplayModeFn get_display_mode = nullptr;
    GetGlassesVersionFn get_glasses_version = nullptr;
    RegisterStateCallbackFn register_state_callback = nullptr;
    IsProductIdValidFn is_product_id_valid = nullptr;
    SetLogLevelFn set_log_level = nullptr;
};

constexpr const char *kCreateDeviceSymbol = "xr_device_provider_create";
constexpr const char *kInitializeSymbol = "xr_device_provider_initialize";
constexpr const char *kStartSymbol = "xr_device_provider_start";
constexpr const char *kStopSymbol = "xr_device_provider_stop";
constexpr const char *kShutdownSymbol = "xr_device_provider_shutdown";
constexpr const char *kDestroyDeviceSymbol = "xr_device_provider_destroy";
constexpr const char *kSetDofTypeCarinaSymbol = "xr_device_provider_set_dof_type_carina";
constexpr const char *kRegisterCallbacksCarinaSymbol = "xr_device_provider_register_callbacks_carina";
constexpr const char *kGetGlPoseCarinaSymbol = "xr_device_provider_get_gl_pose_carina";
constexpr const char *kResetOriginCarinaSymbol = "xr_device_provider_reset_origin_carina";
constexpr const char *kSetDisplayModeSymbol = "xr_device_provider_set_display_mode";
constexpr const char *kGetDisplayModeSymbol = "xr_device_provider_get_display_mode";
constexpr const char *kGetGlassesVersionSymbol = "xr_device_provider_get_glasses_version";
constexpr const char *kRegisterStateCallbackSymbol = "xr_device_provider_register_state_callback";
constexpr const char *kIsProductIdValidSymbol = "xr_device_provider_is_product_id_valid";
constexpr const char *kSetLogLevelSymbol = "xr_device_provider_set_log_level";

/// Length of a lower-case SHA-256 digest in hex characters (TD-051 pin).
constexpr std::size_t kSha256HexLength = 64;

/// Resolves `name` into the function pointer `out`. A missing symbol is not an
/// error here; the caller names it in the `Unsupported` status.
///
/// The symbol is copied through `std::memcpy` instead of cast between function
/// pointer types: the standard does not guarantee such casts, and MSVC warns
/// about them (`C4191`, an error under `/WX`).
template <typename Function>
[[nodiscard]] bool ResolveSymbol(LibraryHandle handle, const char *name, Function &out) noexcept {
#ifdef _WIN32
    const FARPROC symbol = GetProcAddress(handle, name);
#else
    void *const symbol = dlsym(handle, name);
#endif
    if (symbol == nullptr) {
        return false;
    }
    // Copying the loader's data pointer into the function-pointer slot is the
    // documented portable workaround for the missing standard conversion
    // (casting between function pointer types is conditionally supported and
    // MSVC warns with C4191, an error under /WX). The sizes are static_asserted
    // here on every supported ABI.
    // NOLINTBEGIN(bugprone-bitwise-pointer-cast, bugprone-multi-level-implicit-pointer-conversion)
    static_assert(sizeof(Function) == sizeof(symbol), "a function pointer must fit in a loader symbol");
    std::memcpy(&out, &symbol, sizeof(Function));
    // NOLINTEND(bugprone-bitwise-pointer-cast, bugprone-multi-level-implicit-pointer-conversion)
    return true;
}

[[nodiscard]] bool ResolveVitureSymbols(LibraryHandle handle, VitureApiFns &fns, const char *&missing) noexcept {
    if (!ResolveSymbol(handle, kCreateDeviceSymbol, fns.create)) {
        missing = kCreateDeviceSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kInitializeSymbol, fns.initialize)) {
        missing = kInitializeSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kStartSymbol, fns.start)) {
        missing = kStartSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kStopSymbol, fns.stop)) {
        missing = kStopSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kShutdownSymbol, fns.shutdown)) {
        missing = kShutdownSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kDestroyDeviceSymbol, fns.destroy)) {
        missing = kDestroyDeviceSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kSetDofTypeCarinaSymbol, fns.set_dof_type_carina)) {
        missing = kSetDofTypeCarinaSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kRegisterCallbacksCarinaSymbol, fns.register_callbacks_carina)) {
        missing = kRegisterCallbacksCarinaSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kGetGlPoseCarinaSymbol, fns.get_gl_pose_carina)) {
        missing = kGetGlPoseCarinaSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kResetOriginCarinaSymbol, fns.reset_origin_carina)) {
        missing = kResetOriginCarinaSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kSetDisplayModeSymbol, fns.set_display_mode)) {
        missing = kSetDisplayModeSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kGetDisplayModeSymbol, fns.get_display_mode)) {
        missing = kGetDisplayModeSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kGetGlassesVersionSymbol, fns.get_glasses_version)) {
        missing = kGetGlassesVersionSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kRegisterStateCallbackSymbol, fns.register_state_callback)) {
        missing = kRegisterStateCallbackSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kIsProductIdValidSymbol, fns.is_product_id_valid)) {
        missing = kIsProductIdValidSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kSetLogLevelSymbol, fns.set_log_level)) {
        missing = kSetLogLevelSymbol;
        return false;
    }
    return true;
}

/// Maps a VITURE_GLASSES_ERROR_* code (`viture_result.h`) onto the status
/// vocabulary. The vendor codes are negative; `0` is success and is never
/// passed here.
[[nodiscard]] StatusCode VendorStatusCode(int code) noexcept {
    switch (code) {
    case -1: // VITURE_GLASSES_ERROR_INVALID_PARAM
        return StatusCode::InvalidArgument;
    case -2: // VITURE_GLASSES_ERROR_USB_UNAVAILABLE
    case -3: // VITURE_GLASSES_ERROR_USB_EXEC
        return StatusCode::Device;
    case -4: // VITURE_GLASSES_ERROR_NOT_SUPPORTED
        return StatusCode::Unsupported;
    case -5: // VITURE_GLASSES_ERROR_NO_DATA
        return StatusCode::Timeout;
    case -6: // VITURE_GLASSES_ERROR_DATA_PARSE
    case -7: // VITURE_GLASSES_ERROR_DEVICE_REJECTED
    case -8: // VITURE_GLASSES_ERROR_CALIB_INIT
    case -9: // VITURE_GLASSES_ERROR_SERIAL_FETCH
        return StatusCode::Device;
    case -10: // VITURE_GLASSES_ERROR_INVALID_STATE
        return StatusCode::NotReady;
    default: // VITURE_GLASSES_ERROR_UNKNOWN and out-of-range values
        return StatusCode::Internal;
    }
}

/// A failure status naming the vendor call and its raw code; the message is
/// interned because `Status` stores a non-owning pointer.
[[nodiscard]] Status VendorFailure(std::string_view operation, int code) {
    return Status{VendorStatusCode(code), InternMessage("viture_loader: " + std::string(operation) +
                                                        " failed with VITURE code " + std::to_string(code))};
}

/// Parses `CG_VITURE_PRODUCT_ID`: decimal, or `0x`-prefixed hexadecimal.
[[nodiscard]] std::optional<int> ParseProductId(std::string_view text) noexcept {
    if (text.empty()) {
        return std::nullopt;
    }
    int base = 10;
    std::size_t start = 0;
    if (text.size() > 2U && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) {
        base = 16;
        start = 2;
    }
    unsigned int value = 0;
    const char *first = text.data() + start;
    const char *last = text.data() + text.size();
    const std::from_chars_result result = std::from_chars(first, last, value, base);
    if (result.ec != std::errc{} || result.ptr != last || value > 0x7FFFFFFFU) {
        return std::nullopt;
    }
    return static_cast<int>(value);
}

#ifdef _WIN32

/// Finds the first present USB device with `VID_35CA` whose product id the
/// SDK accepts, mirroring the vendor demo's SetupAPI fallback (the Carina is a
/// bulk-transfer device, so the HID enumeration path does not see it).
/// Returns `std::nullopt` when the glasses are not attached.
[[nodiscard]] std::optional<int> EnumerateVitureProductId(const VitureApiFns &fns) noexcept {
    HDEVINFO info = SetupDiGetClassDevsA(nullptr, "USB", nullptr, DIGCF_PRESENT | DIGCF_ALLCLASSES);
    if (info == INVALID_HANDLE_VALUE) {
        return std::nullopt;
    }

    std::optional<int> found;
    SP_DEVINFO_DATA device{};
    device.cbSize = sizeof(device);
    for (DWORD index = 0; SetupDiEnumDeviceInfo(info, index, &device); ++index) {
        std::array<char, 4096> buffer{};
        DWORD length = 0;
        if (SetupDiGetDeviceRegistryPropertyA(info, &device, SPDRP_HARDWAREID, nullptr,
                                              reinterpret_cast<PBYTE>(buffer.data()),
                                              static_cast<DWORD>(buffer.size()), &length) == FALSE) {
            continue;
        }
        // Hardware IDs are a REG_MULTI_SZ list of upper-case strings such as
        // "USB\VID_35CA&PID_0201"; walk the list and test each id string.
        const std::string_view ids{buffer.data(), std::min<std::size_t>(length, buffer.size())};
        std::size_t offset = 0;
        while (offset < ids.size() && ids[offset] != '\0') {
            const std::string_view id = ids.substr(offset);
            offset += id.size() + 1U;
            const std::size_t vendor = id.find("VID_35CA");
            if (vendor == std::string_view::npos) {
                continue;
            }
            const std::size_t product = id.find("PID_", vendor);
            if (product == std::string_view::npos || product + 8U > id.size()) {
                continue;
            }
            unsigned int product_id = 0;
            const char *first = id.data() + product + 4U;
            const std::from_chars_result result = std::from_chars(first, first + 4U, product_id, 16);
            if (result.ec != std::errc{}) {
                continue;
            }
            if (fns.is_product_id_valid(static_cast<int>(product_id)) != 0) {
                found = static_cast<int>(product_id);
                break;
            }
        }
        if (found.has_value()) {
            break;
        }
    }
    SetupDiDestroyDeviceInfoList(info);
    return found;
}

#else

/// POSIX builds exist for CI type-checking and development (no vendor runtime
/// is shipped for them); product-id enumeration is Windows-only.
[[nodiscard]] std::optional<int> EnumerateVitureProductId(const VitureApiFns & /*fns*/) noexcept { return std::nullopt; }

#endif

/// Resolves the product id `xr_device_provider_create` needs: the explicit
/// `CG_VITURE_PRODUCT_ID` override first (validated against the SDK), then a
/// USB scan. A missing device is a `Device` failure naming VID 0x35CA.
[[nodiscard]] Result<int> ResolveProductId(const VitureApiFns &fns) {
    const std::string override_text = ReadEnvironment("CG_VITURE_PRODUCT_ID");
    if (!override_text.empty()) {
        const std::optional<int> parsed = ParseProductId(override_text);
        if (!parsed.has_value()) {
            return Err<int>(Status{StatusCode::InvalidArgument,
                                   InternMessage("viture_loader: CG_VITURE_PRODUCT_ID='" + override_text +
                                                 "' is not a decimal or 0x-hex product id")});
        }
        if (fns.is_product_id_valid(*parsed) == 0) {
            return Err<int>(Status{StatusCode::InvalidArgument,
                                   InternMessage("viture_loader: CG_VITURE_PRODUCT_ID=" + override_text +
                                                 " is not a product id this SDK accepts")});
        }
        return Ok(*parsed);
    }

    const std::optional<int> enumerated = EnumerateVitureProductId(fns);
    if (!enumerated.has_value()) {
        return Err<int>(Status{StatusCode::Device,
                               "viture_loader: no VITURE device found on USB (VID 0x35CA); connect the glasses, "
                               "install the vendor USB driver, or set CG_VITURE_PRODUCT_ID"});
    }
    return Ok(*enumerated);
}

/// `VITURE_DISPLAY_MODE_*` for the `{refresh_hz, sbs}` pair (F-09: SBS modes
/// are 3840x1080 at 60 or 90 Hz; 120 Hz is 2D only). Returns `std::nullopt`
/// for an unsupported pair.
[[nodiscard]] std::optional<int> DisplayModeFor(std::uint32_t refresh_hz, bool sbs) noexcept {
    if (!sbs) {
        switch (refresh_hz) {
        case 60:
            return 0x31; // VITURE_DISPLAY_MODE_1920_1080_60HZ
        case 90:
            return 0x33; // VITURE_DISPLAY_MODE_1920_1080_90HZ
        case 120:
            return 0x34; // VITURE_DISPLAY_MODE_1920_1080_120HZ
        default:
            return std::nullopt;
        }
    }
    switch (refresh_hz) {
    case 60:
        return 0x32; // VITURE_DISPLAY_MODE_3840_1080_60HZ (3D mode)
    case 90:
        return 0x35; // VITURE_DISPLAY_MODE_3840_1080_90HZ (3D mode)
    default:
        return std::nullopt;
    }
}

/// The refresh rate a `VITURE_DISPLAY_MODE_*` value encodes, for every mode
/// constant in `viture_protocol_public.h` (both the 1080p and the 1200p
/// families). Unknown values return `std::nullopt`.
[[nodiscard]] std::optional<std::uint32_t> RefreshHzForMode(int mode) noexcept {
    switch (mode) {
    case 0x31: // 1920x1080@60
    case 0x32: // 3840x1080@60 (3D)
    case 0x41: // 1920x1200@60
    case 0x42: // 3840x1200@60 (3D)
        return 60U;
    case 0x33: // 1920x1080@90
    case 0x35: // 3840x1080@90 (3D)
    case 0x43: // 1920x1200@90
    case 0x45: // 3840x1200@90 (3D)
        return 90U;
    case 0x34: // 1920x1080@120
    case 0x44: // 1920x1200@120
        return 120U;
    default:
        return std::nullopt;
    }
}

/// Monotonic host seconds, used only as the SDK-stamp fallback until the first
/// pose callback arrives (see the file header).
[[nodiscard]] double SteadySeconds() noexcept {
#ifdef _WIN32
    LARGE_INTEGER counter{};
    LARGE_INTEGER frequency{};
    if (::QueryPerformanceCounter(&counter) == 0 || ::QueryPerformanceFrequency(&frequency) == 0 ||
        frequency.QuadPart == 0) {
        return 0.0;
    }
    return static_cast<double>(counter.QuadPart) / static_cast<double>(frequency.QuadPart);
#else
    timespec now{};
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) {
        return 0.0;
    }
    return static_cast<double>(now.tv_sec) + static_cast<double>(now.tv_nsec) / 1e9;
#endif
}

/// The process-wide target of the vendor's context-less pose callback. Only
/// one `VendorVitureApi` is live at a time; the newest created instance owns
/// the callback and `ReleaseCallback` clears it.
class VendorVitureApi;
std::atomic<VendorVitureApi *> g_pose_callback_target{nullptr};
void ViturePoseCallback(float *pose, double timestamp);

/// No-op state callback: the SDK expects one to be registered, the adapter
/// does not consume device state events. Under `CG_VITURE_DEBUG=1` every
/// event is traced (HIL diagnosis: wear/proximity, brightness, volume, film).
void VitureStateCallback(int glass_state_id, int glass_value) {
    if (DebugTracesEnabled()) {
        std::fprintf(stderr, "viture-state: id=%d value=%d\n", glass_state_id, glass_value);
    }
}

/// No-op stereo camera callback: the Carina VIO engine captures the camera
/// callback pointer at start time (vendor demo note), so the S5 adapter
/// registers one even though it does not consume camera frames.
void VitureCameraCallback(char * /*left0*/, char * /*right0*/, char * /*left1*/, char * /*right1*/,
                          double /*timestamp*/, int /*width*/, int /*height*/) {}

/// The vendor-backed API. It owns the library handle and the device handle and
/// keeps the resolved pointers valid exactly as long as the API object.
///
/// The pose callback is the SDK's only true timestamped pose source, so its
/// `double timestamp` is the SDK monotonic clock reference for `PollPose`
/// (U-01 latency analysis). The callback arrives on an SDK thread; the stamp
/// transfer to the polling thread is a lock-free atomic pair, the same
/// discipline as `VitureHeadPoseSource::LastSdkSeconds`. Only one instance may
/// be live per process (the vendor callback carries no context pointer); the
/// newest created instance owns the callback, and `DestroyDevice` releases it.
class VendorVitureApi final : public IVitureApi {
  public:
    explicit VendorVitureApi(VitureApiFns fns) noexcept : fns_(fns) {}

    ~VendorVitureApi() override { DestroyDevice(); }

    VendorVitureApi(const VendorVitureApi &) = delete;
    VendorVitureApi &operator=(const VendorVitureApi &) = delete;
    VendorVitureApi(VendorVitureApi &&) = delete;
    VendorVitureApi &operator=(VendorVitureApi &&) = delete;

    Result<void> CreateDevice() override {
        if (handle_ != nullptr) {
            return Err<void>(Status{StatusCode::Device, "viture: a device is already created"});
        }
        const Result<int> product_id = ResolveProductId(fns_);
        if (!product_id.ok()) {
            return Err<void>(product_id.status());
        }
        const bool trace = DebugTracesEnabled();

        handle_ = fns_.create(*product_id);
        if (trace) {
            std::fprintf(stderr, "viture-device: create(pid=%d) -> %p\n", *product_id, handle_);
        }
        if (handle_ == nullptr) {
            return Err<void>(Status{StatusCode::Device,
                                    InternMessage("viture: xr_device_provider_create(" + std::to_string(*product_id) +
                                                  ") returned null; is the glasses connected and the vendor USB "
                                                  "driver installed?")});
        }

        // F-03: the S5 adapter runs the Carina in 3DoF; the call must precede
        // initialize (viture_device_carina.h). HIL instrumentation (U-01):
        // CG_VITURE_DOF=6dof selects 6DoF so the fallback (6DoF with the
        // position discarded) can be measured; anything else means 3DoF.
        const int is_6dof = ReadEnvironment("CG_VITURE_DOF") == "6dof" ? 1 : 0;
        int code = fns_.set_dof_type_carina(handle_, is_6dof);
        if (trace) {
            std::fprintf(stderr, "viture-device: set_dof_type_carina(%s) rc=%d\n", is_6dof != 0 ? "6dof" : "3dof", code);
        }
        if (code != 0) {
            const Status status = VendorFailure("xr_device_provider_set_dof_type_carina(3DoF)", code);
            DestroyHandle();
            return Err<void>(status);
        }

        // The vendor demo passes null for the config and cache-directory
        // strings; the SDK owns any caching it needs.
        code = fns_.initialize(handle_, nullptr, nullptr);
        if (trace) {
            std::fprintf(stderr, "viture-device: initialize rc=%d\n", code);
        }
        if (code != 0) {
            const Status status = VendorFailure("xr_device_provider_initialize", code);
            DestroyHandle();
            return Err<void>(status);
        }

        // The library expects a state callback before start (the vendor demo
        // always registers one; without it the USB thread logs
        // "m_StateCallback is not initialized" on the first state event). The
        // S5 adapter does not consume brightness/volume/film events, so the
        // callback is a no-op.
        code = fns_.register_state_callback(handle_, &VitureStateCallback);
        if (trace) {
            std::fprintf(stderr, "viture-device: register_state_callback rc=%d\n", code);
        }
        if (code != 0) {
            const Status status = VendorFailure("xr_device_provider_register_state_callback", code);
            DestroyHandle();
            return Err<void>(status);
        }

        // The Carina VIO engine captures the callback pointers at start time,
        // so registration happens before StartPose (vendor demo comment). The
        // pose callback supplies the SDK timestamp for polled poses; the
        // camera callback is a no-op the VIO engine expects to be present;
        // vsync and IMU frames are not consumed by the S5 adapter.
        g_pose_callback_target.store(this, std::memory_order_release);
        code = fns_.register_callbacks_carina(handle_, &ViturePoseCallback, nullptr, nullptr, &VitureCameraCallback);
        if (trace) {
            std::fprintf(stderr, "viture-device: register_callbacks_carina rc=%d\n", code);
        }
        if (code != 0) {
            const Status status = VendorFailure("xr_device_provider_register_callbacks_carina", code);
            DestroyHandle();
            return Err<void>(status);
        }
        return Ok();
    }

    void DestroyDevice() noexcept override {
        if (handle_ == nullptr) {
            return;
        }
        const bool trace = DebugTracesEnabled();
        ReleaseCallback();
        if (started_) {
            const int stop_code = fns_.stop(handle_);
            if (trace) {
                std::fprintf(stderr, "viture-device: stop rc=%d\n", stop_code);
                std::fflush(stderr);
            }
            const int shutdown_code = fns_.shutdown(handle_);
            if (trace) {
                std::fprintf(stderr, "viture-device: shutdown rc=%d\n", shutdown_code);
                std::fflush(stderr);
            }
        }
        fns_.destroy(handle_);
        if (trace) {
            std::fprintf(stderr, "viture-device: destroy done\n");
            std::fflush(stderr);
        }
        handle_ = nullptr;
        started_ = false;
        stop_requested_.store(false, std::memory_order_relaxed);
    }

    Result<void> StartPose() override {
        if (handle_ == nullptr) {
            return Err<void>(Status{StatusCode::NotReady, "viture: no device; call CreateDevice first"});
        }
        if (started_) {
            return Ok();
        }
        stop_requested_.store(false, std::memory_order_relaxed);
        const int code = fns_.start(handle_);
        if (DebugTracesEnabled()) {
            std::fprintf(stderr, "viture-device: start rc=%d\n", code);
        }
        if (code != 0) {
            return Err<void>(VendorFailure("xr_device_provider_start", code));
        }
        started_ = true;
        return Ok();
    }

    Result<cg_head_sample> PollPose() override {
        if (stop_requested_.load(std::memory_order_relaxed)) {
            return Err<cg_head_sample>(Status{StatusCode::Timeout, "viture: poll interrupted by RequestStop"});
        }
        if (handle_ == nullptr) {
            return Err<cg_head_sample>(Status{StatusCode::NotReady, "viture: no device; call CreateDevice first"});
        }

        float pose[kViturePoseFloatCount] = {};
        int pose_status = 1;
        const bool trace = DebugTracesEnabled();
        if (trace) {
            std::fprintf(stderr, "viture-poll: enter\n");
            std::fflush(stderr);
        }
        const int code = fns_.get_gl_pose_carina(handle_, pose, 0.0, &pose_status);
        if (trace) {
            std::fprintf(stderr, "viture-poll: exit rc=%d status=%d\n", code, pose_status);
        }
        if (code != 0) {
            return Err<cg_head_sample>(VendorFailure("xr_device_provider_get_gl_pose_carina", code));
        }

        // The poll API carries no timestamp: use the newest Carina pose
        // callback stamp (SDK monotonic seconds) as the sample's SDK time.
        // Until the first callback arrives the fallback is the host steady
        // clock, which the HIL report must treat as host-derived (the offset
        // estimate is then ~0 rather than a pipeline latency).
        const double stamp_seconds =
            has_stamp_.load(std::memory_order_acquire) ? stamp_seconds_.load(std::memory_order_relaxed) : SteadySeconds();

        cg_head_sample sample{};
        sample.host_time = static_cast<cg_time_ns>(std::llround(stamp_seconds * 1e9));
        sample.pose = cg_pose{cg_vec3{pose[0], pose[1], pose[2]}, cg_quat{pose[3], pose[4], pose[5], pose[6]}};
        sample.state = pose_status == 0 ? CG_TRACK_STABLE : CG_TRACK_UNSTABLE;
        sample.sequence = ++sequence_;
        return Ok(sample);
    }

    Result<void> ResetOriginCarina(const std::array<float, kViturePoseFloatCount> &pose) override {
        if (handle_ == nullptr) {
            return Err<void>(Status{StatusCode::NotReady, "viture: no device; call CreateDevice first"});
        }
        std::array<float, kViturePoseFloatCount> mutable_pose = pose;
        const int code = fns_.reset_origin_carina(handle_, mutable_pose.data());
        if (code != 0) {
            return Err<void>(VendorFailure("xr_device_provider_reset_origin_carina", code));
        }
        return Ok();
    }

    Result<void> SetDisplayMode(std::uint32_t refresh_hz, bool sbs) override {
        if (handle_ == nullptr) {
            return Err<void>(Status{StatusCode::NotReady, "viture: no device; call CreateDevice first"});
        }
        const std::optional<int> mode = DisplayModeFor(refresh_hz, sbs);
        if (!mode.has_value()) {
            return Err<void>(Status{
                StatusCode::Unsupported,
                InternMessage("viture: unsupported display mode " + std::to_string(refresh_hz) + " Hz " +
                              (sbs ? "SBS" : "2D") + "; supported: 2D 60/90/120 Hz, SBS 60/90 Hz (F-09)")});
        }
        const int code = fns_.set_display_mode(handle_, *mode);
        if (code != 0) {
            return Err<void>(VendorFailure("xr_device_provider_set_display_mode", code));
        }
        return Ok();
    }

    Result<std::uint32_t> GetRefreshHz() override {
        if (handle_ == nullptr) {
            return Err<std::uint32_t>(Status{StatusCode::NotReady, "viture: no device; call CreateDevice first"});
        }
        const int mode = fns_.get_display_mode(handle_);
        if (mode < 0) {
            return Err<std::uint32_t>(VendorFailure("xr_device_provider_get_display_mode", mode));
        }
        const std::optional<std::uint32_t> refresh_hz = RefreshHzForMode(mode);
        if (!refresh_hz.has_value()) {
            return Err<std::uint32_t>(Status{StatusCode::Unsupported,
                                             InternMessage("viture: unknown display mode value " +
                                                           std::to_string(mode) + " from the SDK")});
        }
        return Ok(*refresh_hz);
    }

    std::string SdkVersion() const override {
        if (handle_ == nullptr) {
            return {};
        }
        // The real SDK exposes the glasses firmware string, not an SDK version
        // getter (`carina_a1088_viture_get_sdk_version` lives in the internal
        // carina_vio library, which this loader does not open). Diagnostics
        // only.
        std::array<char, 128> buffer{};
        int length = static_cast<int>(buffer.size());
        if (fns_.get_glasses_version(handle_, buffer.data(), &length) != 0 || length <= 0) {
            return {};
        }
        const std::size_t count =
            std::min<std::size_t>(static_cast<std::size_t>(length), static_cast<std::size_t>(buffer.size() - 1U));
        return std::string{buffer.data(), count};
    }

    void RequestStop() noexcept override { stop_requested_.store(true, std::memory_order_relaxed); }

    /// SDK-thread entry point for the Carina pose callback (lock-free).
    void OnPoseCallback(double timestamp) noexcept {
        stamp_seconds_.store(timestamp, std::memory_order_relaxed);
        has_stamp_.store(true, std::memory_order_release);
    }

  private:
    /// Releases the device handle of a failed `CreateDevice` (not started, so
    /// no stop/shutdown call).
    void DestroyHandle() noexcept {
        ReleaseCallback();
        fns_.destroy(handle_);
        handle_ = nullptr;
    }

    /// Clears the process-wide callback target if this instance owns it.
    void ReleaseCallback() noexcept {
        VendorVitureApi *expected = this;
        g_pose_callback_target.compare_exchange_strong(expected, nullptr, std::memory_order_acq_rel);
    }

    VitureApiFns fns_{};
    XrDeviceProviderHandle handle_ = nullptr;
    bool started_ = false;
    std::atomic<bool> stop_requested_{false};
    std::uint32_t sequence_ = 0;

    // SDK-thread -> polling-thread stamp transfer (same discipline as the
    // wrapper's `LastSdkSeconds`).
    std::atomic<double> stamp_seconds_{0.0};
    std::atomic<bool> has_stamp_{false};
};

void ViturePoseCallback(float * /*pose*/, double timestamp) {
    VendorVitureApi *target = g_pose_callback_target.load(std::memory_order_acquire);
    if (target != nullptr) {
        target->OnPoseCallback(timestamp);
    }
}

} // namespace

DllPathDecision DecideDllPath(const std::filesystem::path &candidate, const std::filesystem::path &working_directory,
                              const std::filesystem::path &executable_directory) {
    if (candidate.empty() || !candidate.is_absolute()) {
        return DllPathDecision::kRejectRelative;
    }
    const std::filesystem::path resolved = candidate.lexically_normal();
    const std::filesystem::path working = working_directory.lexically_normal();
    if (!IsWithin(resolved, working)) {
        return DllPathDecision::kAllow;
    }
    if (!executable_directory.empty() && SamePath(resolved.parent_path(), executable_directory.lexically_normal())) {
        return DllPathDecision::kAllow;
    }
    return DllPathDecision::kRejectInsideWorkingDirectory;
}

Result<std::unique_ptr<IVitureApi>> LoadVitureApi(const std::string &dll_path) {
    if (dll_path.empty()) {
        return Err<std::unique_ptr<IVitureApi>>(Status{StatusCode::InvalidArgument, "viture_loader: empty DLL path"});
    }

    std::error_code working_error;
    const std::filesystem::path working_directory = std::filesystem::current_path(working_error);
    if (working_error) {
        return Err<std::unique_ptr<IVitureApi>>(Status{
            StatusCode::InvalidArgument,
            "viture_loader: cannot resolve the working directory to validate the DLL path; pass an absolute path"});
    }
#ifdef _WIN32
    const std::filesystem::path candidate{Utf8ToWide(dll_path)};
#else
    const std::filesystem::path candidate{dll_path};
#endif
    switch (DecideDllPath(candidate, working_directory, ExecutableDirectory())) {
    case DllPathDecision::kRejectRelative:
        return Err<std::unique_ptr<IVitureApi>>(Status{
            StatusCode::InvalidArgument, InternMessage("viture_loader: refusing the relative DLL path '" + dll_path +
                                                       "'; pass an absolute path outside the working directory")});
    case DllPathDecision::kRejectInsideWorkingDirectory:
        return Err<std::unique_ptr<IVitureApi>>(Status{
            StatusCode::InvalidArgument,
            InternMessage("viture_loader: refusing the DLL path '" + dll_path +
                          "': it is inside the working directory, where a same-user process could have planted it; "
                          "pass an absolute path outside the working directory or place the DLL next to the running "
                          "executable")});
    case DllPathDecision::kAllow:
        break;
    }

    const std::filesystem::path resolved = candidate.lexically_normal();
#ifdef _WIN32
    const std::wstring wide = resolved.wstring();
    const std::string resolved_path = WideToUtf8(wide.c_str(), static_cast<int>(wide.size()));
#else
    const std::string resolved_path = resolved.string();
#endif

    // TD-051: optional content pin. The path policy constrains where the
    // library comes from; when the operator sets CG_VITURE_DLL_SHA256 the
    // loader additionally refuses unexpected bytes before opening the file.
    const std::string pin = ReadEnvironment("CG_VITURE_DLL_SHA256");
    if (!pin.empty()) {
        std::string expected = pin;
        if (expected.size() != kSha256HexLength || !std::all_of(expected.begin(), expected.end(), [](char character) {
                return std::isxdigit(static_cast<unsigned char>(character)) != 0;
            })) {
            return Err<std::unique_ptr<IVitureApi>>(
                Status{StatusCode::InvalidArgument,
                       InternMessage("viture_loader: CG_VITURE_DLL_SHA256 must be exactly 64 hex characters")});
        }
        std::transform(expected.begin(), expected.end(), expected.begin(), [](char character) {
            return static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
        });
        const std::optional<std::string> actual = FileSha256Hex(resolved);
        if (!actual.has_value()) {
            return Err<std::unique_ptr<IVitureApi>>(Status{
                StatusCode::Unsupported, InternMessage("viture_loader: cannot read '" + dll_path +
                                                       "' to verify CG_VITURE_DLL_SHA256; refusing the library")});
        }
        if (*actual != expected) {
            return Err<std::unique_ptr<IVitureApi>>(Status{
                StatusCode::Unsupported,
                InternMessage("viture_loader: DLL hash mismatch for '" + dll_path + "': CG_VITURE_DLL_SHA256 expects " +
                              expected + ", the file hashes to " + *actual + "; refusing the library")});
        }
    }

    std::string open_error;
    LibraryHandle handle = OpenLibrary(resolved_path, open_error);
    if (handle == nullptr) {
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Unsupported, InternMessage(dll_path + ": " + open_error)});
    }

    VitureApiFns fns{};
    const char *missing = nullptr;
    if (!ResolveVitureSymbols(handle, fns, missing)) {
        CloseLibrary(handle);
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Unsupported, InternMessage(std::string{missing} + ": symbol not found in " + dll_path)});
    }

    // Keep SDK chatter (USB retries, calibration notices) off the probe's
    // stdout so the HIL log stays parseable; errors still reach the default
    // logger. The null check keeps MSVC /analyze (C6011) quiet: the symbol is
    // required by ResolveVitureSymbols above, but the analyzer cannot see
    // through the out-parameter struct.
    if (fns.set_log_level != nullptr) {
        fns.set_log_level(1);
    }

    // CXX-11: construct with `new (std::nothrow)` so an allocation failure
    // cannot throw out of this function and cannot leak the library handle
    // (the constructor is noexcept; only the allocation itself can fail).
    std::unique_ptr<IVitureApi> api(new (std::nothrow) VendorVitureApi(fns));
    if (api == nullptr) {
        CloseLibrary(handle);
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Internal,
                   InternMessage("viture_loader: out of memory constructing the vendor API for " + dll_path)});
    }
    return Ok(std::move(api));
}

} // namespace cg::glasses
