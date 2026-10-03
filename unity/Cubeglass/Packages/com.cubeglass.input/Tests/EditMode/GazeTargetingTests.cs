using System;
using System.Collections.Generic;
using Cubeglass.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins <see cref="GazeTargeting"/> against ADR-0004: the pointer origin is
    /// the eye midpoint routed through the single UnityConvert flip, yaw zero
    /// looks down internal -Z, a Unity right turn becomes negative internal
    /// yaw, positive pitch looks up and degenerate rigs are rejected.
    /// </summary>
    public class GazeTargetingTests
    {
        private const float Tolerance = 1e-4f;

        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void DestroyObjects()
        {
            for (int i = 0; i < objects.Count; i++)
            {
                UnityEngine.Object.DestroyImmediate(objects[i]);
            }

            objects.Clear();
        }

        [Test]
        public void MidpointOriginAndAdrForward()
        {
            Camera left = CreateEye(new Vector3(-0.032f, 0f, 0f), Quaternion.identity);
            Camera right = CreateEye(new Vector3(0.032f, 0f, 0f), Quaternion.identity);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(left, right, out PointerRay ray));
            Assert.AreEqual(0f, ray.Origin.X, Tolerance, "midpoint X");
            Assert.AreEqual(0f, ray.Origin.Y, Tolerance, "midpoint Y");
            Assert.AreEqual(0f, ray.Origin.Z, Tolerance, "midpoint Z");
            Assert.AreEqual(0f, ray.Direction.X, Tolerance, "ADR yaw zero faces -Z: X");
            Assert.AreEqual(0f, ray.Direction.Y, Tolerance, "ADR yaw zero faces -Z: Y");
            Assert.AreEqual(-1f, ray.Direction.Z, Tolerance, "ADR yaw zero faces -Z: Z");
        }

        [Test]
        public void OriginRunsThroughThePositionFlip()
        {
            Camera left = CreateEye(new Vector3(1f, 2f, 3f), Quaternion.identity);
            Camera right = CreateEye(new Vector3(1f, 2f, 3f), Quaternion.identity);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(left, right, out PointerRay ray));
            Assert.AreEqual(1f, ray.Origin.X, Tolerance);
            Assert.AreEqual(2f, ray.Origin.Y, Tolerance);
            Assert.AreEqual(-3f, ray.Origin.Z, Tolerance, "ADR position (x, y, z) -> (x, y, -z)");
        }

        [Test]
        public void UnityYawRightBecomesNegativeInternalYaw()
        {
            Quaternion yawRight = Quaternion.Euler(0f, 90f, 0f);
            Camera left = CreateEye(Vector3.zero, yawRight);
            Camera right = CreateEye(Vector3.zero, yawRight);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(left, right, out PointerRay ray));
            Assert.AreEqual(1f, ray.Direction.X, Tolerance, "Unity +90 yaw looks along Unity +X");
            Assert.AreEqual(0f, ray.Direction.Y, Tolerance);
            Assert.AreEqual(0f, ray.Direction.Z, Tolerance);

            float internalYaw = Mathf.Atan2(-(float)ray.Direction.X, -(float)ray.Direction.Z) * Mathf.Rad2Deg;
            Assert.AreEqual(-90f, internalYaw, 1e-3f, "internal positive yaw turns toward -X, so a right turn is negative");
        }

        [Test]
        public void PitchUpBecomesPositiveInternalPitch()
        {
            Quaternion pitchUp = Quaternion.Euler(-30f, 0f, 0f);
            Camera left = CreateEye(Vector3.zero, pitchUp);
            Camera right = CreateEye(Vector3.zero, pitchUp);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(left, right, out PointerRay ray));
            Assert.Greater(ray.Direction.Y, 0f, "ADR positive pitch looks up");

            float internalPitch = Mathf.Asin((float)ray.Direction.Y) * Mathf.Rad2Deg;
            Assert.AreEqual(30f, internalPitch, 0.01f);
        }

        [Test]
        public void SingleEyeFallsBackToThatEye()
        {
            Camera right = CreateEye(new Vector3(4f, 5f, 6f), Quaternion.identity);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(null, right, out PointerRay ray));
            Assert.AreEqual(4f, ray.Origin.X, Tolerance);
            Assert.AreEqual(5f, ray.Origin.Y, Tolerance);
            Assert.AreEqual(-6f, ray.Origin.Z, Tolerance);
            Assert.AreEqual(-1f, ray.Direction.Z, Tolerance);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(right, null, out PointerRay leftRay));
            Assert.AreEqual(leftRay.Origin.X, ray.Origin.X, Tolerance);
            Assert.AreEqual(leftRay.Origin.Z, ray.Origin.Z, Tolerance);
        }

        [Test]
        public void NoCameraReturnsFalseAndDefaultRay()
        {
            Assert.IsFalse(GazeTargeting.TryBuildPointerRay((Camera)null, null, out PointerRay ray));
            Assert.AreEqual(0f, ray.Origin.X);
            Assert.AreEqual(0f, ray.Origin.Z);
            Assert.AreEqual(0f, ray.Direction.Z);
        }

        [Test]
        public void StereoRigOverloadUsesBothEyes()
        {
            var rigObject = new GameObject("StereoRig");
            objects.Add(rigObject);
            Cubeglass.Unity.Rendering.StereoRig rig = rigObject.AddComponent<Cubeglass.Unity.Rendering.StereoRig>();
            rigObject.transform.position = new Vector3(0f, 1f, 2f);
            rig.ApplyEyeLayout(1920, 1080);

            Assert.IsTrue(GazeTargeting.TryBuildPointerRay(rig, out PointerRay ray));
            Assert.AreEqual(0f, ray.Origin.X, Tolerance, "IPD midpoint collapses to the rig origin");
            Assert.AreEqual(1f, ray.Origin.Y, Tolerance);
            Assert.AreEqual(-2f, ray.Origin.Z, Tolerance);
            Assert.AreEqual(-1f, ray.Direction.Z, Tolerance);
        }

        private Camera CreateEye(Vector3 position, Quaternion rotation)
        {
            var eyeObject = new GameObject("Eye");
            objects.Add(eyeObject);
            eyeObject.transform.position = position;
            eyeObject.transform.rotation = rotation;
            return eyeObject.AddComponent<Camera>();
        }
    }
}
