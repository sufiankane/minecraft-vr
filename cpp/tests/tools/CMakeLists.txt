# The soak's RSS accounting lives in the header-only `rss_gate.hpp` next to
# the tool, so the unit tests compile it directly and do not build the soak
# binary (CXX-04).
add_executable(cg_soak_gate_tests rss_gate_tests.cpp)
target_link_libraries(cg_soak_gate_tests PRIVATE GTest::gtest_main cg_warnings)
target_include_directories(cg_soak_gate_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../tools/soak)

add_test(NAME soak_gate COMMAND cg_soak_gate_tests)

# TD-007: a short end-to-end smoke run of the soak tool registers with ctest so
# the latency and thread/handle gates run in CI, not only in the nightly lane.
# The run is much shorter than the 60 s RSS baseline, which the tool reports as
# "no baseline (short run)" and intentionally does not fail; the RSS budget
# itself is exercised by the nightly 30-minute run.
add_test(NAME soak_smoke COMMAND $<TARGET_FILE:glasses_soak> --minutes 0.05 --rate 200)
set_tests_properties(soak_smoke PROPERTIES TIMEOUT 120)
