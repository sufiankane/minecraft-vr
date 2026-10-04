add_library(
    cg_glasses STATIC
    src/display_control.cpp
    src/fake_head_pose_source.cpp
    src/file_hash.cpp
    src/replay_head_pose_source.cpp
    src/viture_loader.cpp
    src/viture_head_pose_source.cpp
)
add_library(cg::glasses ALIAS cg_glasses)

target_compile_features(cg_glasses PUBLIC cxx_std_20)
target_include_directories(
    cg_glasses
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
)
# TD-010: cg_warnings is PUBLIC because these public headers are consumed
# directly; a consumer must compile them under the same /W4 /WX (MSVC) or
# -Wall -Wextra -Wconversion -Werror (GCC/Clang) regime.
target_link_libraries(cg_glasses PUBLIC cg::core_math cg_warnings)
if(NOT WIN32)
    target_link_libraries(cg_glasses PRIVATE ${CMAKE_DL_LIBS})
endif()
