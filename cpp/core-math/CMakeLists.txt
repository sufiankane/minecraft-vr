add_library(cg_core_math STATIC src/version.cpp src/vec3.cpp src/quat.cpp)
add_library(cg::core_math ALIAS cg_core_math)

target_compile_features(cg_core_math PUBLIC cxx_std_20)
target_include_directories(cg_core_math PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(cg_core_math PRIVATE cg_warnings)
