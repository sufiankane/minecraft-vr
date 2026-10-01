using System;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    // Property loops are deterministic: Random(20261001) and 10_000 iterations per property,
    // mirroring the C++ rapidcheck properties. FsCheck-based property tests arrive with
    // stage S2; this file deliberately has no FsCheck dependency yet.
    [TestFixture]
    public sealed class PropertyTests
    {
        private const int Seed = 20261001;
        private const int Iterations = 10_000;
        private const double Tolerance = 1e-9;

        [Test]
        public void FromComponentsIsNormalizedAndFinite()
        {
            Random random = new Random(Seed);

            for (int i = 0; i < Iterations; i++)
            {
                Quat q = Quat.FromComponents(
                    NextComponent(random),
                    NextComponent(random),
                    NextComponent(random),
                    NextComponent(random));

                Assert.That(q.IsNormalized(Tolerance), Is.True, $"iteration {i}");
                Assert.That(double.IsFinite(q.W), Is.True, $"iteration {i}");
                Assert.That(double.IsFinite(q.X), Is.True, $"iteration {i}");
                Assert.That(double.IsFinite(q.Y), Is.True, $"iteration {i}");
                Assert.That(double.IsFinite(q.Z), Is.True, $"iteration {i}");
            }
        }

        [Test]
        public void RotatePreservesLength()
        {
            Random random = new Random(Seed);

            for (int i = 0; i < Iterations; i++)
            {
                Quat q = NextQuat(random);
                Vec3 v = NextVec3(random);

                double before = Vec3.Length(v);
                double after = Vec3.Length(q.Rotate(v));
                Assert.That(Math.Abs(after - before), Is.LessThanOrEqualTo(Tolerance), $"iteration {i}");
            }
        }

        [Test]
        public void ProductWithConjugateIsIdentity()
        {
            Random random = new Random(Seed);

            for (int i = 0; i < Iterations; i++)
            {
                Quat q = NextQuat(random);
                Quat product = q * q.Conjugate();

                Assert.That(Math.Abs(product.W - 1.0), Is.LessThanOrEqualTo(Tolerance), $"iteration {i}");
                Assert.That(Math.Abs(product.X), Is.LessThanOrEqualTo(Tolerance), $"iteration {i}");
                Assert.That(Math.Abs(product.Y), Is.LessThanOrEqualTo(Tolerance), $"iteration {i}");
                Assert.That(Math.Abs(product.Z), Is.LessThanOrEqualTo(Tolerance), $"iteration {i}");
            }
        }

        [Test]
        public void NormalizeProducesUnitQuaternion()
        {
            Random random = new Random(Seed);

            for (int i = 0; i < Iterations; i++)
            {
                double w = NextComponent(random);
                double x = NextComponent(random);
                double y = NextComponent(random);
                double z = NextComponent(random);
                double squaredNorm = (w * w) + (x * x) + (y * y) + (z * z);
                if (squaredNorm < 1e-24)
                {
                    continue;
                }

                Quat normalised = Quat.Normalize(Quat.FromComponents(w, x, y, z));
                Assert.That(normalised.IsNormalized(Tolerance), Is.True, $"iteration {i}");
            }
        }

        [Test]
        public void SlerpEndpointsMatchInputs()
        {
            Random random = new Random(Seed);

            for (int i = 0; i < Iterations; i++)
            {
                Quat a = NextQuat(random);
                Quat b = NextQuat(random);

                Quat start = Quat.Slerp(a, b, 0.0);
                Assert.That(Math.Abs(start.W - a.W), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(start.X - a.X), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(start.Y - a.Y), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(start.Z - a.Z), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");

                // Slerp takes the shortest path, so a negative dot flips the end quaternion.
                Quat shortestEnd = a.Dot(b) < 0.0 ? Quat.FromComponents(-b.W, -b.X, -b.Y, -b.Z) : b;
                Quat end = Quat.Slerp(a, b, 1.0);
                Assert.That(Math.Abs(end.W - shortestEnd.W), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(end.X - shortestEnd.X), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(end.Y - shortestEnd.Y), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
                Assert.That(Math.Abs(end.Z - shortestEnd.Z), Is.LessThanOrEqualTo(1e-12), $"iteration {i}");
            }
        }

        private static double NextComponent(Random random)
        {
            return (random.NextDouble() * 4.0) - 2.0;
        }

        private static Quat NextQuat(Random random)
        {
            return Quat.FromComponents(
                NextComponent(random),
                NextComponent(random),
                NextComponent(random),
                NextComponent(random));
        }

        private static Vec3 NextVec3(Random random)
        {
            return new Vec3(NextComponent(random), NextComponent(random), NextComponent(random));
        }
    }
}
