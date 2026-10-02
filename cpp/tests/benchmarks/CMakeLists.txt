add_executable(cg_core_math_benchmarks version_benchmark.cpp core_math_benchmark.cpp glasses_benchmark.cpp)
target_include_directories(cg_core_math_benchmarks PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../glasses")
target_link_libraries(cg_core_math_benchmarks PRIVATE cg::core_math cg::glasses benchmark::benchmark cg_warnings)

set_target_properties(cg_core_math_benchmarks PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
