#pragma once

#include <filesystem>
#include <memory>
#include <string>

#include "cg/glasses/viture_api.hpp"
#include "result.hpp"

namespace cg::glasses {

/// The loader's decision for a caller-supplied vendor-DLL path.
enum class DllPathDecision {
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
/// Authenticating the vendor DLL (signature/hash pinning) remains open and is
/// recorded as tech debt; the path policy constrains where a library may come
/// from, not what it contains. The vendor symbol table is provisional and
/// documented in `viture_loader.cpp`; the exact export names are a HIL question
/// recorded in ADR-0009.
[[nodiscard]] Result<std::unique_ptr<IVitureApi>> LoadVitureApi(const std::string &dll_path);

} // namespace cg::glasses
