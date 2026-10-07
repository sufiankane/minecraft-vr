add_executable(cg_capture_tests contract_tests.cpp slot_tests.cpp)

# TD-010: cg_capture propagates cg_warnings PUBLIC, so this header consumer
# compiles under /W4 /WX without repeating the link.
target_link_libraries(cg_capture_tests PRIVATE cg::capture GTest::gtest_main)
