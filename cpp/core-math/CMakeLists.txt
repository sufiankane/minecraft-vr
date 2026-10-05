add_library(cg_core_math STATIC src/version.cpp src/vec3.cpp src/quat.cpp src/pose.cpp src/convert.cpp src/clock_mapper.cpp)
add_library(cg::core_math ALIAS cg_core_math)

target_compile_features(cg_core_math PUBLIC cxx_std_20)
target_include_directories(cg_core_math PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
# TD-010: the warning flags are a usage requirement of the public headers, so
# they propagate to every consumer instead of stopping at this target.
target_link_libraries(cg_core_math PUBLIC cg_warnings)
# TD-030: analyze only this module's sources, not its consumers (tests/tools).
target_link_libraries(cg_core_math PRIVATE cg_analyze)
