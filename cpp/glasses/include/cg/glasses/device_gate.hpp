#pragma once

#include <functional>

#include "result.hpp"

namespace cg::glasses {

/// One display-seam action executed inside a `DeviceGate`.
using DisplaySeamAction = std::function<Result<void>()>;

/// Mutual exclusion between the display seam and the pose device (CXX-01).
///
/// The display calls (`IVitureApi::SetDisplayMode`/`GetRefreshHz`) must never
/// run while a `PollPose` could be in flight or a `Start`/`Stop` transition is
/// running. `VitureDisplayControl` wraps every seam action in a gate; the
/// production gate is `VitureHeadPoseSource::WithDeviceStopped`, which holds
/// the source's lifecycle mutex for the action's duration. An empty gate runs
/// the action directly and is for standalone/test use, where the caller owns
/// the exclusivity of the seam.
using DeviceGate = std::function<Result<void>(const DisplaySeamAction &)>;

} // namespace cg::glasses
