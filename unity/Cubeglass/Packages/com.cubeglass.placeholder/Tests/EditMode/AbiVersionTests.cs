using NUnit.Framework;

namespace Cubeglass.Placeholder.Tests
{
    public class AbiVersionTests
    {
        [Test]
        public void AbiVersion_IsTwo()
        {
            Assert.AreEqual(2, AbiVersion.Value, "the Unity mirror must track CG_ABI_VERSION 2 (ADR-0010)");
        }
    }
}
