using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class AbiVersionTests
    {
        [Test]
        public void ValueIsOne()
        {
            Assert.That(AbiVersion.Value, Is.EqualTo(1));
        }
    }
}
