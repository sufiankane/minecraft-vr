using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class PoseTests
    {
        // cos(pi/4), the component of a 90 degree rotation about a principal axis.
        private const double HalfSqrt2 = 0.7071067811865476;
        private const double Pi = 3.14159265358979323846;

        private static void AssertVecNear(Vec3 v, double x, double y, double z, double tolerance)
        {
            Assert.That(v.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(v.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(v.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        private static void AssertQuatNear(Quat q, double w, double x, double y, double z, double tolerance)
        {
            Assert.That(q.W, Is.EqualTo(w).Within(tolerance), "W");
            Assert.That(q.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(q.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(q.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        [Test]
        public void ComposeWithInverseIsIdentityWithinTolerance()
        {
            Pose pose = new Pose(new Vec3(0.3, -1.2, 2.5), Quat.FromAxisAngle(new Vec3(1.0, 2.0, 3.0), 0.7));

            Pose after = Pose.Compose(pose, pose.Inverse());
            AssertVecNear(after.Position, 0.0, 0.0, 0.0, 1e-12);
            AssertQuatNear(after.Rotation, 1.0, 0.0, 0.0, 0.0, 1e-12);

            Pose before = Pose.Compose(pose.Inverse(), pose);
            AssertVecNear(before.Position, 0.0, 0.0, 0.0, 1e-12);
            AssertQuatNear(before.Rotation, 1.0, 0.0, 0.0, 0.0, 1e-12);
        }

        [Test]
        public void InverseYaw90AtX1()
        {
            Pose pose = new Pose(new Vec3(1.0, 0.0, 0.0), Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0));

            Pose inverse = pose.Inverse();
            AssertVecNear(inverse.Position, 0.0, 0.0, -1.0, 1e-15);
            AssertQuatNear(inverse.Rotation, HalfSqrt2, 0.0, -HalfSqrt2, 0.0, 1e-15);
        }

        [Test]
        public void TransformPointRotatesThenTranslates()
        {
            Pose pose = new Pose(new Vec3(1.0, 2.0, 3.0), Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0));

            Vec3 transformed = pose.TransformPoint(new Vec3(0.0, 0.0, -1.0));
            AssertVecNear(transformed, 0.0, 2.0, 3.0, 1e-12);
        }

        [Test]
        public void TransformPointRoundTripsThroughInverse()
        {
            Pose pose = new Pose(new Vec3(0.3, -1.2, 2.5), Quat.FromAxisAngle(new Vec3(1.0, 2.0, 3.0), 0.7));
            Vec3 point = new Vec3(-4.0, 0.5, 2.0);

            Vec3 roundTrip = pose.Inverse().TransformPoint(pose.TransformPoint(point));
            AssertVecNear(roundTrip, point.X, point.Y, point.Z, 1e-12);
        }

        [Test]
        public void ComposeTranslatesChildThroughParentRotation()
        {
            Pose parent = new Pose(new Vec3(1.0, 2.0, 3.0), Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0));
            Pose child = new Pose(new Vec3(0.0, 0.0, -1.0), Quat.Identity);

            Pose composed = Pose.Compose(parent, child);
            AssertVecNear(composed.Position, 0.0, 2.0, 3.0, 1e-12);
            AssertQuatNear(composed.Rotation, HalfSqrt2, 0.0, HalfSqrt2, 0.0, 1e-12);
        }

        [Test]
        public void ComposeMultipliesParentAndChildRotations()
        {
            Pose parent = new Pose(new Vec3(0.0, 0.0, 0.0), Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0));
            Pose child = new Pose(new Vec3(0.0, 0.0, 0.0), Quat.FromAxisAngle(new Vec3(1.0, 0.0, 0.0), Pi / 2.0));

            Pose composed = Pose.Compose(parent, child);
            AssertVecNear(composed.Position, 0.0, 0.0, 0.0, 0.0);
            AssertQuatNear(composed.Rotation, 0.5, 0.5, 0.5, -0.5, 1e-15);
        }
    }
}
