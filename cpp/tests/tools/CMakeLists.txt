# The soak's RSS accounting lives in the header-only `rss_gate.hpp` next to
# the tool, so the unit tests compile it directly and do not build the soak
# binary (CXX-04).
add_executable(cg_soak_gate_tests rss_gate_tests.cpp)
target_link_libraries(cg_soak_gate_tests PRIVATE GTest::gtest_main cg_warnings)
target_include_directories(cg_soak_gate_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../tools/soak)

add_test(NAME soak_gate COMMAND cg_soak_gate_tests)
