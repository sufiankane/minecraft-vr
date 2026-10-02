add_library(cg_glasses STATIC src/fake_head_pose_source.cpp)
add_library(cg::glasses ALIAS cg_glasses)

target_compile_features(cg_glasses PUBLIC cxx_std_20)
target_include_directories(
    cg_glasses
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
)
target_link_libraries(cg_glasses PUBLIC cg::core_math PRIVATE cg_warnings)
