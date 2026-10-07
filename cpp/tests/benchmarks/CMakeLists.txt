add_executable(cg_core_math_benchmarks version_benchmark.cpp core_math_benchmark.cpp glasses_benchmark.cpp
                                       capture_benchmarks.cpp)
target_include_directories(
    cg_core_math_benchmarks PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../glasses" "${CMAKE_CURRENT_SOURCE_DIR}/../../capture/include"
)
target_link_libraries(cg_core_math_benchmarks PRIVATE cg::core_math cg::glasses cg::capture benchmark::benchmark cg_warnings)

set_target_properties(cg_core_math_benchmarks PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
