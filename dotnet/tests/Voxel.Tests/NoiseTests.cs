using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class NoiseTests
    {
        [Test]
        public void Value2DDoesNotWrapTheLatticeAtTheIntegerEdge()
        {
            for (long seed = 0; seed < 8; seed++)
            {
                double edge = Noise.Value2D(int.MaxValue, 0.5, seed);
                Assert.That(Noise.Value2D(2147483648.0, 0.5, seed), Is.EqualTo(edge), $"seed {seed}");
                Assert.That(Noise.Value2D(2147483649.5, 0.5, seed), Is.EqualTo(edge), $"seed {seed}");

                double edgeY = Noise.Value2D(0.5, int.MaxValue, seed);
                Assert.That(Noise.Value2D(0.5, 2147483648.5, seed), Is.EqualTo(edgeY), $"seed {seed}");
            }
        }
    }
}
