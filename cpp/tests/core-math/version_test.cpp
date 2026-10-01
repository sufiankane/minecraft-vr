#include <gtest/gtest.h>
#include "cg/core_math/version.hpp"

TEST(CoreMathVersion, AbiVersionIsOne) {
  EXPECT_EQ(cg::core_math::AbiVersion(), 1);
}
