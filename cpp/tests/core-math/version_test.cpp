#include "cg/core_math/version.hpp"
#include <gtest/gtest.h>

TEST(CoreMathVersion, AbiVersionIsOne) { EXPECT_EQ(cg::core_math::AbiVersion(), 1); }
