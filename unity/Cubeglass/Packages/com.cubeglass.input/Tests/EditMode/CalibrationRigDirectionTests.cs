using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Unity.Rendering;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// TD-022: the calibration look direction was pinned only at the
    /// <see cref="SyntheticPoseDrive"/> level. This covers the invariant one
    /// level up — a scripted turn driven through the provider into the
    /// <see cref="LateLatchPose"/> and out to the rig's cameras — so the sign
    /// contract is verified where it actually matters (the eye transforms).
    /// </summary>
    /// <remarks>
    /// Conventions: internal yaw is the negative of Unity yaw (ADR-0004), the
    /// drive adds <c>TurnSnap</c> to the internal yaw, and
    /// <c>UnityConvert</c> maps internal <c>(w,x,y,z)</c> to Unity
    /// <c>(w,-x,-y,z)</c>. A device-right scripted turn (negative internal yaw)
    /// must therefore end with a positive Unity camera yaw and the camera
    /// forward pointing to Unity's right (+X).
    /// </remarks>
    public sealed class CalibrationRigDirectionTests
    {
        private const float Tolerance = 1e-3f;

        private GameObject root;
        private StereoRig rig;
        private LateLatchPose latch;
        private SyntheticPoseProvider provider;
        private SyntheticPoseDrive drive;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("CalibrationRigDirection");
            var rigObject = new GameObject("Rig");
            rigObject.transform.SetParent(root.transform, false);
            rig = rigObject.AddComponent<StereoRig>();
            rig.ApplyEyeLayout(3840, 1080);

            var providerObject = new GameObject("Provider");
            providerObject.transform.SetParent(root.transform, false);
            provider = providerObject.AddComponent<SyntheticPoseProvider>();
            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);
            drive = providerObject.AddComponent<SyntheticPoseDrive>();
            drive.Provider = provider;

            latch = rigObject.AddComponent<LateLatchPose>();
            latch.Rig = rig;
            latch.Provider = provider;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            root = null;
            rig = null;
            latch = null;
            provider = null;
            drive = null;
        }

        [Test]
        public void DeviceRightScriptedTurnYawsTheEyeCamerasToUnityRight()
        {
            // A scripted right turn: TurnSnap -45 deg/s over one second adds
            // -45 to the internal yaw (SyntheticPoseDriveTests convention).
            drive.Advance(Frame(turnSnap: -45f), 0, 1f);
            latch.TickOnce();

            Assert.AreEqual(PoseTrackingState.Stable, latch.TrackingState, "the scripted sample must apply");
            Camera left = rig.LeftCamera;
            Camera right = rig.RightCamera;
            Assert.IsNotNull(left, "the rig must have a left eye");
            Assert.IsNotNull(right, "the rig must have a right eye");

            Assert.Greater(left.transform.forward.x, 0.5f, "a right turn must face Unity +X");
            Assert.Greater(right.transform.forward.x, 0.5f, "both eyes must turn the same way");
            Assert.AreEqual(45f, Mathf.DeltaAngle(0f, left.transform.eulerAngles.y), 0.5f, "Unity yaw is the positive mirror of the internal yaw");
            Assert.AreEqual(45f, Mathf.DeltaAngle(0f, right.transform.eulerAngles.y), 0.5f, "both eye cameras share the rig yaw");
        }

        [Test]
        public void DeviceLeftScriptedTurnYawsTheEyeCamerasToUnityLeft()
        {
            drive.Advance(Frame(turnSnap: 45f), 0, 1f);
            latch.TickOnce();

            Camera left = rig.LeftCamera;
            Assert.Less(left.transform.forward.x, -0.5f, "a left turn must face Unity -X");
            Assert.AreEqual(-45f, Mathf.DeltaAngle(0f, left.transform.eulerAngles.y), 0.5f, "internal +yaw is Unity -yaw");
        }

        [Test]
        public void RecentreReturnsTheEyesToThePlayerForward()
        {
            drive.Advance(Frame(turnSnap: -45f), 0, 1f);
            latch.TickOnce();
            Assert.Greater(rig.LeftCamera.transform.forward.x, 0.5f);

            drive.Advance(Frame(recenter: true), 0, 1f / 60f);
            drive.Advance(Frame(), 0, 0f);
            latch.TickOnce();

            Vector3 forward = rig.LeftCamera.transform.forward;
            Assert.AreEqual(0f, forward.x, Tolerance, "recentred, the camera faces the player forward");
            Assert.AreEqual(1f, forward.z, Tolerance, "Unity forward is +Z");
            Assert.AreEqual(PoseTrackingState.Stable, latch.TrackingState);
        }

        private static InputFrame Frame(
            Vector2f move = default,
            float turnSnap = 0f,
            bool recenter = false)
        {
            return new InputFrame(
                move,
                turnSnap,
                recenter,
                null,
                ButtonState.Up,
                ButtonState.Up,
                0,
                TrackingQuality.Good);
        }
    }
}
