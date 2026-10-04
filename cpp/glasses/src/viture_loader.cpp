// The only translation unit that knows a VITURE SDK symbol or opens the
// vendor library. No vendor header is included anywhere in the repository
// (ADR-0001, ADR-0009): the SDK surface arrives through the function table
// below, resolved at load time.
//
// HIL QUESTION (pending, recorded in ADR-0009 under "Vendor symbol binding"):
// the exact exported names, the calling convention and the poll-blocking
// behaviour of the vendor library are not covered by dossier facts
// F-01..F-10. F-02 and F-04 describe the Carina poll and recentre calls in
// prose, but the device lifecycle spellings and the display-mode surface
// (U-08) are unknown. The symbol names in this table are therefore
// placeholders: the two with a dossier citation are best-effort
// transcriptions and the rest are guesses in the same spelling style. The
// function-pointer signatures are contract-shaped placeholders as well, so
// the adapter at the bottom is a thin status mapper rather than the final
// binding. Until the HIL run answers the question, loading the real DLL fails
// with `Unsupported` naming the first unresolved placeholder, which is the
// documented pre-HIL behaviour. When the answers exist, this one translation
// unit is rewritten; nothing else changes.

#include "cg/glasses/viture_loader.hpp"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <system_error>
#include <unordered_map>
#include <utility>

#include "result.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
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

[[nodiscard]] LibraryHandle OpenLibrary(const std::string &path) {
    const std::wstring wide = Utf8ToWide(path);
    if (wide.empty()) {
        return nullptr;
    }
    return LoadLibraryW(wide.c_str());
}

void CloseLibrary(LibraryHandle handle) noexcept {
    if (handle != nullptr) {
        FreeLibrary(handle);
    }
}

[[nodiscard]] std::string LastLibraryError() {
    const DWORD error = GetLastError();
    LPWSTR buffer = nullptr;
    const DWORD length =
        FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                       nullptr, error, 0, reinterpret_cast<LPWSTR>(&buffer), 0, nullptr);
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

#else

using LibraryHandle = void *;

[[nodiscard]] LibraryHandle OpenLibrary(const std::string &path) noexcept {
    return dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
}

void CloseLibrary(LibraryHandle handle) noexcept {
    if (handle != nullptr) {
        dlclose(handle);
    }
}

[[nodiscard]] std::string LastLibraryError() {
    const char *error = dlerror();
    return error == nullptr ? "dlopen failed" : error;
}

#endif

/// Outcome of validating a caller-supplied vendor-DLL path.
enum class DllPathStatus {
    kAccepted,
    /// A relative path that normalises outside the current working directory.
    kEscapesWorkingDirectory,
    /// The path cannot be validated (no working directory, not resolvable).
    kUnresolvable,
};

/// Validates `path` and resolves a relative path to its absolute form.
///
/// A relative path is accepted only when it stays inside the current working
/// directory, and it is normalised to an absolute path before the loader opens
/// it. That keeps `LoadLibraryW`/`dlopen` from running their default search
/// order (cwd, `PATH`, system directories) on a bare name that a same-user
/// process could plant: `viture_glasses_sdk.dll` is allowed because it names
/// the working directory, `..\..\evil.dll` is rejected. Absolute paths are the
/// caller's explicit choice and are used as given; authenticating the vendor
/// DLL (signature/hash) remains open and is recorded as tech debt.
[[nodiscard]] DllPathStatus ResolveDllPath(std::string &path) {
#ifdef _WIN32
    const std::filesystem::path candidate{Utf8ToWide(path)};
#else
    const std::filesystem::path candidate{path};
#endif
    if (candidate.is_absolute()) {
        return DllPathStatus::kAccepted;
    }
    std::error_code error;
    const std::filesystem::path working_directory = std::filesystem::current_path(error);
    if (error) {
        return DllPathStatus::kUnresolvable;
    }
    const std::filesystem::path resolved = (working_directory / candidate).lexically_normal();
    if (!resolved.is_absolute()) {
        return DllPathStatus::kUnresolvable;
    }
    for (const std::filesystem::path &part : resolved.lexically_relative(working_directory)) {
        if (part == "..") {
            return DllPathStatus::kEscapesWorkingDirectory;
        }
    }
#ifdef _WIN32
    const std::wstring wide = resolved.wstring();
    path = WideToUtf8(wide.c_str(), static_cast<int>(wide.size()));
    if (path.empty()) {
        return DllPathStatus::kUnresolvable;
    }
#else
    path = resolved.string();
#endif
    return DllPathStatus::kAccepted;
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

// --- Provisional vendor binding table (see the HIL note at the top) --------

using CreateDeviceFn = cg_status (*)();
using DestroyDeviceFn = void (*)();
using StartPoseFn = cg_status (*)();
using PollPoseFn = cg_status (*)(cg_head_sample *out);
using ResetOriginCarinaFn = cg_status (*)(const float pose[7]);
using SetDisplayModeFn = cg_status (*)(std::uint32_t refresh_hz, bool sbs);
using GetRefreshHzFn = cg_status (*)(std::uint32_t *out_refresh_hz);
using SdkVersionFn = const char *(*)();

struct VitureApiFns {
    CreateDeviceFn create_device = nullptr;
    DestroyDeviceFn destroy_device = nullptr;
    StartPoseFn start_pose = nullptr;
    PollPoseFn poll_pose = nullptr;
    ResetOriginCarinaFn reset_origin_carina = nullptr;
    SetDisplayModeFn set_display_mode = nullptr;
    GetRefreshHzFn get_refresh_hz = nullptr;
    SdkVersionFn sdk_version = nullptr;
};

// Placeholder export names (see the HIL note at the top).
constexpr const char *kCreateDeviceSymbol = "xr_device_provider_create";
constexpr const char *kDestroyDeviceSymbol = "xr_device_provider_destroy";
constexpr const char *kStartPoseSymbol = "xr_device_provider_start_pose";
constexpr const char *kPollPoseSymbol = "xr_device_provider_get_gl_pose_carina";     // F-02
constexpr const char *kResetOriginSymbol = "xr_device_provider_reset_origin_carina"; // F-04
constexpr const char *kSetDisplayModeSymbol = "xr_device_provider_set_display_mode";
constexpr const char *kGetRefreshHzSymbol = "xr_device_provider_get_display_refresh_rate";
constexpr const char *kSdkVersionSymbol = "xr_device_provider_get_sdk_version";

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
    static_assert(sizeof(Function) == sizeof(symbol), "a function pointer must fit in a loader symbol");
    std::memcpy(&out, &symbol, sizeof(Function));
    return true;
}

[[nodiscard]] bool ResolveVitureSymbols(LibraryHandle handle, VitureApiFns &fns, const char *&missing) noexcept {
    if (!ResolveSymbol(handle, kCreateDeviceSymbol, fns.create_device)) {
        missing = kCreateDeviceSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kDestroyDeviceSymbol, fns.destroy_device)) {
        missing = kDestroyDeviceSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kStartPoseSymbol, fns.start_pose)) {
        missing = kStartPoseSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kPollPoseSymbol, fns.poll_pose)) {
        missing = kPollPoseSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kResetOriginSymbol, fns.reset_origin_carina)) {
        missing = kResetOriginSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kSetDisplayModeSymbol, fns.set_display_mode)) {
        missing = kSetDisplayModeSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kGetRefreshHzSymbol, fns.get_refresh_hz)) {
        missing = kGetRefreshHzSymbol;
        return false;
    }
    if (!ResolveSymbol(handle, kSdkVersionSymbol, fns.sdk_version)) {
        missing = kSdkVersionSymbol;
        return false;
    }
    return true;
}

[[nodiscard]] Result<void> ToResult(cg_status status) noexcept {
    if (status == CG_OK) {
        return Ok();
    }
    return Err<void>(FromCgStatus(status));
}

/// Thin adapter over a resolved provisional table. It owns the library handle,
/// so the resolved pointers stay valid exactly as long as the API object.
class VendorVitureApi final : public IVitureApi {
  public:
    VendorVitureApi(LibraryHandle handle, const VitureApiFns &fns) noexcept : handle_(handle), fns_(fns) {}

    ~VendorVitureApi() override { CloseLibrary(handle_); }

    VendorVitureApi(const VendorVitureApi &) = delete;
    VendorVitureApi &operator=(const VendorVitureApi &) = delete;

    Result<void> CreateDevice() override { return ToResult(fns_.create_device()); }

    void DestroyDevice() noexcept override { fns_.destroy_device(); }

    Result<void> StartPose() override {
        stop_requested_.store(false, std::memory_order_relaxed);
        return ToResult(fns_.start_pose());
    }

    Result<cg_head_sample> PollPose() override {
        if (stop_requested_.load(std::memory_order_relaxed)) {
            return Err<cg_head_sample>(Status{StatusCode::Timeout, "viture: poll interrupted by RequestStop"});
        }
        cg_head_sample sample{};
        const cg_status status = fns_.poll_pose(&sample);
        if (status != CG_OK) {
            return Err<cg_head_sample>(FromCgStatus(status));
        }
        return Ok(sample);
    }

    Result<void> ResetOriginCarina(const float pose[7]) override { return ToResult(fns_.reset_origin_carina(pose)); }

    Result<void> SetDisplayMode(std::uint32_t refresh_hz, bool sbs) override {
        return ToResult(fns_.set_display_mode(refresh_hz, sbs));
    }

    Result<std::uint32_t> GetRefreshHz() override {
        std::uint32_t refresh_hz = 0;
        const cg_status status = fns_.get_refresh_hz(&refresh_hz);
        if (status != CG_OK) {
            return Err<std::uint32_t>(FromCgStatus(status));
        }
        return Ok(refresh_hz);
    }

    std::string SdkVersion() const override {
        const char *version = fns_.sdk_version();
        return version == nullptr ? std::string{} : std::string{version};
    }

    void RequestStop() noexcept override { stop_requested_.store(true, std::memory_order_relaxed); }

  private:
    LibraryHandle handle_ = nullptr;
    VitureApiFns fns_{};
    std::atomic<bool> stop_requested_{false};
};

} // namespace

Result<std::unique_ptr<IVitureApi>> LoadVitureApi(const std::string &dll_path) {
    if (dll_path.empty()) {
        return Err<std::unique_ptr<IVitureApi>>(Status{StatusCode::InvalidArgument, "viture_loader: empty DLL path"});
    }

    std::string resolved_path = dll_path;
    switch (ResolveDllPath(resolved_path)) {
    case DllPathStatus::kAccepted:
        break;
    case DllPathStatus::kEscapesWorkingDirectory:
        return Err<std::unique_ptr<IVitureApi>>(Status{
            StatusCode::InvalidArgument,
            "viture_loader: relative DLL path escapes the working directory; pass an absolute path or keep the library "
            "inside the working directory"});
    case DllPathStatus::kUnresolvable:
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::InvalidArgument,
                   "viture_loader: cannot resolve the DLL path against the working directory; pass an absolute path"});
    }

    LibraryHandle handle = OpenLibrary(resolved_path);
    if (handle == nullptr) {
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Unsupported, InternMessage(dll_path + ": " + LastLibraryError())});
    }

    VitureApiFns fns{};
    const char *missing = nullptr;
    if (!ResolveVitureSymbols(handle, fns, missing)) {
        CloseLibrary(handle);
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Unsupported, InternMessage(std::string{missing} + ": symbol not found in " + dll_path)});
    }

    std::unique_ptr<IVitureApi> api = std::make_unique<VendorVitureApi>(handle, fns);
    return Ok(std::move(api));
}

} // namespace cg::glasses
