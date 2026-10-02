add_executable(cg_bridge_layout_tests layout_tests.cpp)

target_link_libraries(cg_bridge_layout_tests PRIVATE cg::bridge GTest::gtest_main cg_warnings)

add_test(NAME bridge_layout COMMAND cg_bridge_layout_tests)
