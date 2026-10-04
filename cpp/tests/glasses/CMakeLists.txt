add_executable(
    cg_glasses_tests
    allocation_tests.cpp
    contract_tests.cpp
    display_control_tests.cpp
    recentre_state_tests.cpp
    replay_tests.cpp
    thread_safety_tests.cpp
    viture_fault_tests.cpp
    viture_loader_path_tests.cpp
    viture_loader_tests.cpp
)
target_link_libraries(cg_glasses_tests PRIVATE cg::glasses GTest::gtest cg_warnings)
target_compile_definitions(
    cg_glasses_tests PRIVATE CG_POSE_REPLAY_FIXTURE="${CMAKE_CURRENT_SOURCE_DIR}/data/pose_replay.csv"
)
