using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class QuatTests
    {
        // cos(pi/4), the component of a 90 degree rotation about a principal axis.
        private const double HalfSqrt2 = 0.7071067811865476;
        private const double Pi = 3.14159265358979323846;

        private static void AssertQuatNear(Quat q, double w, double x, double y, double z, double tolerance)
        {
            Assert.That(q.W, Is.EqualTo(w).Within(tolerance), "W");
            Assert.That(q.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(q.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(q.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        private static void AssertVecNear(Vec3 v, double x, double y, double z, double tolerance)
        {
            Assert.That(v.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(v.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(v.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        [Test]
        public void FromComponentsNormalizesAxisAligned()
        {
            Quat q = Quat.FromComponents(2.0, 0.0, 0.0, 0.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromComponentsNormalizesDiagonal()
        {
            Quat q = Quat.FromComponents(1.0, 1.0, 1.0, 1.0);
            Assert.That(q.W, Is.EqualTo(0.5));
            Assert.That(q.X, Is.EqualTo(0.5));
            Assert.That(q.Y, Is.EqualTo(0.5));
            Assert.That(q.Z, Is.EqualTo(0.5));
        }

        [Test]
        public void FromComponentsDegenerateIsIdentity()
        {
            Quat q = Quat.FromComponents(0.0, 0.0, 0.0, 0.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromComponentsHugeFiniteOverflowIsIdentity()
        {
            // The squared norm overflows to +Inf, which is treated as
            // degenerate. Pinned deliberately and mirrored by C++ (M6).
            Quat q = Quat.FromComponents(1e200, 1e200, 0.0, 0.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromAxisAngleHugeFiniteAxisOverflowIsIdentity()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(1e200, 1e200, 0.0), 1.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromComponentsNaNIsIdentity()
        {
            Quat q = Quat.FromComponents(double.NaN, 0.0, 0.0, 0.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromComponentsInfinityIsIdentity()
        {
            Quat q = Quat.FromComponents(0.0, 0.0, double.PositiveInfinity, 0.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromAxisAngleYaw90RotatesForwardToNegativeX()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);
            AssertQuatNear(q, HalfSqrt2, 0.0, HalfSqrt2, 0.0, 1e-15);

            Vec3 rotated = q.Rotate(new Vec3(0.0, 0.0, -1.0));
            AssertVecNear(rotated, -1.0, 0.0, 0.0, 1e-15);
        }

        [Test]
        public void FromAxisAngleZeroAxisIsIdentity()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(0.0, 0.0, 0.0), 1.0);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromAxisAngleNaNRadiansIsIdentity()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), double.NaN);
            Assert.That(q.W, Is.EqualTo(1.0));
            Assert.That(q.X, Is.EqualTo(0.0));
            Assert.That(q.Y, Is.EqualTo(0.0));
            Assert.That(q.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void FromAxisAngleInfiniteRadiansIsIdentity()
        {
            Quat positive = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), double.PositiveInfinity);
            Assert.That(positive.W, Is.EqualTo(1.0));
            Assert.That(positive.X, Is.EqualTo(0.0));
            Assert.That(positive.Y, Is.EqualTo(0.0));
            Assert.That(positive.Z, Is.EqualTo(0.0));

            Quat negative = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), double.NegativeInfinity);
            Assert.That(negative.W, Is.EqualTo(1.0));
            Assert.That(negative.X, Is.EqualTo(0.0));
            Assert.That(negative.Y, Is.EqualTo(0.0));
            Assert.That(negative.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void IdentityRotationLeavesVectorUnchanged()
        {
            Vec3 v = new Vec3(0.25, -1.5, 3.0);
            Vec3 rotated = Quat.Identity.Rotate(v);
            AssertVecNear(rotated, v.X, v.Y, v.Z, 0.0);
        }

        [Test]
        public void Roll180FlipsX()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(0.0, 0.0, 1.0), Pi);
            Vec3 rotated = q.Rotate(new Vec3(1.0, 0.0, 0.0));
            AssertVecNear(rotated, -1.0, 0.0, 0.0, 1e-15);
        }

        [Test]
        public void HamiltonProductKnownCase()
        {
            Quat a = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);
            Quat b = Quat.FromAxisAngle(new Vec3(1.0, 0.0, 0.0), Pi / 2.0);
            Quat product = a * b;
            AssertQuatNear(product, 0.5, 0.5, 0.5, -0.5, 1e-15);
        }

        [Test]
        public void InverseOfYaw90IsConjugate()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);
            AssertQuatNear(q.Inverse(), HalfSqrt2, 0.0, -HalfSqrt2, 0.0, 1e-15);
        }

        [Test]
        public void ProductWithConjugateIsIdentity()
        {
            Quat q = Quat.FromAxisAngle(new Vec3(1.0, 2.0, 3.0), 0.7);
            AssertQuatNear(q * q.Conjugate(), 1.0, 0.0, 0.0, 0.0, 1e-15);
        }

        [Test]
        public void SlerpMidpointIs22Point5Degrees()
        {
            Quat mid = Quat.Slerp(Quat.Identity, Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0), 0.5);
            AssertQuatNear(mid, 0.9238795325112867, 0.0, 0.3826834323650898, 0.0, 1e-12);
        }

        [Test]
        public void SlerpEndpointsAreExact()
        {
            Quat a = Quat.Identity;
            Quat b = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);
            AssertQuatNear(Quat.Slerp(a, b, 0.0), a.W, a.X, a.Y, a.Z, 1e-15);
            AssertQuatNear(Quat.Slerp(a, b, 1.0), b.W, b.X, b.Y, b.Z, 1e-15);
        }

        [Test]
        public void SlerpNearParallelUsesLinearInterpolation()
        {
            Quat a = Quat.Identity;
            Quat b = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), 1e-5);
            Assert.That(a.Dot(b), Is.GreaterThan(0.9995));

            Quat mid = Quat.Slerp(a, b, 0.5);
            AssertQuatNear(mid, 0.999999999996875, 0.0, 2.4999999999973958e-6, 0.0, 1e-12);
            Assert.That(mid.IsNormalized(1e-9), Is.True);
        }

        [Test]
        public void SlerpIdenticalPairReturnsSameRotation()
        {
            Quat a = Quat.FromAxisAngle(new Vec3(1.0, 2.0, 3.0), 0.7);

            Quat mid = Quat.Slerp(a, a, 0.5);
            AssertQuatNear(mid, a.W, a.X, a.Y, a.Z, 1e-12);
            Assert.That(mid.IsNormalized(1e-9), Is.True);
        }

        [Test]
        public void SlerpOppositeQuaternionsTakesShortestPath()
        {
            Quat a = Quat.Identity;
            Quat b = Quat.FromComponents(-1.0, 0.0, 0.0, 0.0);

            Quat mid = Quat.Slerp(a, b, 0.5);
            AssertQuatNear(mid, 1.0, 0.0, 0.0, 0.0, 1e-15);
            Assert.That(mid.IsNormalized(1e-9), Is.True);

            AssertQuatNear(Quat.Slerp(a, b, 1.0), 1.0, 0.0, 0.0, 0.0, 1e-15);
        }

        [Test]
        public void IsNormalizedReportsUnitQuaternions()
        {
            Assert.That(Quat.Identity.IsNormalized(1e-9), Is.True);
            Assert.That(Quat.FromComponents(1.0, 1.0, 1.0, 1.0).IsNormalized(1e-9), Is.True);
            Assert.That(Quat.FromAxisAngle(new Vec3(1.0, 2.0, 3.0), 0.7).IsNormalized(1e-9), Is.True);
        }
    }
}
