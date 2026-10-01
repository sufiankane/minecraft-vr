add_executable(cg_core_math_benchmarks version_benchmark.cpp)
target_link_libraries(cg_core_math_benchmarks PRIVATE cg::core_math benchmark::benchmark cg_warnings)

set_target_properties(cg_core_math_benchmarks PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
