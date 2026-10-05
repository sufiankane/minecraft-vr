cmake_minimum_required(VERSION 3.25)

project(cubeglass-cpp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(CG_BUILD_BENCHMARKS "Build Cubeglass micro-benchmarks" OFF)
# TD-030: opt-in MSVC static analysis (/analyze) over the native module sources.
# Off by default so the normal Debug/Release lanes stay unchanged; the
# windows-analyze preset turns it on and the cpp-windows job builds the module
# targets under it. /analyze:external- keeps the analysis focused on our code,
# and the module targets keep /W4 /WX (cg_warnings), so a code-analysis warning
# fails the lane.
option(CG_ENABLE_ANALYZE "Run MSVC static analysis (/analyze) on the native modules" OFF)

add_library(cg_warnings INTERFACE)
if(MSVC)
    # /permissive- pins standard conformance (two-phase lookup, no MSVC
    # permissive fallbacks); /utf-8 makes source and execution character sets
    # UTF-8 on all MSVC locales. Both are added to the shared warning flags
    # (M-10); keep this list minimal and warning-clean under /W4 /WX.
    target_compile_options(cg_warnings INTERFACE /W4 /WX /permissive- /utf-8)
else()
    target_compile_options(cg_warnings INTERFACE -Wall -Wextra -Wpedantic -Wconversion -Werror)
endif()

# The module targets link this privately; when CG_ENABLE_ANALYZE is off it is an
# empty interface, so the flag is a no-op on every other lane. It is separate
# from cg_warnings because cg_warnings propagates to tests/tools, whose sources
# are deliberately outside the static-analysis scope (TD-030).
add_library(cg_analyze INTERFACE)
if(CG_ENABLE_ANALYZE AND MSVC)
    target_compile_options(cg_analyze INTERFACE /analyze /analyze:external-)
endif()

enable_testing()

find_package(GTest CONFIG REQUIRED)

add_subdirectory(core-math)
add_subdirectory(glasses)
add_subdirectory(bridge)
add_subdirectory(tools)
add_subdirectory(tests)

if(CG_BUILD_BENCHMARKS)
    find_package(benchmark CONFIG REQUIRED)
    add_subdirectory(tests/benchmarks)
endif()
