using System;
using System.IO;
using NUnit.Framework;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Review M-11: world names are single directory names under the save root,
    /// so names Windows cannot create (reserved device names, trailing dots or
    /// spaces) must be rejected at construction instead of aliasing or failing
    /// later. World names are case-insensitive on Windows/macOS
    /// (<c>Default</c> and <c>default</c> are the same directory), which the
    /// store documents rather than duplicating here.
    /// </summary>
    public class FileWorldStoreEditModeTests
    {
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase("name.")]
        [TestCase("name ")]
        [TestCase("CON")]
        [TestCase("con")]
        [TestCase("CON.txt")]
        [TestCase("NUL")]
        [TestCase("LPT1")]
        [TestCase("COM9.log")]
        [TestCase("AUX")]
        [TestCase("PRN")]
        public void RejectsUnsafeWorldNames(string worldName)
        {
            Assert.Throws<ArgumentException>(() => new FileWorldStore(worldName));
        }

        [Test]
        public void AcceptsAnOrdinaryNameAndCreatesTheWorldDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "cg-store-name-" + Guid.NewGuid().ToString("N"));
            var store = new FileWorldStore("Default-2", root);
            try
            {
                Assert.IsTrue(Directory.Exists(store.WorldDirectory), "the world directory is created");
            }
            finally
            {
                store.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
