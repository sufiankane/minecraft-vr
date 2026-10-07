find_package(nlohmann_json CONFIG REQUIRED)

add_library(cg_capture STATIC src/fake_stereo_source.cpp src/newest_frame_slot.cpp src/pgm.cpp src/cgrec.cpp
                               src/recorder.cpp src/replay_stereo_source.cpp)
add_library(cg::capture ALIAS cg_capture)

target_compile_features(cg_capture PUBLIC cxx_std_20)
target_include_directories(
    cg_capture
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts"
           "${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/cpp"
)
# HostTime comes from core-math through ports.hpp, so its include directory is
# part of this module's public surface; the port contract types stay in
# contracts/cpp (dossier 5.3).
target_link_libraries(cg_capture PUBLIC cg::core_math cg_warnings)
target_link_libraries(cg_capture PRIVATE nlohmann_json::nlohmann_json)
# TD-030: analyze only this module's sources, not its consumers (tests/tools).
target_link_libraries(cg_capture PRIVATE cg_analyze)
