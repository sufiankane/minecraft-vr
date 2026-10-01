cmake_minimum_required(VERSION 3.25)

project(cubeglass-cpp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(CG_BUILD_BENCHMARKS "Build Cubeglass micro-benchmarks" OFF)

add_library(cg_warnings INTERFACE)
if(MSVC)
    target_compile_options(cg_warnings INTERFACE /W4 /WX)
else()
    target_compile_options(cg_warnings INTERFACE -Wall -Wextra -Wpedantic -Wconversion -Werror)
endif()

enable_testing()

find_package(GTest CONFIG REQUIRED)

add_subdirectory(core-math)
add_subdirectory(tests)
