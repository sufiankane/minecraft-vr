#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <algorithm>
#include <cmath>
#include <tuple>

#include <gtest/gtest.h>
#include <rapidcheck/gtest.h>

namespace {

using cg::core_math::Length;
using cg::core_math::Normalize;
using cg::core_math::Quat;
using cg::core_math::Slerp;
using cg::core_math::Vec3;

// Arbitrary doubles mapped into [-2, 2]; a quarter of the inputs are the exact
// endpoints of the range, which keeps near-degenerate quaternions in the mix.
rc::Gen<double> BoundedComponent() {
    return rc::gen::map(rc::gen::arbitrary<double>(), [](double value) { return std::clamp(value, -2.0, 2.0); });
}

rc::Gen<Quat> BoundedQuat() {
    return rc::gen::map(rc::gen::tuple(BoundedComponent(), BoundedComponent(), BoundedComponent(), BoundedComponent()),
                        [](const std::tuple<double, double, double, double> &components) {
                            return Quat::FromComponents(std::get<0>(components), std::get<1>(components),
                                                        std::get<2>(components), std::get<3>(components));
                        });
}

rc::Gen<Vec3> BoundedVector() {
    return rc::gen::map(rc::gen::tuple(BoundedComponent(), BoundedComponent(), BoundedComponent()),
                        [](const std::tuple<double, double, double> &components) {
                            return Vec3{std::get<0>(components), std::get<1>(components), std::get<2>(components)};
                        });
}

double SquaredNorm(const Quat &q) { return q.w() * q.w() + q.x() * q.x() + q.y() * q.y() + q.z() * q.z(); }

RC_GTEST_PROP(QuatProperty, FromComponentsIsNormalizedAndFinite, ()) {
    const double w = *BoundedComponent();
    const double x = *BoundedComponent();
    const double y = *BoundedComponent();
    const double z = *BoundedComponent();

    const Quat q = Quat::FromComponents(w, x, y, z);
    RC_ASSERT(q.IsNormalized(1e-9));
    RC_ASSERT(std::isfinite(q.w()));
    RC_ASSERT(std::isfinite(q.x()));
    RC_ASSERT(std::isfinite(q.y()));
    RC_ASSERT(std::isfinite(q.z()));
}

RC_GTEST_PROP(QuatProperty, RotatePreservesLength, ()) {
    const Quat q = *BoundedQuat();
    const Vec3 v = *BoundedVector();

    const double before = Length(v);
    const double after = Length(q.Rotate(v));
    RC_ASSERT(std::abs(after - before) <= 1e-9);
}

RC_GTEST_PROP(QuatProperty, ProductWithInverseIsIdentity, ()) {
    const Quat q = *BoundedQuat();
    const Quat product = q * q.Inverse();
    RC_ASSERT(std::abs(product.w() - 1.0) <= 1e-9);
    RC_ASSERT(std::abs(product.x()) <= 1e-9);
    RC_ASSERT(std::abs(product.y()) <= 1e-9);
    RC_ASSERT(std::abs(product.z()) <= 1e-9);
}

RC_GTEST_PROP(QuatProperty, NormalizeProducesUnitQuaternion, ()) {
    const Quat q = *BoundedQuat();
    RC_PRE(SquaredNorm(q) >= 1e-24);

    const Quat normalised = Normalize(q);
    RC_ASSERT(normalised.IsNormalized(1e-9));
}

RC_GTEST_PROP(QuatProperty, SlerpEndpointsMatchInputs, ()) {
    const Quat a = *BoundedQuat();
    const Quat b = *BoundedQuat();

    const Quat start = Slerp(a, b, 0.0);
    RC_ASSERT(std::abs(start.w() - a.w()) <= 1e-12);
    RC_ASSERT(std::abs(start.x() - a.x()) <= 1e-12);
    RC_ASSERT(std::abs(start.y() - a.y()) <= 1e-12);
    RC_ASSERT(std::abs(start.z() - a.z()) <= 1e-12);

    // Slerp takes the shortest path, so a negative dot flips the end quaternion.
    const Quat shortestEnd = (a.Dot(b) < 0.0) ? Quat::FromComponents(-b.w(), -b.x(), -b.y(), -b.z()) : b;
    const Quat end = Slerp(a, b, 1.0);
    RC_ASSERT(std::abs(end.w() - shortestEnd.w()) <= 1e-12);
    RC_ASSERT(std::abs(end.x() - shortestEnd.x()) <= 1e-12);
    RC_ASSERT(std::abs(end.y() - shortestEnd.y()) <= 1e-12);
    RC_ASSERT(std::abs(end.z() - shortestEnd.z()) <= 1e-12);
}

} // namespace
