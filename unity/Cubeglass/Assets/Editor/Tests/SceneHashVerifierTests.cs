using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Cubeglass.Editor.Tests
{
    /// <summary>
    /// TD-025: the committed scene pins in <c>docs/notes/s7-gate.md</c> section
    /// 7 must be verifiable from the editor. The hash is taken over the scene
    /// bytes with CRLF normalised to LF, so a Windows checkout with
    /// <c>core.autocrlf</c> matches the committed git blob.
    /// </summary>
    public sealed class SceneHashVerifierTests
    {
        [Test]
        public void CommittedScenesMatchThePinnedHashes()
        {
            IReadOnlyList<SceneHashVerifier.SceneHashResult> results = SceneHashVerifier.VerifyAll();
            Assert.AreEqual(2, results.Count, "both committed scenes are pinned");
            foreach (SceneHashVerifier.SceneHashResult result in results)
            {
                Assert.IsTrue(
                    result.Match,
                    result.ScenePath + " does not match its pin.\nexpected: " + result.Expected
                        + "\nactual:   " + result.Actual
                        + "\nIf the rebuild was intentional, update the pin (recipe: docs/notes/s7-gate.md section 7.1).");
            }
        }

        [Test]
        public void ComputeHashNormalizesCrlfAndDetectsChanges()
        {
            byte[] lf = Encoding.UTF8.GetBytes("--- !u!1 &1\nGameObject:\n");
            byte[] crlf = Encoding.UTF8.GetBytes("--- !u!1 &1\r\nGameObject:\r\n");

            Assert.AreEqual(
                SceneHashVerifier.ComputeHash(lf),
                SceneHashVerifier.ComputeHash(crlf),
                "a CRLF checkout must hash the same as the LF blob");

            byte[] changed = Encoding.UTF8.GetBytes("--- !u!1 &1\nGameObject:\n  m_Layer: 1\n");
            Assert.AreNotEqual(
                SceneHashVerifier.ComputeHash(lf),
                SceneHashVerifier.ComputeHash(changed),
                "different content must not collide");
        }

        [Test]
        public void VerifySceneReportsAMissingFile()
        {
            SceneHashVerifier.SceneHashResult result = SceneHashVerifier.VerifyScene(
                "Assets/Scenes/DoesNotExist.unity", SceneHashVerifier.GameSceneSha256);

            Assert.IsFalse(result.Match, "a missing scene cannot match a pin");
            Assert.AreEqual("<missing>", result.Actual);
        }
    }
}