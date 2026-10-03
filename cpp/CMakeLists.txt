cmake_minimum_required(VERSION 3.25)

project(cubeglass-cpp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(CG_BUILD_BENCHMARKS "Build Cubeglass micro-benchmarks" OFF)

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
