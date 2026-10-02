using System;
using System.Globalization;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class VectorFloatTests
    {
        [Test]
        public void Vector3fStoresComponents()
        {
            var value = new Vector3f(1.5F, -2.25F, 3.0F);

            Assert.That(value.X, Is.EqualTo(1.5F));
            Assert.That(value.Y, Is.EqualTo(-2.25F));
            Assert.That(value.Z, Is.EqualTo(3.0F));
        }

        [Test]
        public void Vector3fZeroIsOrigin()
        {
            Assert.That(Vector3f.Zero.X, Is.EqualTo(0.0F));
            Assert.That(Vector3f.Zero.Y, Is.EqualTo(0.0F));
            Assert.That(Vector3f.Zero.Z, Is.EqualTo(0.0F));
            Assert.That(Vector3f.Zero, Is.EqualTo(default(Vector3f)));
        }

        [Test]
        public void Vector3fEqualityComparesComponentsExactly()
        {
            var value = new Vector3f(1.0F, 2.0F, 3.0F);

            Assert.That(value, Is.EqualTo(new Vector3f(1.0F, 2.0F, 3.0F)));
            Assert.That(value.Equals((object)new Vector3f(1.0F, 2.0F, 3.0F)), Is.True);
            Assert.That(value.Equals("not a vector"), Is.False);
            Assert.That(value.GetHashCode(), Is.EqualTo(new Vector3f(1.0F, 2.0F, 3.0F).GetHashCode()));
            Assert.That(value, Is.Not.EqualTo(new Vector3f(1.0F, 2.0F, 4.0F)));
        }

        [Test]
        public void Vector3fEqualityOperatorsMatchEquals()
        {
            var value = new Vector3f(1.0F, 2.0F, 3.0F);

            Assert.That(value == new Vector3f(1.0F, 2.0F, 3.0F), Is.True);
            Assert.That(value != new Vector3f(1.0F, 2.0F, 4.0F), Is.True);
            Assert.That(value != new Vector3f(1.0F, 2.0F, 3.0F), Is.False);
        }

        [Test]
        public void Vector3fToStringUsesInvariantCulture()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Assert.That(new Vector3f(0.5F, 2.0F, -3.25F).ToString(), Is.EqualTo("(0.5, 2, -3.25)"));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void Vector2fStoresComponents()
        {
            var value = new Vector2f(1.5F, -2.25F);

            Assert.That(value.X, Is.EqualTo(1.5F));
            Assert.That(value.Y, Is.EqualTo(-2.25F));
        }

        [Test]
        public void Vector2fZeroIsOrigin()
        {
            Assert.That(Vector2f.Zero.X, Is.EqualTo(0.0F));
            Assert.That(Vector2f.Zero.Y, Is.EqualTo(0.0F));
            Assert.That(Vector2f.Zero, Is.EqualTo(default(Vector2f)));
        }

        [Test]
        public void Vector2fEqualityComparesComponentsExactly()
        {
            var value = new Vector2f(1.0F, 2.0F);

            Assert.That(value, Is.EqualTo(new Vector2f(1.0F, 2.0F)));
            Assert.That(value.Equals((object)new Vector2f(1.0F, 2.0F)), Is.True);
            Assert.That(value.Equals("not a vector"), Is.False);
            Assert.That(value.GetHashCode(), Is.EqualTo(new Vector2f(1.0F, 2.0F).GetHashCode()));
            Assert.That(value == new Vector2f(1.0F, 2.0F), Is.True);
            Assert.That(value != new Vector2f(1.0F, 3.0F), Is.True);
        }

        [Test]
        public void Vector2fToStringUsesInvariantCulture()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Assert.That(new Vector2f(0.5F, -3.25F).ToString(), Is.EqualTo("(0.5, -3.25)"));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void FloatVectorOperationsAllocateNothing()
        {
            var a3 = new Vector3f(1.0F, 2.0F, 3.0F);
            var b3 = new Vector3f(4.0F, 5.0F, 6.0F);
            var a2 = new Vector2f(1.0F, 2.0F);
            var b2 = new Vector2f(3.0F, 4.0F);

            float warmup = RunOperations(a3, b3, a2, b2, 1_000);

            long before = GC.GetAllocatedBytesForCurrentThread();
            float sink = RunOperations(a3, b3, a2, b2, 100_000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(warmup, Is.GreaterThan(0.0F), "the warm-up must run the operations");
            Assert.That(sink, Is.GreaterThan(warmup), "the measured loop must run the operations");
            Assert.That(
                allocated,
                Is.EqualTo(0L),
                $"Float-vector operations allocated {allocated} bytes over 100,000 iterations");
        }

        private static float RunOperations(Vector3f a3, Vector3f b3, Vector2f a2, Vector2f b2, int iterations)
        {
            float sink = 0.0F;
            for (int i = 0; i < iterations; ++i)
            {
                Vector3f c3 = a3;
                sink += c3.X + c3.Y + c3.Z;
                sink += a3 == b3 ? 1.0F : 0.0F;
                sink += a3 != b3 ? 1.0F : 0.0F;
                sink += a3.Equals(b3) ? 1.0F : 0.0F;
                sink += a3.GetHashCode();
                sink += Vector3f.Zero.Y;

                Vector2f c2 = b2;
                sink += c2.X + c2.Y;
                sink += a2 == b2 ? 1.0F : 0.0F;
                sink += a2 != b2 ? 1.0F : 0.0F;
                sink += a2.Equals(b2) ? 1.0F : 0.0F;
                sink += a2.GetHashCode();
                sink += Vector2f.Zero.X;
            }

            return sink;
        }
    }
}
