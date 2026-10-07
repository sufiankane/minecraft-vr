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
    viture_stereo_source_tests.cpp
)
# TD-010: no explicit cg_warnings here on purpose. cg_glasses propagates it
# PUBLIC, so this target (a plain header consumer) compiles under /W4 /WX
# without repeating the link; a regression in the propagation now fails the
# build instead of silently dropping the flags.
target_link_libraries(cg_glasses_tests PRIVATE cg::glasses GTest::gtest)
target_compile_definitions(
    cg_glasses_tests PRIVATE CG_POSE_REPLAY_FIXTURE="${CMAKE_CURRENT_SOURCE_DIR}/data/pose_replay.csv"
)
