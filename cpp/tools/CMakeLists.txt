add_executable(cg_pose_probe pose_probe/main.cpp)
target_link_libraries(cg_pose_probe PRIVATE cg::glasses cg_warnings)
if(WIN32)
    target_link_libraries(cg_pose_probe PRIVATE winmm)
endif()

set_target_properties(cg_pose_probe PROPERTIES
    OUTPUT_NAME "cg-pose-probe"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

add_executable(glasses_soak soak/glasses_soak.cpp)
target_link_libraries(glasses_soak PRIVATE cg::glasses cg_warnings)
if(WIN32)
    target_link_libraries(glasses_soak PRIVATE winmm psapi)
endif()

set_target_properties(glasses_soak PROPERTIES
    OUTPUT_NAME "glasses_soak"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
