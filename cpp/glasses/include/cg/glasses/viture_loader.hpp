#pragma once

#include <filesystem>
#include <memory>
#include <string>

#include "cg/glasses/viture_api.hpp"
#include "result.hpp"

namespace cg::glasses {

/// The loader's decision for a caller-supplied vendor-DLL path.
enum class DllPathDecision : std::uint8_t {
    /// The path may be opened: an absolute path outside the working directory,
    /// or a DLL next to the running executable.
    kAllow,
    /// Relative paths are refused: resolving them against the working
    /// directory makes the loaded library attacker-influenced.
    kRejectRelative,
    /// The path resolves inside the working directory, where a same-user
    /// process can plant a DLL; only the executable's own directory is exempt.
    kRejectInsideWorkingDirectory,
};

/// Pure path policy behind `LoadVitureApi`, exposed so the decision matrix is
/// testable without a real vendor DLL.
///
/// `candidate` is accepted only when it is absolute and either outside
/// `working_directory` or a sibling of `executable_directory`. The comparison
/// is lexical (no filesystem access, no symlink resolution).
[[nodiscard]] DllPathDecision DecideDllPath(const std::filesystem::path &candidate,
                                            const std::filesystem::path &working_directory,
                                            const std::filesystem::path &executable_directory);

/// Loads the vendor VITURE SDK library at `dll_path` and binds its Carina
/// entry points behind `IVitureApi`.
///
/// Windows tooling passes a DLL path (for example from `CG_VITURE_DLL`);
/// POSIX resolves a shared object so CI type-checks the same translation
/// unit. An empty path, a relative path, or an absolute path inside the
/// working directory (except a DLL next to the running executable) is
/// `InvalidArgument`: the working directory is attacker-influenced, so a path
/// there is treated as a planted library. On Windows the library is opened
/// with the `LOAD_LIBRARY_SEARCH_*` flags, so neither the DLL nor its
/// dependencies resolve through the working directory or `PATH`. A library
/// that cannot be opened, or that does not export every entry point of the
/// provisional table, is `Unsupported`; the non-owning `Status` message names
/// the path or the first unresolved symbol. The returned API keeps the library
/// loaded for its whole lifetime.
///
/// Optional content pin (TD-051): when the `CG_VITURE_DLL_SHA256` environment
/// variable is set, it must hold exactly 64 hex characters (otherwise
/// `InvalidArgument`) and the loader hashes the file before opening it,
/// refusing a mismatch with `Unsupported` that names the expected and actual
/// digests. The pin catches a wrong, stale or corrupted build; it is not a
/// signature check (an attacker who can set the environment can also set the
/// expected hash).
///
/// Residual trust (documented, not mitigated here): the path policy trusts a
/// DLL placed next to the running executable, so a process able to write the
/// executable's directory can still replace the vendor binary (an OS
/// code-signing policy is the real control). On POSIX, `dlopen` uses the
/// platform's default dependency search (including `LD_LIBRARY_PATH` and the
/// caller's `RPATH`), so the vendor `.so`'s own dependencies are not
/// constrained by this policy; POSIX exists for CI type-checking and
/// development, not as a hardened deployment. Signature verification remains
/// the open residual. The vendor symbol table binds the real VITURE Windows
/// SDK exports (observed 2026-10-05, documented in `viture_loader.cpp`); the
/// live pose-rate/latency and display-mode answers (U-01/U-08) remain HIL
/// questions recorded in ADR-0009. `CreateDevice` resolves the product id
/// `xr_device_provider_create` requires from `CG_VITURE_PRODUCT_ID`
/// (decimal or 0x-hex) or, when unset, from a USB scan for VID 0x35CA.
[[nodiscard]] Result<std::unique_ptr<IVitureApi>> LoadVitureApi(const std::string &dll_path);

} // namespace cg::glasses
