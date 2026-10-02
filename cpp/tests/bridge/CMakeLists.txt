add_executable(cg_bridge_layout_tests layout_tests.cpp)

target_link_libraries(cg_bridge_layout_tests PRIVATE cg::bridge GTest::gtest_main cg_warnings)

add_test(NAME bridge_layout COMMAND cg_bridge_layout_tests)

add_executable(cg_bridge_reader_tests shm_reader_tests.cpp)

target_link_libraries(cg_bridge_reader_tests PRIVATE cg::bridge GTest::gtest_main cg_warnings)

add_test(NAME bridge_reader COMMAND cg_bridge_reader_tests)

add_executable(cg_bridge_stress_tests shm_stress_tests.cpp)

find_package(Threads REQUIRED)

target_link_libraries(cg_bridge_stress_tests PRIVATE cg::bridge GTest::gtest_main cg_warnings Threads::Threads)

add_test(NAME bridge_stress COMMAND cg_bridge_stress_tests)

if(WIN32)
    # cg_bridge is SHARED on Windows; put the DLL next to the tests that
    # import its symbols so ctest can run them from the build tree. The copy is
    # a tracked output so it is refreshed whenever the DLL changes.
    set(cg_bridge_runtime_dll "${CMAKE_CURRENT_BINARY_DIR}/cg_unity_bridge.dll")
    add_custom_command(
        OUTPUT "${cg_bridge_runtime_dll}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:cg_bridge>" "${cg_bridge_runtime_dll}"
        DEPENDS cg_bridge
        COMMENT "Copying cg_unity_bridge.dll next to the bridge tests"
        VERBATIM
    )
    add_custom_target(cg_bridge_test_runtime ALL DEPENDS "${cg_bridge_runtime_dll}")
    add_dependencies(cg_bridge_reader_tests cg_bridge_test_runtime)
    add_dependencies(cg_bridge_stress_tests cg_bridge_test_runtime)
endif()
