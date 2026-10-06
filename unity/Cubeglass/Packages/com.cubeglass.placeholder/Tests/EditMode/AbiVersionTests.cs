using NUnit.Framework;

namespace Cubeglass.Placeholder.Tests
{
    public class AbiVersionTests
    {
        [Test]
        public void AbiVersion_IsThree()
        {
            Assert.AreEqual(3, AbiVersion.Value, "the Unity mirror must track CG_ABI_VERSION 3 (S8 5.3 port addition)");
        }
    }
}
