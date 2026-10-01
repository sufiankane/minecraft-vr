add_executable(cg_core_math_tests core-math/version_test.cpp)

target_link_libraries(cg_core_math_tests PRIVATE cg::core_math GTest::gtest_main cg_warnings)

add_test(NAME core_math COMMAND cg_core_math_tests)
