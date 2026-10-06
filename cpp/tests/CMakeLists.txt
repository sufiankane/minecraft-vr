find_package(rapidcheck CONFIG REQUIRED)
find_package(nlohmann_json CONFIG REQUIRED)

add_executable(
    cg_core_math_tests
    core-math/version_test.cpp
    core-math/vec3_test.cpp
    core-math/quat_test.cpp
    core-math/quat_property_test.cpp
    core-math/pose_test.cpp
    core-math/convert_test.cpp
    core-math/time_test.cpp
    core-math/clock_mapper_test.cpp
    core-math/golden_test.cpp
    core-math/allocation_test.cpp
)

target_link_libraries(
    cg_core_math_tests
    PRIVATE cg::core_math GTest::gtest_main rapidcheck cg_warnings nlohmann_json::nlohmann_json
)

target_compile_definitions(
    cg_core_math_tests
    PRIVATE CG_GOLDEN_FIXTURE="${CMAKE_CURRENT_SOURCE_DIR}/../../contracts/golden/transforms.json"
)

add_test(NAME core_math COMMAND cg_core_math_tests)

# Pin the RapidCheck property seed (CXX-15): TestParams::seed defaults to 0,
# which RapidCheck reads as "generate a random seed", so property coverage and
# shrink paths would vary run to run. A fixed seed keeps the lane deterministic
# and failures reproducible; a nightly lane may override RC_PARAMS to vary it.
set_tests_properties(core_math PROPERTIES ENVIRONMENT "RC_PARAMS=seed=20261004")

add_subdirectory(glasses)
add_test(NAME glasses COMMAND cg_glasses_tests)

add_subdirectory(capture)
add_test(NAME capture COMMAND cg_capture_tests)

add_subdirectory(bridge)

add_subdirectory(tools)
