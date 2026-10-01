#include "cg/core_math/convert.hpp"

namespace cg::core_math {

Pose PoseFromSdk(const float sdk[7]) noexcept {
    return Pose{Vec3{sdk[0], sdk[1], sdk[2]}, Quat::FromComponents(sdk[3], sdk[4], sdk[5], sdk[6])};
}

Vec3 ToUnityPosition(const Vec3 &position) noexcept { return Vec3{position.x, position.y, -position.z}; }

Quat ToUnityRotation(const Quat &rotation) noexcept {
    return Quat::FromComponents(rotation.w(), -rotation.x(), -rotation.y(), rotation.z());
}

Pose ToUnityPose(const Pose &pose) noexcept {
    return Pose{ToUnityPosition(pose.position), ToUnityRotation(pose.rotation)};
}

} // namespace cg::core_math
