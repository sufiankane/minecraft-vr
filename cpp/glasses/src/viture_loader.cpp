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

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
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

/// Reads environment variable `name`, or an empty string when unset. Uses the
/// MSVC-safe `_dupenv_s` on Windows (plain `getenv` is a C4996 error under
/// `/W4 /WX`).
[[nodiscard]] std::string ReadEnvironment(const char *name) {
#ifdef _WIN32
    char *value = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&value, &size, name) != 0 || value == nullptr) {
        return {};
    }
    std::string result{value};
    std::free(value);
    return result;
#else
    const char *value = std::getenv(name);
    return value == nullptr ? std::string{} : std::string{value};
#endif
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
    const std::wstring left_text = left.native();
    const std::wstring right_text = right.native();
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
        if (expected.size() != 64U || !std::all_of(expected.begin(), expected.end(), [](char character) {
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

    // CXX-11: construct with `new (std::nothrow)` so an allocation failure
    // cannot throw out of this function and cannot leak the library handle
    // (the constructor is noexcept; only the allocation itself can fail).
    std::unique_ptr<IVitureApi> api(new (std::nothrow) VendorVitureApi(handle, fns));
    if (api == nullptr) {
        CloseLibrary(handle);
        return Err<std::unique_ptr<IVitureApi>>(
            Status{StatusCode::Internal,
                   InternMessage("viture_loader: out of memory constructing the vendor API for " + dll_path)});
    }
    return Ok(std::move(api));
}

} // namespace cg::glasses
