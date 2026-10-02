#pragma once

#include <memory>
#include <string>

#include "cg/glasses/viture_api.hpp"
#include "result.hpp"

namespace cg::glasses {

/// Loads the vendor VITURE SDK library at `dll_path` and binds its Carina
/// entry points behind `IVitureApi`.
///
/// Windows tooling passes a DLL path (for example from `CG_VITURE_DLL`);
/// POSIX resolves a shared object so CI type-checks the same translation
/// unit. An empty path is `InvalidArgument`. A library that cannot be opened,
/// or that does not export every entry point of the provisional table, is
/// `Unsupported`; the non-owning `Status` message names the path or the first
/// unresolved symbol. The returned API keeps the library loaded for its whole
/// lifetime.
///
/// The vendor symbol table is provisional and documented in
/// `viture_loader.cpp`; the exact export names are a HIL question recorded in
/// ADR-0009.
[[nodiscard]] Result<std::unique_ptr<IVitureApi>> LoadVitureApi(const std::string &dll_path);

} // namespace cg::glasses
