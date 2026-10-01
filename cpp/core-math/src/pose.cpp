#include "cg/core_math/pose.hpp"

namespace cg::core_math {

Pose Compose(const Pose &parent, const Pose &child) noexcept {
    return Pose{parent.position + parent.rotation.Rotate(child.position), parent.rotation * child.rotation};
}

Pose Inverse(const Pose &pose) noexcept {
    const Quat rotation = pose.rotation.Inverse();
    return Pose{-rotation.Rotate(pose.position), rotation};
}

Vec3 TransformPoint(const Pose &pose, const Vec3 &point) noexcept {
    return pose.position + pose.rotation.Rotate(point);
}

} // namespace cg::core_math
