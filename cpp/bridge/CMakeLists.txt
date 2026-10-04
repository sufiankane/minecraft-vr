if(WIN32)
    set(CG_BRIDGE_LIBRARY_TYPE SHARED)
else()
    set(CG_BRIDGE_LIBRARY_TYPE STATIC)
endif()

add_library(cg_bridge ${CG_BRIDGE_LIBRARY_TYPE} src/bridge.cpp src/test_writer.cpp)
add_library(cg::bridge ALIAS cg_bridge)

target_compile_features(cg_bridge PUBLIC cxx_std_20)
target_include_directories(
    cg_bridge
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
)
target_link_libraries(cg_bridge PRIVATE cg::core_math cg_warnings)

# CXX-19: `copy_payload` in src/bridge.cpp accesses the C payload storage
# through `std::atomic_ref<std::uint64_t>` (the documented object-model
# deviation in include/cg/bridge/shm_layout.hpp). Disable strict aliasing for
# this target where the compiler supports the flag; MSVC does not exploit the
# aliasing here and has no equivalent option.
if(NOT MSVC)
    target_compile_options(cg_bridge PRIVATE -fno-strict-aliasing)
endif()

if(WIN32)
    set_target_properties(cg_bridge PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON OUTPUT_NAME cg_unity_bridge)
endif()
