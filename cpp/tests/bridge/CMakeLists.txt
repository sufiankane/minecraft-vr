add_executable(cg_bridge_layout_tests layout_tests.cpp)

target_link_libraries(cg_bridge_layout_tests PRIVATE cg::bridge GTest::gtest_main cg_warnings)

add_test(NAME bridge_layout COMMAND cg_bridge_layout_tests)

add_executable(cg_bridge_reader_tests shm_reader_tests.cpp)

# TD-004/TD-067: the reader tests drive the reader through the test-only writer,
# which now lives in cg_bridge_test_support (cg_unity_bridge.dll stays
# production-only).
target_link_libraries(
    cg_bridge_reader_tests PRIVATE cg::bridge cg::bridge_test_support GTest::gtest_main cg_warnings
)

add_test(NAME bridge_reader COMMAND cg_bridge_reader_tests)

add_executable(cg_bridge_stress_tests shm_stress_tests.cpp)

find_package(Threads REQUIRED)

target_link_libraries(
    cg_bridge_stress_tests PRIVATE cg::bridge cg::bridge_test_support GTest::gtest_main cg_warnings Threads::Threads
)

add_test(NAME bridge_stress COMMAND cg_bridge_stress_tests)

# CXX-21: the Windows cg_bridge DLL would resolve operator new from its own
# module, so a replacing test executable could not observe it. This gate
# compiles the same bridge sources into the test binary (statically) and
# replaces the global allocation functions there; see allocation_tests.cpp.
# test_writer.cpp is compiled in directly (not linked from
# cg_bridge_test_support) so the replacing operator new observes its
# create/close allocations too.
add_executable(
    cg_bridge_allocation_tests allocation_tests.cpp "${CMAKE_CURRENT_SOURCE_DIR}/../../bridge/src/bridge.cpp"
                                                  "${CMAKE_CURRENT_SOURCE_DIR}/../../bridge/src/test_writer.cpp"
)
target_compile_features(cg_bridge_allocation_tests PRIVATE cxx_std_20)
target_include_directories(
    cg_bridge_allocation_tests
    PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../../bridge/include" "${CMAKE_CURRENT_SOURCE_DIR}/../../../contracts"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../../contracts/cpp"
)
target_link_libraries(cg_bridge_allocation_tests PRIVATE cg::core_math GTest::gtest_main cg_warnings)
if(NOT MSVC)
    # Same CXX-19 reason as the cg_bridge target: the sources under test keep
    # the object-model deviation documented in shm_layout.hpp.
    target_compile_options(cg_bridge_allocation_tests PRIVATE -fno-strict-aliasing)
endif()

add_test(NAME bridge_allocation COMMAND cg_bridge_allocation_tests)

if(WIN32)
    # Both native libraries are SHARED on Windows; put them next to the tests
    # that import their symbols so ctest can run them from the build tree. The
    # copies are tracked outputs so they are refreshed whenever a DLL changes.
    set(cg_bridge_runtime_dll "${CMAKE_CURRENT_BINARY_DIR}/cg_unity_bridge.dll")
    add_custom_command(
        OUTPUT "${cg_bridge_runtime_dll}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:cg_bridge>" "${cg_bridge_runtime_dll}"
        DEPENDS cg_bridge
        COMMENT "Copying cg_unity_bridge.dll next to the bridge tests"
        VERBATIM
    )
    set(cg_bridge_test_support_runtime_dll "${CMAKE_CURRENT_BINARY_DIR}/cg_bridge_test_support.dll")
    add_custom_command(
        OUTPUT "${cg_bridge_test_support_runtime_dll}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:cg_bridge_test_support>"
                "${cg_bridge_test_support_runtime_dll}"
        DEPENDS cg_bridge_test_support
        COMMENT "Copying cg_bridge_test_support.dll next to the bridge tests"
        VERBATIM
    )
    add_custom_target(
        cg_bridge_test_runtime ALL DEPENDS "${cg_bridge_runtime_dll}" "${cg_bridge_test_support_runtime_dll}"
    )
    add_dependencies(cg_bridge_reader_tests cg_bridge_test_runtime)
    add_dependencies(cg_bridge_stress_tests cg_bridge_test_runtime)
endif()
