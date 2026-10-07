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

# The capture probe's argument parsing and report formatting live in the
# header-only `args.hpp` next to the tool, so the unit tests pin the CLI
# contract without a device (the rss_gate.hpp pattern).
add_executable(cg_capture_probe_tests capture_probe_tests.cpp)
target_link_libraries(cg_capture_probe_tests PRIVATE cg::capture GTest::gtest_main cg_warnings)
target_include_directories(cg_capture_probe_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../tools/capture_probe)

add_test(NAME capture_probe_args COMMAND cg_capture_probe_tests)

# The help path runs the real tool (exit 0, no device or library needed).
add_test(NAME capture_probe_help COMMAND $<TARGET_FILE:cg_capture_probe> --help)
