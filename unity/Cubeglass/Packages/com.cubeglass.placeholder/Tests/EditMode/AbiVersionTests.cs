using NUnit.Framework;

namespace Cubeglass.Placeholder.Tests
{
    public class AbiVersionTests
    {
        [Test]
        public void AbiVersion_IsOne()
        {
            Assert.AreEqual(1, AbiVersion.Value);
        }
    }
}
