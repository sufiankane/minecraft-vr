if(WIN32)
    set(CG_BRIDGE_LIBRARY_TYPE SHARED)
else()
    set(CG_BRIDGE_LIBRARY_TYPE STATIC)
endif()

add_library(cg_bridge ${CG_BRIDGE_LIBRARY_TYPE} src/bridge.cpp)
add_library(cg::bridge ALIAS cg_bridge)

target_compile_features(cg_bridge PUBLIC cxx_std_20)
target_include_directories(
    cg_bridge
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
)
# TD-010: cg_warnings is PUBLIC so consumers of cg_unity_bridge.h and
# shm_layout.hpp compile under the shared warning regime.
target_link_libraries(cg_bridge PRIVATE cg::core_math PUBLIC cg_warnings)
# TD-030: analyze only this module's sources, not its consumers (tests/tools).
target_link_libraries(cg_bridge PRIVATE cg_analyze)

# TD-004/TD-067: the test-only native writer lives in its own shared library so
# the production cg_unity_bridge.dll exports only the frozen 5.12 ABI. C++ and
# C# tests link/load this library; the release workflow never ships it. Same C
# ABI, same include layout, same library type as cg_bridge (SHARED on Windows,
# STATIC elsewhere).
add_library(cg_bridge_test_support ${CG_BRIDGE_LIBRARY_TYPE} src/test_writer.cpp)
add_library(cg::bridge_test_support ALIAS cg_bridge_test_support)

target_compile_features(cg_bridge_test_support PUBLIC cxx_std_20)
target_include_directories(
    cg_bridge_test_support
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
)
target_link_libraries(cg_bridge_test_support PUBLIC cg_warnings)
# TD-030: test_writer.cpp is a bridge module source, so it is analyzed too.
target_link_libraries(cg_bridge_test_support PRIVATE cg_analyze)

# CXX-19: `copy_payload` in src/bridge.cpp accesses the C payload storage
# through `std::atomic_ref<std::uint64_t>` (the documented object-model
# deviation in include/cg/bridge/shm_layout.hpp), and src/test_writer.cpp uses
# the same word-wise atomic stores. Disable strict aliasing for these targets
# where the compiler supports the flag; MSVC does not exploit the aliasing here
# and has no equivalent option.
if(NOT MSVC)
    target_compile_options(cg_bridge PRIVATE -fno-strict-aliasing)
    target_compile_options(cg_bridge_test_support PRIVATE -fno-strict-aliasing)
endif()

if(WIN32)
    set_target_properties(cg_bridge PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON OUTPUT_NAME cg_unity_bridge)
    set_target_properties(
        cg_bridge_test_support PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON OUTPUT_NAME cg_bridge_test_support
    )
endif()
