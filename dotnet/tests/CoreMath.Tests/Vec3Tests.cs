using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class Vec3Tests
    {
        [Test]
        public void DotHandValue()
        {
            Assert.That(Vec3.Dot(new Vec3(1.0, 2.0, 3.0), new Vec3(4.0, 5.0, 6.0)), Is.EqualTo(32.0));
        }

        [Test]
        public void CrossHandValue()
        {
            Vec3 result = Vec3.Cross(new Vec3(1.0, 2.0, 3.0), new Vec3(4.0, 5.0, 6.0));
            Assert.That(result.X, Is.EqualTo(-3.0));
            Assert.That(result.Y, Is.EqualTo(6.0));
            Assert.That(result.Z, Is.EqualTo(-3.0));
        }

        [Test]
        public void CrossBasisVectors()
        {
            Vec3 result = Vec3.Cross(new Vec3(1.0, 0.0, 0.0), new Vec3(0.0, 1.0, 0.0));
            Assert.That(result.X, Is.EqualTo(0.0));
            Assert.That(result.Y, Is.EqualTo(0.0));
            Assert.That(result.Z, Is.EqualTo(1.0));
        }

        [Test]
        public void ArithmeticOperators()
        {
            Vec3 a = new Vec3(1.0, 2.0, 3.0);
            Vec3 b = new Vec3(4.0, 5.0, 6.0);

            Vec3 sum = a + b;
            Assert.That(sum.X, Is.EqualTo(5.0));
            Assert.That(sum.Y, Is.EqualTo(7.0));
            Assert.That(sum.Z, Is.EqualTo(9.0));

            Vec3 difference = b - a;
            Assert.That(difference.X, Is.EqualTo(3.0));
            Assert.That(difference.Y, Is.EqualTo(3.0));
            Assert.That(difference.Z, Is.EqualTo(3.0));

            Vec3 negated = -a;
            Assert.That(negated.X, Is.EqualTo(-1.0));
            Assert.That(negated.Y, Is.EqualTo(-2.0));
            Assert.That(negated.Z, Is.EqualTo(-3.0));

            Vec3 rightScaled = a * 2.0;
            Assert.That(rightScaled.X, Is.EqualTo(2.0));
            Assert.That(rightScaled.Y, Is.EqualTo(4.0));
            Assert.That(rightScaled.Z, Is.EqualTo(6.0));

            Vec3 leftScaled = 2.0 * a;
            Assert.That(leftScaled.X, Is.EqualTo(2.0));
            Assert.That(leftScaled.Y, Is.EqualTo(4.0));
            Assert.That(leftScaled.Z, Is.EqualTo(6.0));

            Vec3 halved = b / 2.0;
            Assert.That(halved.X, Is.EqualTo(2.0));
            Assert.That(halved.Y, Is.EqualTo(2.5));
            Assert.That(halved.Z, Is.EqualTo(3.0));
        }

        [Test]
        public void LengthHandValue()
        {
            Assert.That(Vec3.Length(new Vec3(3.0, 4.0, 12.0)), Is.EqualTo(13.0));
        }

        [Test]
        public void NormalizedHasUnitLength()
        {
            Vec3 normalised = Vec3.Normalized(new Vec3(1.0, 2.0, 3.0));
            Assert.That(Vec3.Length(normalised), Is.EqualTo(1.0).Within(1e-12));
        }

        [Test]
        public void NormalizedZeroStaysZero()
        {
            Vec3 normalised = Vec3.Normalized(new Vec3(0.0, 0.0, 0.0));
            Assert.That(normalised.X, Is.EqualTo(0.0));
            Assert.That(normalised.Y, Is.EqualTo(0.0));
            Assert.That(normalised.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void NearlyEqualsToleranceBoundaries()
        {
            Assert.That(new Vec3(1.0, 2.0, 3.0).NearlyEquals(new Vec3(1.0, 2.0, 3.0), 0.0), Is.True);
            Assert.That(new Vec3(0.0, 0.0, 0.0).NearlyEquals(new Vec3(1e-3, 0.0, 0.0), 1e-3), Is.True);
            Assert.That(new Vec3(0.0, 0.0, 0.0).NearlyEquals(new Vec3(1.0000001e-3, 0.0, 0.0), 1e-3), Is.False);
            Assert.That(new Vec3(0.0, 0.0, 0.0).NearlyEquals(new Vec3(0.0, 0.0, 1e-3), 1e-3), Is.True);
            Assert.That(new Vec3(0.0, 0.0, 0.0).NearlyEquals(new Vec3(0.0, 1e-3, 0.0), 1e-12), Is.False);
        }

        [Test]
        public void EqualityComparesComponentsExactly()
        {
            Assert.That(new Vec3(1.0, 2.0, 3.0), Is.EqualTo(new Vec3(1.0, 2.0, 3.0)));
            Assert.That(new Vec3(1.0, 2.0, 3.0) == new Vec3(1.0, 2.0, 3.0), Is.True);
            Assert.That(new Vec3(1.0, 2.0, 3.0) != new Vec3(1.0, 2.0, 4.0), Is.True);
        }
    }
}
