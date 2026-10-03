using Cubeglass.CoreMath;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// EditMode pins for the S7 Task 4a body transform (ADR-0011): the world
    /// position and yaw are the ADR-0004 conversions of
    /// <c>PlayerState.Position</c> and <c>PlayerState.YawRadians</c>, the head
    /// child stays at the fixed eye height under every pose tick, and the head
    /// rotation is never read back into the body.
    /// </summary>
    public class PlayerRootTests
    {
        private const float Tolerance = 1e-5f;

        private GameObject rootObject;
        private GameObject headObject;
        private PlayerRoot root;

        [SetUp]
        public void CreateHierarchy()
        {
            rootObject = new GameObject("PlayerRootTest");
            root = rootObject.AddComponent<PlayerRoot>();
            headObject = new GameObject("Head");
            headObject.transform.SetParent(rootObject.transform, false);
            root.Head = headObject.transform;
        }

        [TearDown]
        public void DestroyHierarchy()
        {
            UnityEngine.Object.DestroyImmediate(rootObject);
            rootObject = null;
            headObject = null;
            root = null;
        }

        [Test]
        public void HeadingMatchesUnityConvertForYawOnlyRotations()
        {
            Assert.Less(
                Quaternion.Angle(Quaternion.identity, PlayerRoot.Heading(0f)),
                Tolerance,
                "internal yaw 0 faces Unity +Z");

            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, -90f, 0f), PlayerRoot.Heading(Mathf.PI * 0.5f)),
                Tolerance,
                "internal yaw 90 degrees becomes Unity -90 (ADR-0004)");

            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, 45f, 0f), PlayerRoot.Heading(-Mathf.PI * 0.25f)),
                Tolerance,
                "the conversion flips the yaw sign both ways");
        }

        [Test]
        public void SetPlayerPoseAppliesTheConvertedBodyPose()
        {
            root.SetPlayerPose(new Vec3(1.0, 2.0, 3.0), Mathf.PI * 0.5f);

            Assert.AreEqual(1f, rootObject.transform.position.x, Tolerance, "position.x");
            Assert.AreEqual(2f, rootObject.transform.position.y, Tolerance, "position.y");
            Assert.AreEqual(-3f, rootObject.transform.position.z, Tolerance, "position.z is mirrored");
            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, -90f, 0f), rootObject.transform.localRotation),
                Tolerance,
                "world yaw is the converted internal yaw");
            Assert.AreEqual(1.0, root.PositionInternal.X, 1e-12, "internal position is recorded");
            Assert.AreEqual(Mathf.PI * 0.5f, root.YawRadians, 1e-6f, "internal yaw is recorded");
            Assert.IsTrue(root.HasPose);
        }

        [Test]
        public void HeadStaysAtTheEyeHeightAndNeverDrivesTheBody()
        {
            root.SetPlayerPose(new Vec3(0.0, 0.0, 0.0), 0f);

            Assert.Less(
                Vector3.Distance(new Vector3(0f, PlayerRoot.EyeHeightMeters, 0f), headObject.transform.localPosition),
                Tolerance,
                "the head sits at the ADR-0008 eye height above the feet centre");

            // The late latch writes only the head's local rotation; the next
            // body pose tick must keep the converted body yaw and leave the
            // head-relative rotation alone.
            headObject.transform.localRotation = Quaternion.Euler(10f, 30f, 5f);
            root.SetPlayerPose(new Vec3(4.0, 0.0, -2.0), -Mathf.PI * 0.25f);

            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, 45f, 0f), rootObject.transform.localRotation),
                Tolerance,
                "the rig follows the player; the head rotation is not read back");
            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(10f, 30f, 5f), headObject.transform.localRotation),
                Tolerance,
                "the body pose tick does not touch the head-relative rotation");
            Assert.AreEqual(4f, rootObject.transform.position.x, Tolerance);
            Assert.AreEqual(2f, rootObject.transform.position.z, Tolerance, "the body translation is mirrored");
        }
    }
}
