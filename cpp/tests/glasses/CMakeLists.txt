add_executable(cg_glasses_tests contract_tests.cpp viture_fault_tests.cpp viture_loader_tests.cpp)
target_link_libraries(cg_glasses_tests PRIVATE cg::glasses GTest::gtest cg_warnings)
