find_package(rapidcheck CONFIG REQUIRED)

add_executable(
    cg_core_math_tests
    core-math/version_test.cpp
    core-math/vec3_test.cpp
    core-math/quat_test.cpp
    core-math/quat_property_test.cpp
    core-math/pose_test.cpp
    core-math/convert_test.cpp
    core-math/time_test.cpp
)

target_link_libraries(
    cg_core_math_tests
    PRIVATE cg::core_math GTest::gtest_main rapidcheck cg_warnings
)

add_test(NAME core_math COMMAND cg_core_math_tests)
